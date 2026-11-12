#ifndef _RUNECLR_DEADLINE_H
#define _RUNECLR_DEADLINE_H

#include <atomic>

// Process-global monotonic epoch for the opt-in epoch deadline mode. It is advanced only by
// the trusted host bridge (AssemblyNative_RuneCLRDeadline op 7) and is never writable from
// managed or author code. Each session keeps its own start/budget; the counter is shared.
extern std::atomic<INT64> g_runeCLREpoch;
// Host-declared epoch tick period in microseconds. 0 means "not declared yet"; the value is
// latched process-wide so every session uses the same rounding basis.
extern std::atomic<INT64> g_runeCLREpochPeriodMicros;

// Host-declared period: process-global, latched to the first value; repeating the same value is
// idempotent, a different value is rejected by the caller (qcall op 6 maps it to ArgumentOutOfRange).
inline bool RuneCLRDeclareEpochPeriod(INT64 periodMicros) noexcept
{
    if (periodMicros <= 0 || periodMicros > 1000000) return false;
    INT64 current = g_runeCLREpochPeriodMicros.load(std::memory_order_acquire);
    if (current != 0) return current == periodMicros;
    INT64 expected = 0;
    if (g_runeCLREpochPeriodMicros.compare_exchange_strong(expected, periodMicros, std::memory_order_acq_rel))
        return true;
    return expected == periodMicros;
}

// Advance the shared epoch by one tick. Never performs an overflowing signed add and never exposes
// a wrapped (negative) value: a saturated or already-corrupt counter is pinned to INT64_MAX, which
// makes every epoch deadline fail closed instead of becoming unlimited.
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

