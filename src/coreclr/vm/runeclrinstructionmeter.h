// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#ifndef _RUNECLR_INSTRUCTION_METER_H
#define _RUNECLR_INSTRUCTION_METER_H

// Private diagnostic accounting plus an opt-in terminal prototype.
// Charge() preserves diagnostic v1 behavior; terminal dispatch uses ChargeExecution().
// The owning CustomAssemblyBinder outlives all active frames for its definitions.
class RuneCLRInstructionMeter
{
public:
    explicit RuneCLRInstructionMeter(INT64 limit, bool terminalPrototype = false, INT64 cleanupLimit = 0)
        : m_enabled(limit > 0), m_terminalPrototype(terminalPrototype),
          m_phaseAcquireLoad(terminalPrototype && CLRConfig::GetConfigValue(CLRConfig::EXTERNAL_RuneCLRPhaseAcquireLoad) != 0),
          m_derivedLedger(terminalPrototype && CLRConfig::GetConfigValue(CLRConfig::EXTERNAL_RuneCLRDerivedLedger) != 0),
          m_remaining(limit), m_executed(0),
          m_cleanupRemaining(cleanupLimit), m_cleanupExecuted(0), m_phase(0),
          m_initialExecutionLimit(limit), m_initialCleanupLimit(cleanupLimit)
    {
        // Keep existing field offsets; append immutable allowances for the opt-in ledger.
        static_assert(offsetof(RuneCLRInstructionMeter, m_enabled) == 0);
        static_assert(offsetof(RuneCLRInstructionMeter, m_terminalPrototype) == 1);
        static_assert(offsetof(RuneCLRInstructionMeter, m_phaseAcquireLoad) == 2);
        static_assert(offsetof(RuneCLRInstructionMeter, m_derivedLedger) == 3);
        static_assert(offsetof(RuneCLRInstructionMeter, m_remaining) == 8);
        static_assert(offsetof(RuneCLRInstructionMeter, m_executed) == 16);
        static_assert(offsetof(RuneCLRInstructionMeter, m_cleanupRemaining) == 24);
        static_assert(offsetof(RuneCLRInstructionMeter, m_cleanupExecuted) == 32);
        static_assert(offsetof(RuneCLRInstructionMeter, m_phase) == 40);
        static_assert(offsetof(RuneCLRInstructionMeter, m_initialExecutionLimit) == 48);
        static_assert(offsetof(RuneCLRInstructionMeter, m_initialCleanupLimit) == 56);
        static_assert(sizeof(RuneCLRInstructionMeter) == 64);
        static_assert(alignof(RuneCLRInstructionMeter) == 8);
    }

    bool Enabled() const { return m_enabled; }
    bool IsTerminalPrototype() const { return m_terminalPrototype; }
    bool IsTerminated() const noexcept { return Phase() != 0; }
    LONG Reason() const noexcept { return IsTerminated() ? 1 : 0; }
    LONG Phase() const noexcept
    {
        // Acquire orders later reads, but does not retain the old full RMW fence.
        return m_phaseAcquireLoad ? VolatileLoad(&m_phase) : InterlockedCompareExchange(&m_phase, 0, 0);
    }

    INT64 CleanupRemaining() const noexcept
    {
        return InterlockedCompareExchange64(&m_cleanupRemaining, 0, 0);
    }

    INT64 CleanupExecuted() const noexcept
    {
        if (m_derivedLedger)
            return m_initialCleanupLimit - CleanupRemaining();
        return InterlockedCompareExchange64(&m_cleanupExecuted, 0, 0);
    }

    FORCEINLINE bool ChargeExecution() noexcept
    {
        if (!m_terminalPrototype)
        {
            Charge();
            return true;
        }
        if (IsTerminated())
            return false;
        if (!TryConsume(m_remaining, m_executed, m_derivedLedger))
        {
            // The first rejected opcode latches the cause; no managed exception is needed here.
            InterlockedCompareExchange(&m_phase, 1, 0);
            return false;
        }
        return true;
    }

    FORCEINLINE bool TryChargeCleanup() noexcept
    {
        // Normal finally blocks remain in execution phase and use ChargeExecution().
        if (!m_terminalPrototype || !IsTerminated() || Phase() != 1)
            return false;
        if (!TryConsume(m_cleanupRemaining, m_cleanupExecuted, m_derivedLedger))
        {
            InterlockedCompareExchange(&m_phase, 2, 1);
            return false;
        }
        return true;
    }

    INT64 Remaining() const noexcept
    {
        return InterlockedCompareExchange64(&m_remaining, 0, 0);
    }

    INT64 Executed() const noexcept
    {
        // Count CAS-admitted dispatches, not completed bodies; separate getters are not a transaction.
        if (m_derivedLedger)
            return m_initialExecutionLimit - Remaining();
        return InterlockedCompareExchange64(&m_executed, 0, 0);
    }

    FORCEINLINE void Charge() noexcept
    {
        // Independent saturating counters; snapshots are not a two-field transaction.
        LONGLONG previous = Executed();
        while (previous < INT64_MAX)
        {
            LONGLONG observed = InterlockedCompareExchange64(&m_executed, previous + 1, previous);
            if (observed == previous)
                break;
            previous = observed;
        }

        previous = Remaining();
        while (previous > 0)
        {
            LONGLONG observed = InterlockedCompareExchange64(&m_remaining, previous - 1, previous);
            if (observed == previous)
                break;
            previous = observed;
        }
    }

private:
    // Count only allowed terminal opcodes. At quiescence, remaining = limit - executed.
    static FORCEINLINE bool TryConsume(volatile LONGLONG& remaining, volatile LONGLONG& executed, bool derivedLedger) noexcept
    {
        LONGLONG previous = InterlockedCompareExchange64(&remaining, 0, 0);
        while (previous > 0)
        {
            LONGLONG observed = InterlockedCompareExchange64(&remaining, previous - 1, previous);
            if (observed == previous)
            {
                // The immutable positive limit is at most INT64_MAX, so admitted charges cannot overflow.
                if (!derivedLedger)
                    InterlockedIncrement64(&executed);
                return true;
            }
            previous = observed;
        }
        return false;
    }

    const bool m_enabled;
    const bool m_terminalPrototype;
    const bool m_phaseAcquireLoad;
    const bool m_derivedLedger;
    alignas(8) mutable volatile LONGLONG m_remaining;
    // These counter slots are not written or authoritative for an opted-in terminal ledger.
    alignas(8) mutable volatile LONGLONG m_executed;
    alignas(8) mutable volatile LONGLONG m_cleanupRemaining;
    alignas(8) mutable volatile LONGLONG m_cleanupExecuted;
    // One atomic latch: reason is derived as phase != 0 ? execution-limit : none.
    mutable volatile LONG m_phase; // 0 execution; 1 terminated cleanup; 2 cleanup exhausted.
    alignas(8) const INT64 m_initialExecutionLimit;
    alignas(8) const INT64 m_initialCleanupLimit;
};

#endif // _RUNECLR_INSTRUCTION_METER_H
