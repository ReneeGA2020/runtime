// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#ifndef _INTERPEXEC_H_
#define _INTERPEXEC_H_

#include <interpretershared.h>
#include "interpframeallocator.h"

#define INTERP_STACK_SIZE 1024*1024
#define INTERP_STACK_FRAGMENT_SIZE 4096

struct StackVal
{
    union
    {
        int32_t i;
        int64_t l;
        size_t s;
        float f;
        double d;
        void *o;
        void *p;
    } data;
};

typedef DPTR(struct InterpMethodContextFrame) PTR_InterpMethodContextFrame;
class InterpreterFrame;
class RuneCLRDeadline;

struct InterpMethodContextFrame
{
    PTR_InterpMethodContextFrame pParent;
    PTR_InterpByteCodeStart startIp; // from startIp we can obtain InterpMethod and MethodDesc
    int8_t *pStack;
    int8_t *pRetVal;
    const int32_t *ip; // This ip is updated only when execution can leave the frame
    PTR_InterpMethodContextFrame pNext;

    // Private RuneCLR experiment: the binder's callback deadline is resolved once per
    // frame (see runeclrdeadline.h). nullptr means "not resolved yet"; the resolved
    // "no deadline" case stores &g_runeCLRDeadlineNone so the cache stays one pointer
    // and the frame stays 56 bytes instead of 64.
    RuneCLRDeadline* runeclrCallbackDeadline;

#ifndef DACCESS_COMPILE
    FORCEINLINE void ResetRuneCLRCallbackDeadline() noexcept
    {
        runeclrCallbackDeadline = nullptr;
    }

    void ReInit(InterpMethodContextFrame *pParent, InterpByteCodeStart* startIp, int8_t *pRetVal, int8_t *pStack)
    {
        ResetRuneCLRCallbackDeadline();
        this->pParent = pParent;
        this->startIp = startIp;
        this->pRetVal = pRetVal;
        this->pStack = pStack;
        this->ip = NULL;
    }
#endif // DACCESS_COMPILE
};

struct InterpThreadContext
{
    PTR_INT8 pStackStart;
    PTR_INT8 pStackEnd;

    // This stack pointer is the highest stack memory that can be used by the current frame. This does not
    // change throughout the execution of a frame and it is essentially the upper limit of the execution
    // stack pointer. It is needed when re-entering interp, to know from which address we can start using
    // stack, and also needed for the GC to be able to scan the stack.
    PTR_INT8 pStackPointer;

    FrameDataAllocator frameDataAllocator;

    // Private RuneCLR experiment: non-null only while this thread executes a
    // finally/fault funclet of a terminated callback, which is the single path
    // allowed to spend the bounded cleanup budget instead of being abandoned.
    RuneCLRDeadline* runeclrCleanupOwner;

#ifdef DEBUGGING_SUPPORTED
    // Breakpoint bypass state. When the debugger wants the interpreter to skip
    // a breakpoint and execute the original opcode, it sets these fields.
    // Stored on thread context rather than frame context to avoid bloating
    // the hot InterpMethodContextFrame struct with debug-only fields.
    const int32_t *m_bypassAddress;   // Address of breakpoint to bypass (NULL = no bypass)
    int32_t        m_bypassOpcode;    // Original opcode to execute instead of INTOP_BREAKPOINT

    void SetBypass(const int32_t* address, int32_t opcode)
    {
        _ASSERTE(m_bypassAddress == NULL);
        m_bypassAddress = address;
        m_bypassOpcode = opcode;
    }

    void ClearBypass()
    {
        m_bypassAddress = NULL;
        m_bypassOpcode = 0;
    }

    bool HasBypass(const int32_t* address, int32_t* pOpcode) const
    {
        if (m_bypassAddress == address)
        {
            if (pOpcode != NULL)
                *pOpcode = m_bypassOpcode;
            return true;
        }
        return false;
    }
#endif // DEBUGGING_SUPPORTED

    InterpThreadContext();
    ~InterpThreadContext();
};

struct ExceptionClauseArgs
{
    // Address of the exception clause IR code
    const int32_t *ip;
    // Frame in which context the exception clause is executed
    InterpMethodContextFrame *pFrame;
    // Set to true if the exception clause is a filter
    bool isFilter;
    // Set to true for a finally/fault cleanup funclet. A callback deadline that
    // fires inside such a funclet abandons the remaining author cleanup instead
    // of throwing, so an unbounded finally cannot outlive the callback.
    bool isCleanup;
    // The exception object passed to the filter or catch clause
    OBJECTREF throwable;
};

void InterpExecMethod(InterpreterFrame *pInterpreterFrame, InterpMethodContextFrame *pFrame, InterpThreadContext *pThreadContext, ExceptionClauseArgs *pExceptionClauseArgs = NULL);
EXTERN_C FCDECL2(ContinuationObject*, AsyncHelpers_ResumeInterpreterContinuation, ContinuationObject* cont, uint8_t* resultStorage);

extern "C" void LookupUnmanagedCallersOnlyMethodByName(const char* fullQualifiedTypeName, const char* methodName, MethodDesc** ppMD);
extern "C" void ExecuteInterpretedMethodFromUnmanaged(MethodDesc* pMD, int8_t* args, size_t argSize, int8_t* ret, PCODE callerIp);

CallStubHeader *CreateNativeToInterpreterCallStub(InterpMethod* pInterpMethod);

void InvokeManagedMethod(MethodDesc *pMD, int8_t *pArgs, int8_t *pRet, PCODE target, Object** pContinuationRet);

void InterpDispatchCache_ReclaimAll();
void InterpDispatchCache_ClearForLoaderAllocator(LoaderAllocator* pLoaderAllocator);

#endif // _INTERPEXEC_H_