class RuneCLRDeadline
{
public:
    enum class QueryStatus { Success, ForeignActive, Transitioning, ReservationRace, InvalidField, InvariantFailure };
    void FlushOwner(void* owner) noexcept
    {
        if (!m_diagBatch || VolatileLoad(&m_active) != 1 || owner != m_owner.load(std::memory_order_acquire)) return;
        UINT32 delta = m_diagPending;
        if (delta != 0)
        {
            InterlockedExchangeAdd64(&m_admitted, static_cast<LONGLONG>(delta));
            m_diagPending = 0;
        }
    }
    QueryStatus TryQuery(void* owner, INT32 field, INT64& value) noexcept
    {
        if (field < 0 || field > 10) return QueryStatus::InvalidField;
        if (!m_diagBatch) { value = field == 7 ? 1 : Query(field); return QueryStatus::Success; }
        LONG active = VolatileLoad(&m_active);
        if (active == 1)
        {
            if (owner != m_owner.load(std::memory_order_acquire)) return QueryStatus::ForeignActive;
            if (m_diagPending > static_cast<UINT32>(m_interval)) return QueryStatus::InvariantFailure;
            FlushOwner(owner);
            value = field == 7 ? 2 : Query(field);
            return QueryStatus::Success;
        }
        if (active != 0) return QueryStatus::Transitioning;
        if (InterlockedCompareExchange(&m_active, -2, 0) != 0) return QueryStatus::ReservationRace;
        value = field == 3 ? 0 : field == 7 ? 2 : Query(field);
        InterlockedExchange(&m_active, 0);
        return QueryStatus::Success;
    }
    bool Enabled() const noexcept { return m_interval != 0; }
    LONG Failure() const noexcept { return VolatileLoad(&m_failure); }
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
            default: return -1;
        }
    }
    bool Configure(INT64 duration100ns, INT32 interval) noexcept
    {
        if (Enabled() || duration100ns <= 0 || (interval != 1 && interval != 100 && interval != 1000))
            return false;
        INT64 frequency;
        if (!Frequency(frequency)) return false;
        const INT64 denominator = 10000000;
        INT64 seconds = duration100ns / denominator;
        INT64 remainder = duration100ns % denominator;
        if (seconds > INT64_MAX / frequency || remainder > (INT64_MAX - denominator + 1) / frequency)
            return false;
        INT64 fractional = (remainder * frequency + denominator - 1) / denominator;
        INT64 whole = seconds * frequency;
        if (whole > INT64_MAX - fractional || whole + fractional <= 0) return false;
        m_frequency = frequency;
        m_duration = whole + fractional;
        m_interval = interval;
        return true;
    }
    // Opt-in epoch mode: the deadline is measured in host-declared epoch ticks instead of a
    // per-poll clock read. Start is captured at an arbitrary phase inside the current tick, so
    // the budget is ceil(duration/period) + 1: the fixed phase allowance guarantees the
    // deadline can never fire before the requested duration has elapsed. Worst-case lateness
    // is therefore < 2 declared periods plus ticker scheduling and poll-interval delays.
    bool ConfigureEpoch(INT64 duration100ns, INT32 interval) noexcept
    {
        if (Enabled() || duration100ns <= 0 || (interval != 1 && interval != 100 && interval != 1000))
            return false;
        INT64 periodMicros = g_runeCLREpochPeriodMicros.load(std::memory_order_acquire);
        if (periodMicros <= 0 || periodMicros > 1000000) return false;
        INT64 denominator = periodMicros * 10; // 100ns units per declared tick
        if (duration100ns > INT64_MAX - denominator) return false;
        INT64 whole = (duration100ns + denominator - 1) / denominator;
        if (whole <= 0 || whole > INT64_MAX - 2) return false;
        m_budgetTicks = whole + 1;
        m_interval = interval;
        m_epochMode = true;
        return true;
    }
    INT64 Begin(void* owner) noexcept
    {
        if (!Enabled() || Failure() != 0 || InterlockedCompareExchange(&m_active, -1, 0) != 0)
        { Fail(4); return 0; }
        if (m_diagBatch && m_diagPending != 0)
        { Fail(3); InterlockedExchange(&m_active, 0); return 0; }
        if (m_token == INT64_MAX)
        { Fail(3); InterlockedExchange(&m_active, 0); return 0; }
        if (m_epochMode)
        {
            // Fail closed when no trusted ticker has ever advanced the shared epoch, and when the
            // counter is saturated: a saturated counter can never expire a new callback.
            INT64 epoch = g_runeCLREpoch.load(std::memory_order_acquire);
            if (epoch <= 0 || epoch == INT64_MAX)
            { Fail(3); InterlockedExchange(&m_active, 0); return 0; }
            m_startEpoch = epoch;
            m_lastEpoch = epoch;
        }
        else
        {
            if (!Now(m_start))
            { Fail(3); InterlockedExchange(&m_active, 0); return 0; }
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
        { FlushOwner(owner); Fail(4); return false; }
        FlushOwner(owner);
        bool result = Poll(owner);
        InterlockedExchange(&m_active, 0);
        return result;
    }
    bool Admit(void* owner, bool boundary) noexcept
    {
        if (!Enabled()) return true;
        if (Failure() != 0) { FlushOwner(owner); return false; }
        if (VolatileLoad(&m_active) != 1 || owner != m_owner.load(std::memory_order_acquire)) { Fail(4); return false; }
        if (boundary || ++m_pending >= m_interval)
        {
            m_pending = 0;
            if (!Poll(owner)) return false;
        }
        if (m_diagBatch) { ++m_diagPending; if (boundary) FlushOwner(owner); }
        else InterlockedIncrement64(&m_admitted);
        return true;
    }
    FORCEINLINE bool AdmitInline(void* owner, bool boundary) noexcept
    {
        if (!Enabled()) return true;
        if (Failure() != 0) { FlushOwner(owner); return false; }
        if (VolatileLoad(&m_active) != 1 || owner != m_owner.load(std::memory_order_acquire)) { FailInline(4); return false; }
        if (boundary || ++m_pending >= m_interval)
        {
            m_pending = 0;
            if (!PollCold(owner)) return false;
        }
        if (m_diagBatch) { ++m_diagPending; if (boundary) FlushOwner(owner); }
        else InterlockedIncrement64(&m_admitted);
        return true;
    }
private:
    NOINLINE void FailInline(LONG reason) noexcept { Fail(reason); }
    NOINLINE bool PollCold(void* owner) noexcept { return Poll(owner); }
    void Fail(LONG reason) noexcept { InterlockedCompareExchange(&m_failure, reason, 0); }
    bool Poll(void* owner) noexcept
    {
        FlushOwner(owner);
        if (Failure() != 0) return false;
        if (owner != m_owner.load(std::memory_order_acquire)) { Fail(4); return false; }
        InterlockedIncrement64(&m_checks);
        if (m_epochMode)
        {
            // No clock call on this path: one atomic load, monotonic check, budget compare.
            INT64 epoch = g_runeCLREpoch.load(std::memory_order_acquire);
            // Saturation is an irreversible invariant failure, not "no progress yet".
            if (epoch == INT64_MAX) { Fail(3); return false; }
            if (epoch < m_lastEpoch || epoch < m_startEpoch) { Fail(3); return false; }
            m_lastEpoch = epoch;
            if (epoch - m_startEpoch >= m_budgetTicks) { Fail(2); return false; }
            return true;
        }
        INT64 now;
        if (!Now(now) || now < m_last || now < m_start) { Fail(3); return false; }
        m_last = now;
        if (now - m_start >= m_duration) { Fail(2); return false; }
        return true;
    }
    static bool Frequency(INT64& frequency) noexcept
    {
#ifdef TARGET_WINDOWS
        LARGE_INTEGER value;
        if (!QueryPerformanceFrequency(&value) || value.QuadPart <= 0) return false;
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
        if (!QueryPerformanceCounter(&value) || value.QuadPart < 0) return false;
        ticks = value.QuadPart;
#else
        struct timespec value;
#ifdef __APPLE__
        const clockid_t clock = CLOCK_UPTIME_RAW;
#else
        const clockid_t clock = CLOCK_MONOTONIC;
#endif
        if (clock_gettime(clock, &value) != 0 || value.tv_sec < 0 || value.tv_nsec < 0 || value.tv_nsec >= 1000000000 ||
            value.tv_sec > (INT64_MAX - value.tv_nsec) / 1000000000) return false;
        ticks = value.tv_sec * INT64_C(1000000000) + value.tv_nsec;
#endif
        return true;
    }
    INT32 m_interval = 0;
    volatile LONG m_failure = 0;
    volatile LONG m_active = 0;
    std::atomic<void*> m_owner{nullptr};
    static_assert(std::atomic<void*>::is_always_lock_free);
    INT64 m_frequency = 0;
    INT64 m_duration = 0;
    INT64 m_start = 0;
    INT64 m_last = 0;
    INT64 m_token = 0;
    INT32 m_pending = 0;
    bool m_epochMode = false;
    INT64 m_budgetTicks = 0;
    INT64 m_startEpoch = 0;
    INT64 m_lastEpoch = 0;
    alignas(8) mutable volatile LONGLONG m_checks = 0;
    alignas(8) mutable volatile LONGLONG m_admitted = 0;
    UINT32 m_diagPending = 0;
    const bool m_diagBatch = CLRConfig::GetConfigValue(CLRConfig::EXTERNAL_RuneCLRDeadlineBatchedAdmitted) != 0;
};
#endif
