// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#ifndef _RUNECLR_DEADLINE_H
#define _RUNECLR_DEADLINE_H

#include <atomic>

// Private RuneCLR experiment: opt-in callback deadline for interpreted author code.
//
// This is the epoch-only subset of the sealed 3126 callback-deadline experiment
// (runeclr/callback-epoch-20261110): the process-global epoch counter and period
// latch, the clock/epoch budget comparison, the termination phase latch and the
// bounded cleanup budget. The instruction meter, the batched/derived admission
// ledgers and the inline/healthy dispatch variants are deliberately not part of
// this file. Nothing here runs unless a trusted host configures a deadline on a
// custom (interpreter-required) binder, and the runtime owns no thread and no
// timer: a frozen or absent ticker means the epoch never advances, which is a
// liveness gap, not a global preemption guarantee.
//
// One instance is embedded in each CustomAssemblyBinder. The epoch counter itself
// is process-wide because the trusted host owns the only ticker.

extern std::atomic<INT64> g_runeCLREpoch;
extern std::atomic<INT64> g_runeCLREpochPeriodMicros;

// Diagnostic latches: read-only host information that never gates execution.
// FuncletSeen: bit0 = finally/fault funclet entered, bit1 = catch/filter funclet entered.
// Trace: bit0 dispatch had clause args, bit1 clause args were cleanup, bit2 deny observed,
// bit3 bounded-cleanup branch taken, bit4 bounded-cleanup granted, bit5 scope saw cleanup,
// bit6 scope set the owner, bit7 scope found a deadline that was not enabled.
extern std::atomic<LONG> g_runeCLRDeadlineFuncletSeen;
extern std::atomic<LONG> g_runeCLRDeadlineTrace;

inline void RuneCLRDeadlineTraceBit(LONG bit) noexcept
{
    g_runeCLRDeadlineTrace.fetch_or(bit, std::memory_order_relaxed);
}

// Process-sticky gate: 0 until some binder configures a deadline. While it is 0 the
// interpreter checkpoint is one relaxed load and one never-taken branch, so a process
// that never opts in keeps the unpatched interpreter path. It is never cleared.
extern std::atomic<LONG> g_runeCLRDeadlineConfigured;

FORCEINLINE bool RuneCLRAnyDeadlineConfigured() noexcept
{
    return g_runeCLRDeadlineConfigured.load(std::memory_order_relaxed) != 0;
}

// Host-declared period, latched process-wide to the first declared value; repeating the same
// value is idempotent and a different value is rejected by the caller (qcall op 6 maps it to
// ArgumentOutOfRangeException).
inline bool RuneCLRDeclareEpochPeriod(INT64 periodMicros) noexcept
{
    if (periodMicros <= 0 || periodMicros > 1000000)
        return false;

    INT64 current = g_runeCLREpochPeriodMicros.load(std::memory_order_acquire);
    if (current != 0)
        return current == periodMicros;

    INT64 expected = 0;
    if (g_runeCLREpochPeriodMicros.compare_exchange_strong(expected, periodMicros, std::memory_order_acq_rel))
        return true;
    return expected == periodMicros;
}

// Advance the shared epoch by one tick. Never performs an overflowing signed add and never
// exposes a wrapped (negative) value: a saturated or already-corrupt counter is pinned to
// INT64_MAX, which makes every epoch deadline fail closed instead of becoming unlimited.
inline INT64 RuneCLRTickEpoch() noexcept
{
    INT64 previous = g_runeCLREpoch.load(std::memory_order_acquire);
    for (;;)
    {
        if (previous < 0 || previous == INT64_MAX)
        {
            INT64 expected = previous;
            g_runeCLREpoch.compare_exchange_strong(expected, INT64_MAX, std::memory_order_acq_rel);
            return INT64_MAX;
        }

        INT64 next = previous + 1;
        if (g_runeCLREpoch.compare_exchange_weak(previous, next, std::memory_order_acq_rel, std::memory_order_acquire))
            return next;
    }
}

#ifdef TARGET_UNIX
#include <time.h>
#endif

