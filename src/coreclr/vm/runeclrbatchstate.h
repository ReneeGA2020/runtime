// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#ifndef _RUNECLR_BATCH_STATE_H
#define _RUNECLR_BATCH_STATE_H

// Stack-local to one interpreter invocation. Dangerous opcodes flush before
// running; pure opcodes cannot enter EH/native callbacks with pending debt.
class RuneCLRBatchState
{
    RuneCLRInstructionMeter* m_meter = nullptr;
    const void* m_frame = nullptr;
    const void* m_start = nullptr;
    UINT32 m_pending = 0;
public:
    ~RuneCLRBatchState() { Flush(); }
    void Flush() noexcept
    {
        if (m_pending != 0)
        {
            m_meter->RecordBatch(m_pending);
            m_pending = 0;
        }
        m_meter = nullptr;
        m_frame = m_start = nullptr;
    }
    bool Admit(RuneCLRInstructionMeter* meter, const void* frame, const void* start) noexcept
    {
        if (m_meter != meter || m_frame != frame || m_start != start || m_pending == meter->BatchInterval())
        {
            Flush();
            m_meter = meter;
            m_frame = frame;
            m_start = start;
        }
        if (m_pending == 0 && !meter->CanBeginBatch())
            return false;
        ++m_pending;
        return true;
    }
};
#endif
