// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace System.Reflection
{
    // Private, opt-in reflection experiment. Operation IDs form the versioned host bridge contract.
    internal static partial class RuneCLRReflectionPolicy
    {
        private static readonly AsyncLocal<Scope?> s_current = new AsyncLocal<Scope?>();
        private static volatile bool s_everEntered;
        [ThreadStatic] private static bool t_checking;
        [ThreadStatic] private static int t_internalDelegateCreation;
        [ThreadStatic] internal static int TrustedResolutionDepth;
        internal const int MaximumUgcImageBytes = 32 * 1024 * 1024;
        internal const int SessionOriginCapability = 1;
        private const int MaximumStackOrigins = 16;
        private static readonly bool s_sessionOriginScan = ReadSessionOriginScanSwitch();
        private static int s_attachedSessions;

        private static int RuneCLR_GetReflectionPolicyVersion() => 4;

        // Additive capability contract. The versioned entry points stay at 4 so the
        // published host bridge keeps binding; capability bits describe optional support.
        private static int RuneCLR_GetReflectionPolicyCapabilities()
        {
            int capabilities = 0;
            if (s_sessionOriginScan)
            {
                try
                {
                    AssemblyLoadContext? ignored = null;
                    _ = RuneCLR_GetStackLoadContextNative(-1, ObjectHandleOnStack.Create(ref ignored));
                    capabilities |= SessionOriginCapability;
                }
                catch (EntryPointNotFoundException) { }
            }
            return capabilities;
        }

        private static bool ReadSessionOriginScanSwitch() =>
            !AppContext.TryGetSwitch("RuneCLR.EnableReflectionPolicyOriginScan", out bool enabled) || enabled;

        // Attached sessions are never forgotten: a retired session keeps a fail-closed
        // tombstone so author frames that still execute cannot silently become unrestricted.
        private static bool SessionOriginScanActive =>
            s_sessionOriginScan && Volatile.Read(ref s_attachedSessions) != 0;

        // Returns the skip-th distinct non-default load context executing on this thread.
        // The runtime reports an incomplete walk instead of truncating an outer owner.
        private static bool GetStackLoadContext(int skip, [NotNullWhen(true)] out AssemblyLoadContext? context)
        {
            // The runtime writes the managed reference into a plain local first; only a
            // successfully materialised owner is copied out to the caller's slot.
            AssemblyLoadContext? resolved = null;
            int status = RuneCLR_GetStackLoadContextNative(skip, ObjectHandleOnStack.Create(ref resolved));
            context = resolved;
            if (status != 0)
                throw new NotSupportedException(status == 1
                    ? "Too many distinct non-default load contexts execute on this thread for the session-origin policy."
                    : "The runtime could not inspect this thread for the session-origin policy.");
            return context is not null;
        }

        private static void DemandStackOrigins(MemberInfo member, object? receiver, object?[]? arguments,
            int operation, Scope? ambient)
        {
            for (int skip = 0; skip < MaximumStackOrigins; skip++)
            {
                if (!GetStackLoadContext(skip, out AssemblyLoadContext? context))
                    return;
                Session? session = context.RuneCLRPolicySession;
                if (session is null || ambient?.Includes(session) == true)
                    continue;
                // Enforcement state stays unreachable to author code on this path too.
                GuardMember(member, member as Type ?? member.DeclaringType);
                t_checking = true;
                try
                {
                    session.Demand(member, receiver, arguments, operation);
                    if (operation == 4)
                        session.Demand(ResolveDispatch((MethodInfo)member, receiver), receiver, arguments, 0);
                }
                finally
                {
                    t_checking = false;
                }
            }
        }

        private static Session? StackSession(Session? origin)
        {
            for (int skip = 0; skip < MaximumStackOrigins; skip++)
            {
                if (!GetStackLoadContext(skip, out AssemblyLoadContext? context))
                    return origin;
                Session? session = context.RuneCLRPolicySession;
                if (session is null)
                    continue;
                if (origin is not null && !ReferenceEquals(origin, session))
                    throw new InvalidOperationException("Assembly loads cannot cross UGC policy sessions.");
                origin = session;
            }
            return origin;
        }

        private static bool HasStackOrigin()
        {
            for (int skip = 0; skip < MaximumStackOrigins; skip++)
            {
                if (!GetStackLoadContext(skip, out AssemblyLoadContext? context))
                    return false;
                if (context.RuneCLRPolicySession is not null)
                    return true;
            }
            return false;
        }

        // The QCall ABI appends the hidden error-status parameter only when it is declared.
        [ErrorHandler(typeof(QCallExceptionStatusMarshaller), ErrorLocation.HiddenLastParameter)]
        [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "RuneCLRReflectionPolicy_GetStackLoadContext")]
        private static partial int RuneCLR_GetStackLoadContextNative(int skip, ObjectHandleOnStack context);

        private static IDisposable RuneCLR_EnterReflectionPolicy(Action<MemberInfo, object?, object?[]?, int> check)
        {
            ArgumentNullException.ThrowIfNull(check);
            if (!AppContext.TryGetSwitch("RuneCLR.EnableReflectionPolicy", out bool enabled) || !enabled)
                throw new PlatformNotSupportedException("The RuneCLR reflection-policy experiment is disabled.");

            return new Session(check).Activate(retireSession: true);
        }

        private static IDisposable RuneCLR_CreateReflectionPolicySession(
            Action<MemberInfo, object?, object?[]?, int> check, AssemblyLoadContext context,
            Action<byte[], string?> admit, out Func<IDisposable> activate)
        {
            ArgumentNullException.ThrowIfNull(check);
            if (!AppContext.TryGetSwitch("RuneCLR.EnableReflectionPolicy", out bool enabled) || !enabled)
                throw new PlatformNotSupportedException("The RuneCLR reflection-policy experiment is disabled.");
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(admit);
            var session = new Session(check, context, admit);
            // Publish the sticky scan requirement before the owner mapping can be observed,
            // so a concurrent stack-walk lookup cannot see the owner while scans look disabled.
            // A failed attach leaves the counter set, which only keeps the scan armed.
            session.MarkAttached();
            context.RuneCLR_AttachPolicySession(session);
            activate = () => session.Activate(retireSession: false);
            return session;
        }

        internal static AssemblyLoadContext? LoadContext(AssemblyLoadContext? requested)
        {
            Scope? scope = s_everEntered ? s_current.Value : null;
            Session? current = scope?.LoadSession();
            Session? attached = requested?.RuneCLRPolicySession;
            if (current is not null && attached is not null && !ReferenceEquals(current, attached))
                throw new InvalidOperationException("Assembly loads cannot cross UGC policy sessions.");
            // Resolver owned dependencies keep their normal binding context, but an
            // explicitly attached session still receives the same admission path.
            if (TrustedResolutionDepth != 0)
                return attached?.LoadContext();
            Session? origin = current ?? attached;
            int attachedCount = Volatile.Read(ref s_attachedSessions);
            if (s_sessionOriginScan && attachedCount != 0 && (origin is null || attachedCount > 1))
            {
                // A present ambient or requested context must not mask another actual
                // owner on the stack; a different owner refuses the load.
                origin = StackSession(origin);
            }
            return origin?.LoadContext();
        }

        internal static bool InternalDelegateCreationAllowed => t_internalDelegateCreation > 0;

        internal static void DemandInternalDelegateCreation(string route)
        {
            if (HasScope && !InternalDelegateCreationAllowed)
                throw new NotSupportedException(route + " is unsupported while a reflection policy is active.");
        }

        internal static Delegate CreateDynamicMethodDelegate(DynamicMethod method, Type delegateType)
        {
            ArgumentNullException.ThrowIfNull(method);
            ArgumentNullException.ThrowIfNull(delegateType);
            int previous = t_internalDelegateCreation;
            t_internalDelegateCreation = previous + 1;
            try { return method.CreateDelegate(delegateType, target: null); }
            finally { t_internalDelegateCreation = previous; }
        }

        internal static void Check(MethodInfo method, object? receiver, object?[]? arguments)
        {
            if (!HasScope)
                return;
            if (method.IsVirtual && !method.IsStatic && receiver is not null)
            {
                Type receiverType = receiver.GetType();
                // Reject object-dependent casts before the host's receiver compatibility check.
                if (receiverType.IsCOMObject || typeof(Runtime.InteropServices.IDynamicInterfaceCastable).IsAssignableFrom(receiverType) ||
                    (method.DeclaringType?.IsInterface == true && receiverType.IsArray))
                    RefuseUnsupported("Special runtime dispatch");
            }
            if (method.DeclaringType is Type type && typeof(Delegate).IsAssignableFrom(type) &&
                method.Name is "Invoke" or "BeginInvoke" or "EndInvoke")
                RefuseUnsupported("Reflected delegate invocation");
            CheckMember(method, receiver, arguments, method.IsVirtual && !method.IsStatic ? 4 : 0);
        }

        internal static void CheckInvoker(MethodBase method, object? receiver, ReadOnlySpan<object?> arguments)
        {
            if (!HasScope)
                return;
            if (method is DynamicMethod)
                throw new NotSupportedException("Dynamic MethodInvoker invocation is unsupported while a reflection policy is active.");
            object?[] values = arguments.ToArray();
            if (method is MethodInfo info)
                Check(info, receiver, values);
            else if (method is ConstructorInfo constructor)
                CheckConstructor(constructor, receiver, values);
            else
                throw new NotSupportedException("Dynamic reflection targets are unsupported by the reflection policy.");
        }

        internal static void CheckInvoker(MethodBase method, object? receiver, int count,
            object? arg1, object? arg2, object? arg3, object? arg4)
        {
            if (HasScope)
                CheckInvoker(method, receiver, new object?[] { arg1, arg2, arg3, arg4 }.AsSpan(0, count));
        }

        internal static void CheckConstructor(ConstructorInfo constructor, object? receiver, object?[]? arguments) =>
            CheckMember(constructor, receiver, arguments, 1);

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2070:UnrecognizedReflectionPattern",
            Justification = "Activator already requires the constructor. This policy lookup executes no user code and fails if metadata is absent.")]
        internal static void CheckDefaultConstruction(RuntimeType type)
        {
            if (!HasScope)
                return;
            ConstructorInfo? constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (constructor is not null)
                CheckConstructor(constructor, null, null);
            else if (type.IsValueType)
                CheckMember(type, null, null, 5);
            else
                throw new MissingMethodException("No parameterless constructor exists.");
        }

        internal static void CheckPropertySetter(MethodInfo setter, object? receiver, object? value)
        {
            if (HasScope)
                Check(setter, receiver, new object?[] { value });
        }

        internal static void CheckField(FieldInfo field, object? receiver, object? value, bool write)
        {
            if (!HasScope)
                return;
            CheckMember(field, receiver, write ? new object?[] { value } : null, write ? 3 : 2);
        }

        internal static void RefuseDirectFieldAccess()
        {
            if (HasScope)
                throw new NotSupportedException("TypedReference field access is unsupported by the reflection-policy experiment.");
        }

        internal static void RefuseUnsupported(string route)
        {
            if (HasScope)
                throw new NotSupportedException(route + " is unsupported while a reflection policy is active.");
        }

        internal static bool HasScope
        {
            get
            {
                if (!s_everEntered)
                    return false;
                if (s_current.Value is not null)
                    return true;
                if (!SessionOriginScanActive)
                    return false;
                return HasStackOrigin();
            }
        }

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2060:UnrecognizedReflectionPattern",
            Justification = "Rebinds an existing closed invocation's generic arguments to its runtime-resolved implementation. Missing metadata fails closed.")]
        private static MethodInfo ResolveDispatch(MethodInfo method, object? receiver)
        {
            if (method is not RuntimeMethodInfo runtimeMethod || receiver is null ||
                method.DeclaringType is not RuntimeType declaringType || !declaringType.IsInstanceOfType(receiver))
                throw new InvalidOperationException("Reflection dispatch requires a compatible runtime receiver.");

            RuntimeType receiverType = (RuntimeType)receiver.GetType();
            // These can use object-dependent dispatch outside the ordinary method table.
            if (receiverType.IsCOMObject || typeof(Runtime.InteropServices.IDynamicInterfaceCastable).IsAssignableFrom(receiverType) ||
                (declaringType.IsInterface && receiverType.IsArray))
                throw new NotSupportedException("Special runtime reflection dispatch is unsupported.");

            RuntimeMethodHandleInternal handle;
            if (declaringType.IsInterface)
                handle = receiverType.TypeHandle.GetInterfaceMethodImplementation(declaringType.TypeHandle,
                    IRuntimeMethodInfo.GetValue(runtimeMethod));
            else
                handle = RuntimeTypeHandle.GetMethodAt(receiverType, RuntimeMethodHandle.GetSlot(runtimeMethod));

            if (handle.IsNullHandle())
                throw new NotSupportedException("The final reflection dispatch target could not be resolved.");
            RuntimeType reflectedType = RuntimeMethodHandle.GetDeclaringType(handle);
            if (!reflectedType.IsInterface)
                reflectedType = receiverType;
            MethodInfo target = (MethodInfo)RuntimeType.GetMethodBase(reflectedType, handle)!;
            if (method.IsGenericMethod)
                target = target.GetGenericMethodDefinition().MakeGenericMethod(method.GetGenericArguments());
            if (target.IsAbstract || target.ContainsGenericParameters)
                throw new NotSupportedException("The final reflection dispatch target must be closed and executable.");
            GC.KeepAlive(runtimeMethod);
            GC.KeepAlive(receiver);
            return target;
        }

        private static void GuardMember(MemberInfo member, Type? declaring)
        {
            for (Type? enclosing = declaring; enclosing is not null; enclosing = enclosing.DeclaringType)
                if (enclosing == typeof(RuneCLRReflectionPolicy) ||
                    enclosing == typeof(InvokerEmitUtil) ||
                    enclosing == typeof(MethodInvokerCommon) ||
                    enclosing == typeof(MethodBaseInvoker))
                    throw new InvalidOperationException("Reflection cannot access the active policy's enforcement or invoker-compiler state.");
            if (declaring == typeof(AssemblyLoadContext) &&
                (member.Name.StartsWith("RuneCLR", StringComparison.Ordinal) || member.Name.StartsWith("s_runeCLR", StringComparison.Ordinal)))
                throw new InvalidOperationException("Reflection cannot access the UGC loader's enforcement state.");
            if (t_checking)
                throw new InvalidOperationException("A reflection-policy callback cannot recursively invoke reflection.");
        }

        private static void CheckMember(MemberInfo member, object? receiver, object?[]? arguments, int operation)
        {
            if (!s_everEntered)
                return;
            Scope? scope = s_current.Value;
            int attachedCount = Volatile.Read(ref s_attachedSessions);
            if (scope is not null)
            {
                GuardMember(member, member as Type ?? member.DeclaringType);
                t_checking = true;
                try
                {
                    scope.Demand(member, receiver, arguments, operation);
                    if (operation == 4)
                        scope.Demand(ResolveDispatch((MethodInfo)member, receiver), receiver, arguments, 0);
                }
                finally
                {
                    t_checking = false;
                }
                // Normal flowing execution still owes every other actual owner on the stack.
                // A single attached owner already present in this chain cannot be masked.
                if (s_sessionOriginScan && attachedCount != 0 &&
                    (attachedCount > 1 || !scope.IncludesAttachedSession()))
                    DemandStackOrigins(member, receiver, arguments, operation, scope);
                return;
            }
            if (!s_sessionOriginScan || attachedCount == 0)
                return;
            DemandStackOrigins(member, receiver, arguments, operation, ambient: null);
        }

        internal sealed class Session : IDisposable
        {
            private readonly object _gate = new object();
            private Action<MemberInfo, object?, object?[]?, int>? _check;
            private AssemblyLoadContext? _context;
            private Action<byte[], string?>? _admit;
            private readonly bool _ownsContext;

            internal Session(Action<MemberInfo, object?, object?[]?, int> check) => _check = check;

            // Counted once per attached context and never forgotten, so a retired session
            // cannot silently become unrestricted while its author frames still execute.
            internal void MarkAttached() => Interlocked.Increment(ref s_attachedSessions);

            internal Session(Action<MemberInfo, object?, object?[]?, int> check, AssemblyLoadContext context,
                Action<byte[], string?> admit) : this(check)
            {
                _context = context;
                _admit = admit;
                _ownsContext = true;
            }

            // Identity of an attached owner survives retirement; a retired owner still fails closed.
            internal bool OwnsContext => _ownsContext;

            internal AssemblyLoadContext? LoadContext()
            {
                lock (_gate)
                {
                    if (_check is null) throw new InvalidOperationException("The UGC policy session has retired.");
                    return _context;
                }
            }

            internal void Admit(byte[] image, string? path)
            {
                lock (_gate)
                {
                    if (_check is null || _admit is null)
                        throw new InvalidOperationException("The UGC policy session has retired.");
                    _admit(image, path);
                }
            }

            internal IDisposable Activate(bool retireSession)
            {
                lock (_gate)
                {
                    if (_check is null)
                        throw new ObjectDisposedException("Reflection-policy session");
                    var scope = new Scope(this, s_current.Value, retireSession);
                    s_current.Value = scope;
                    s_everEntered = true;
                    return scope;
                }
            }

            internal void Demand(MemberInfo member, object? receiver, object?[]? arguments, int operation)
            {
                lock (_gate)
                {
                    Action<MemberInfo, object?, object?[]?, int> check = _check ??
                        throw new InvalidOperationException("The reflection-policy session has retired.");
                    check(member, receiver, arguments, operation);
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    // The load context mapping stays as a retired tombstone: later checks
                    // fail closed instead of treating still-executing author frames as trusted.
                    // No evaluator, admission callback or load context remains reachable here.
                    _check = null;
                    _context = null;
                    _admit = null;
                }
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly Scope? _previous;
            private readonly Session _session;
            private readonly bool _retireSession;
            private int _retired;

            internal Scope(Session session, Scope? previous, bool retireSession)
            {
                _session = session;
                _previous = previous;
                _retireSession = retireSession;
            }

            internal void Demand(MemberInfo member, object? receiver, object?[]? arguments, int operation)
            {
                if (Volatile.Read(ref _retired) != 0)
                    throw new InvalidOperationException("The reflection-policy scope has retired.");
                // Nested scopes can narrow the outer decision but cannot replace it with a wider policy.
                _previous?.Demand(member, receiver, arguments, operation);
                _session.Demand(member, receiver, arguments, operation);
            }

            internal bool Includes(Session session)
            {
                if (ReferenceEquals(_session, session))
                    return true;
                return _previous?.Includes(session) == true;
            }

            // True when the ambient chain already governs the only attached context owner.
            internal bool IncludesAttachedSession()
            {
                if (_session.OwnsContext)
                    return true;
                return _previous?.IncludesAttachedSession() == true;
            }

            internal Session? LoadSession()
            {
                if (Volatile.Read(ref _retired) != 0)
                    throw new InvalidOperationException("The reflection-policy scope has retired.");
                Session? previous = _previous?.LoadSession();
                AssemblyLoadContext? owner = _session.LoadContext();
                if (owner is not null && previous is not null && !ReferenceEquals(owner, previous.LoadContext()))
                    throw new InvalidOperationException("Nested assembly loads cannot change UGC policy-session ownership.");
                return owner is null ? previous : _session;
            }

            public void Dispose()
            {
                if (Volatile.Read(ref _retired) != 0)
                    return;
                if (!ReferenceEquals(s_current.Value, this))
                    throw new InvalidOperationException("Reflection-policy scopes must retire in nesting order.");

                // Flowed execution contexts retain a retired marker, not a live host callback or unrestricted access.
                Volatile.Write(ref _retired, 1);
                if (_retireSession) _session.Dispose();
                s_current.Value = _previous;
            }
        }
    }
}