// Per-binder callback deadline state. Dispatch is the only hot reader:
//   * unconfigured: Enabled() is false and the interpreter keeps its normal path;
//   * configured: one owner thread at a time polls the budget every N admissions
//     plus at every frame/safe-opcode boundary, and latches the first failure;
//   * terminated: the body throws, and only an actual finally/fault funclet is
//     granted the bounded cleanup budget before remaining cleanup is abandoned.
class RuneCLRDeadline
{
public:
    enum : LONG
    {
        PhaseExecution = 0,
        PhaseTerminated = 1,
        PhaseCleanupExhausted = 2,
    };

    bool Enabled() const noexcept { return m_configured; }
    bool IsEpochMode() const noexcept { return m_epochMode; }
    INT32 Interval() const noexcept { return m_interval; }
    LONG Failure() const noexcept { return VolatileLoad(&m_failure); }
    LONG Phase() const noexcept { return VolatileLoad(&m_phase); }
    bool IsTerminated() const noexcept { return Phase() != PhaseExecution; }
    LONG Reason() const noexcept { return Failure() != 0 ? Failure() : (IsTerminated() ? 1 : 0); }

    // Cleanup allowance in opcodes, set before the deadline is enabled. Zero means
    // "no author cleanup may run after termination".
    bool SetCleanupLimit(INT64 limit) noexcept
    {
        if (m_configured || limit < 0)
            return false;
        m_cleanupRemaining = limit;
        return true;
    }

    // Clock mode: the deadline is measured with the monotonic clock.
    bool Configure(INT64 duration100ns, INT32 interval) noexcept
    {
        if (m_configured || duration100ns <= 0 || (interval != 1 && interval != 100 && interval != 1000))
            return false;

        INT64 frequency;
        if (!Frequency(frequency))
            return false;

        const INT64 denominator = 10000000;
        INT64 seconds = duration100ns / denominator;
        INT64 remainder = duration100ns % denominator;
        if (seconds > INT64_MAX / frequency || remainder > (INT64_MAX - denominator + 1) / frequency)
            return false;

        INT64 fractional = (remainder * frequency + denominator - 1) / denominator;
        INT64 whole = seconds * frequency;
        if (whole > INT64_MAX - fractional || whole + fractional <= 0)
            return false;

        m_frequency = frequency;
        m_duration = whole + fractional;
        m_interval = interval;
        m_configured = true;
        g_runeCLRDeadlineConfigured.store(1, std::memory_order_release);
        return true;
    }

    // Opt-in epoch mode: the deadline is measured in host-declared epoch ticks instead of a
    // per-poll clock read. Start is captured at an arbitrary phase inside the current tick, so
    // the budget is ceil(duration/period) + 1: the fixed phase allowance guarantees the
    // deadline can never fire before the requested duration has elapsed. Worst-case lateness
    // is therefore < 2 declared periods plus ticker scheduling and poll-interval delays.
    bool ConfigureEpoch(INT64 duration100ns, INT32 interval) noexcept
    {
        if (m_configured || duration100ns <= 0 || (interval != 1 && interval != 100 && interval != 1000))
            return false;

        INT64 periodMicros = g_runeCLREpochPeriodMicros.load(std::memory_order_acquire);
        if (periodMicros <= 0 || periodMicros > 1000000)
            return false;

        // One declared period is periodMicros * 10 in 100ns units.
        const INT64 denominator = 10000000;
        INT64 ticksPerPeriod = periodMicros * 10;
        INT64 whole = duration100ns / ticksPerPeriod;
        INT64 remainder = duration100ns % ticksPerPeriod;
        if (remainder != 0)
            ++whole;
        if (whole <= 0 || whole > INT64_MAX - 2)
            return false;

        m_budgetTicks = whole + 1;
        m_interval = interval;
        m_epochMode = true;
        m_configured = true;
        g_runeCLRDeadlineConfigured.store(1, std::memory_order_release);
        return true;
    }

