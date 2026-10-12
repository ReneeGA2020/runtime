// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#ifndef _RUNECLR_INSTRUCTION_METER_H
#define _RUNECLR_INSTRUCTION_METER_H

// Private diagnostic accounting only. Exhaustion does not throw or stop execution.
// The owning CustomAssemblyBinder outlives all active frames for its definitions.
class RuneCLRInstructionMeter
{
public:
    explicit RuneCLRInstructionMeter(INT64 limit)
        : m_enabled(limit > 0), m_remaining(limit), m_executed(0)
    {
    }

    bool Enabled() const { return m_enabled; }

    INT64 Remaining() const noexcept
    {
        return InterlockedCompareExchange64(&m_remaining, 0, 0);
    }

    INT64 Executed() const noexcept
    {
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
    const bool m_enabled;
    alignas(8) mutable volatile LONGLONG m_remaining;
    alignas(8) mutable volatile LONGLONG m_executed;
};

#endif // _RUNECLR_INSTRUCTION_METER_H
