// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace System.Reflection
{
    // Private, opt-in reflection experiment. Operation IDs form the versioned host bridge contract.
    internal static class RuneCLRReflectionPolicy
    {
        private static readonly AsyncLocal<Scope?> s_current = new AsyncLocal<Scope?>();
        private static volatile bool s_everEntered;
        [ThreadStatic] private static bool t_checking;
        [ThreadStatic] private static int t_internalDelegateCreation;
        [ThreadStatic] internal static int TrustedResolutionDepth;
        internal const int MaximumUgcImageBytes = 32 * 1024 * 1024;

        private static int RuneCLR_GetReflectionPolicyVersion() => 4;

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
            return (current ?? attached)?.LoadContext();
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

        internal static bool HasScope => s_everEntered && s_current.Value is not null;

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

        private static void CheckMember(MemberInfo member, object? receiver, object?[]? arguments, int operation)
        {
            if (!s_everEntered)
                return;
            Scope? scope = s_current.Value;
            if (scope is null)
                return;
            Type? declaring = member as Type ?? member.DeclaringType;
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
        }

        internal sealed class Session : IDisposable
        {
            private readonly object _gate = new object();
            private Action<MemberInfo, object?, object?[]?, int>? _check;
            private AssemblyLoadContext? _context;
            private Action<byte[], string?>? _admit;

            internal Session(Action<MemberInfo, object?, object?[]?, int> check) => _check = check;

            internal Session(Action<MemberInfo, object?, object?[]?, int> check, AssemblyLoadContext context,
                Action<byte[], string?> admit) : this(check)
            {
                _context = context;
                _admit = admit;
            }

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