    INT64 Begin(void* owner) noexcept
    {
        if (!m_configured || Failure() != 0 || InterlockedCompareExchange(&m_active, -1, 0) != 0)
        {
            Fail(4);
            return 0;
        }

        if (m_token == INT64_MAX)
        {
            Fail(3);
            InterlockedExchange(&m_active, 0);
            return 0;
        }

        if (m_epochMode)
        {
            // Fail closed when no trusted ticker has advanced the shared epoch yet, and when
            // the counter is saturated: a saturated counter can never expire a new callback.
            INT64 epoch = g_runeCLREpoch.load(std::memory_order_acquire);
            if (epoch <= 0 || epoch == INT64_MAX)
            {
                Fail(3);
                InterlockedExchange(&m_active, 0);
                return 0;
            }
            m_startEpoch = epoch;
            m_lastEpoch = epoch;
        }
        else
        {
            if (!Now(m_start))
            {
                Fail(3);
                InterlockedExchange(&m_active, 0);
                return 0;
            }
            m_last = m_start;
        }

        m_owner.store(owner, std::memory_order_release);
        m_pending = 0;
        ++m_token;
        InterlockedExchange(&m_active, 1);
        return m_token;
    }

    bool End(void* owner, INT64 token) noexcept
    {
        if (VolatileLoad(&m_active) != 1 || owner != m_owner.load(std::memory_order_acquire) || token != m_token)
        {
            Fail(4);
            return false;
        }

        bool result = Poll(owner);
        InterlockedExchange(&m_active, 0);
        return result;
    }

    // One admission. Called for every opcode of a deadline-configured binder; the poll itself
    // runs at boundaries and every m_interval admissions.
    FORCEINLINE bool Check(void* owner, bool boundary) noexcept
    {
        if (Failure() != 0)
            return false;

        if (VolatileLoad(&m_active) != 1 || owner != m_owner.load(std::memory_order_acquire))
        {
            Fail(4);
            return false;
        }

        if (boundary || ++m_pending >= m_interval)
        {
            m_pending = 0;
            if (!Poll(owner))
                return false;
        }

        // The active owner is the only writer; this counter is a diagnostic snapshot,
        // not a quota, so it must not cost a locked read-modify-write per opcode.
        m_admitted = m_admitted + 1;
        return true;
    }

    // Bounded cleanup: only a finally/fault funclet of a terminated callback reaches this.
    // When the allowance is exhausted the phase is latched and the remaining author cleanup
    // is abandoned rather than run unbounded.
    FORCEINLINE bool TryChargeCleanup(void* owner) noexcept
    {
        if (!m_configured || Phase() != PhaseTerminated)
            return false;

        if (owner != m_owner.load(std::memory_order_acquire))
        {
            Fail(4);
            return false;
        }

        LONGLONG previous = InterlockedCompareExchange64(&m_cleanupRemaining, 0, 0);
        for (;;)
        {
            if (previous <= 0)
            {
                InterlockedCompareExchange(&m_phase, PhaseCleanupExhausted, PhaseTerminated);
                return false;
            }

            LONGLONG observed = InterlockedCompareExchange64(&m_cleanupRemaining, previous - 1, previous);
            if (observed == previous)
            {
                ++m_cleanupCharges;
                return true;
            }
            previous = observed;
        }
    }

    // Diagnostic fields read by the trusted host. Unknown fields return -1, matching the
    // baseline version-1 contract; fields 8..10 are additive epoch information.
    INT64 Query(INT32 field) const noexcept
    {
        switch (field)
        {
            case 0: return m_frequency;
            case 1: return InterlockedCompareExchange64(&m_checks, 0, 0);
            case 2: return InterlockedCompareExchange64(&m_admitted, 0, 0);
            case 3: return VolatileLoad(&m_active);
            case 4: return m_interval;
            case 5: return m_token;
            case 6: return Failure();
            case 8: return m_epochMode ? 1 : 0;
            case 9: return m_budgetTicks;
            case 10: return g_runeCLREpoch.load(std::memory_order_acquire);
            case 11: return InterlockedCompareExchange64(&m_cleanupRemaining, 0, 0);
            case 12: return m_cleanupCharges;
            case 13: return g_runeCLRDeadlineFuncletSeen.load(std::memory_order_acquire);
            case 14: return g_runeCLRDeadlineTrace.load(std::memory_order_acquire);
            case 15: return m_startEpoch;
            case 16: return m_lastEpoch;
            default: return -1;
        }
    }

private:
    void Fail(LONG reason) noexcept
    {
        // Any failure terminates the callback: the body is denied and only an actual
        // finally/fault funclet may still spend the bounded cleanup budget.
        InterlockedCompareExchange(&m_failure, reason, 0);
        if (VolatileLoad(&m_phase) == PhaseExecution)
            InterlockedCompareExchange(&m_phase, PhaseTerminated, PhaseExecution);
    }

    FORCEINLINE bool Poll(void* owner) noexcept
    {
        if (Failure() != 0)
            return false;

        if (owner != m_owner.load(std::memory_order_acquire))
        {
            Fail(4);
            return false;
        }

        m_checks = m_checks + 1;
        if (m_epochMode)
        {
            // No clock call on this path: one atomic load, monotonic check, budget compare.
            INT64 epoch = g_runeCLREpoch.load(std::memory_order_acquire);
            // Saturation is an irreversible invariant failure, not "no progress yet".
            if (epoch == INT64_MAX)
            {
                Fail(3);
                return false;
            }
            if (epoch < m_lastEpoch || epoch < m_startEpoch)
            {
                Fail(3);
                return false;
            }

            m_lastEpoch = epoch;
            if (epoch - m_startEpoch >= m_budgetTicks)
            {
                Fail(2);
                return false;
            }
            return true;
        }

        INT64 now;
        if (!Now(now) || now < m_last || now < m_start)
        {
            Fail(3);
            return false;
        }

        m_last = now;
        if (now - m_start >= m_duration)
        {
            Fail(2);
            return false;
        }
        return true;
    }

    static bool Frequency(INT64& frequency) noexcept
    {
#ifdef TARGET_WINDOWS
        LARGE_INTEGER value;
        if (!QueryPerformanceFrequency(&value) || value.QuadPart <= 0)
            return false;
        frequency = value.QuadPart;
#else
        frequency = 1000000000;
#endif
        return true;
    }

    static bool Now(INT64& ticks) noexcept
    {
#ifdef TARGET_WINDOWS
        LARGE_INTEGER value;
        if (!QueryPerformanceCounter(&value) || value.QuadPart < 0)
            return false;
        ticks = value.QuadPart;
#else
        struct timespec value;
#ifdef __APPLE__
        const clockid_t clock = CLOCK_UPTIME_RAW;
#else
        const clockid_t clock = CLOCK_MONOTONIC;
#endif
        if (clock_gettime(clock, &value) != 0 || value.tv_sec < 0 || value.tv_nsec < 0 || value.tv_nsec >= 1000000000 ||
            value.tv_sec > (INT64_MAX - value.tv_nsec) / 1000000000)
            return false;
        ticks = value.tv_sec * INT64_C(1000000000) + value.tv_nsec;
#endif
        return true;
    }

    INT32 m_interval = 0;
    bool m_configured = false;
    bool m_epochMode = false;
    volatile LONG m_failure = 0;
    volatile LONG m_active = 0;
    mutable volatile LONG m_phase = PhaseExecution;
    std::atomic<void*> m_owner{nullptr};
    static_assert(std::atomic<void*>::is_always_lock_free);
    INT64 m_frequency = 0;
    INT64 m_duration = 0;
    INT64 m_start = 0;
    INT64 m_last = 0;
    INT64 m_token = 0;
    INT32 m_pending = 0;
    INT64 m_budgetTicks = 0;
    INT64 m_startEpoch = 0;
    INT64 m_lastEpoch = 0;
    alignas(8) mutable volatile LONGLONG m_checks = 0;
    alignas(8) mutable volatile LONGLONG m_admitted = 0;
    alignas(8) mutable volatile LONGLONG m_cleanupRemaining = 0;
    LONGLONG m_cleanupCharges = 0;
};

// Resolved "this frame's binder has no deadline" marker. Keeping a single pointer per
// frame instead of a pointer plus a resolved flag keeps InterpMethodContextFrame at
// 56 bytes rather than 64. It is never configured and never handed to a host.
extern RuneCLRDeadline g_runeCLRDeadlineNone;

#endif // _RUNECLR_DEADLINE_H