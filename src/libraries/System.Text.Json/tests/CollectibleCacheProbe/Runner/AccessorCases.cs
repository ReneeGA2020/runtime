// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using CollectibleCacheProbe;

public static class AccessorCases
{
    private static readonly Type s_accessorType = typeof(JsonSerializer).Assembly.GetType(
        "System.Text.Json.Serialization.Metadata.ReflectionEmitCachingMemberAccessor", throwOnError: true)!;

    private sealed class FixtureContext(string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) =>
            name.Name == typeof(DefaultBase).Assembly.GetName().Name ? typeof(DefaultBase).Assembly : null;
    }

    private sealed class StableConstructor(object value)
    {
        public object Value { get; } = value;
    }

    private sealed class AddCollection<T>
    {
        public object? Last { get; private set; }
        public void Enqueue(object value) => Last = value;
    }

    private sealed class UnknownProperty(PropertyInfo inner) : PropertyInfo
    {
        public override PropertyAttributes Attributes => inner.Attributes;
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override Type PropertyType => inner.PropertyType;
        public override Type DeclaringType => inner.DeclaringType!;
        public override Type ReflectedType => inner.ReflectedType!;
        public override string Name => inner.Name;
        public override MethodInfo[] GetAccessors(bool nonPublic) => inner.GetAccessors(nonPublic);
        public override MethodInfo? GetGetMethod(bool nonPublic) => inner.GetGetMethod(nonPublic);
        public override MethodInfo? GetSetMethod(bool nonPublic) => inner.GetSetMethod(nonPublic);
        public override ParameterInfo[] GetIndexParameters() => inner.GetIndexParameters();
        public override object[] GetCustomAttributes(bool inherit) => inner.GetCustomAttributes(inherit);
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => inner.GetCustomAttributes(attributeType, inherit);
        public override bool IsDefined(Type attributeType, bool inherit) => inner.IsDefined(attributeType, inherit);
        public override object? GetValue(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? index, System.Globalization.CultureInfo? culture) =>
            inner.GetValue(obj, invokeAttr, binder, index, culture);
        public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, object?[]? index, System.Globalization.CultureInfo? culture) =>
            inner.SetValue(obj, value, invokeAttr, binder, index, culture);
    }

    public static void Run(string fixturePath, bool patched)
    {
        fixturePath = Path.GetFullPath(fixturePath);
        var firstContext = new FixtureContext("accessor-first");
        var secondContext = new FixtureContext("accessor-second");
        Assembly firstAssembly = firstContext.LoadFromAssemblyPath(fixturePath);
        Assembly secondAssembly = secondContext.LoadFromAssemblyPath(fixturePath);
        Type first = FixtureType(firstAssembly, "Dto");
        Type second = FixtureType(secondAssembly, "Dto");
        Require(first != second && first.IsCollectible && second.IsCollectible && first.BaseType == typeof(DefaultBase),
            "Expected distinct collectible types with a shared stable base.");

        object accessor = NewAccessor();
        var failures = new List<string>();
        int baselineViolations = 0;

        Delegate Check(string label, string factory, Type[] types, object?[] args, bool shouldCache, object? target = null)
        {
            MethodInfo method = s_accessorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Single(candidate => candidate.Name == factory && candidate.GetGenericArguments().Length == types.Length
                    && candidate.GetParameters().Length == args.Length);
            if (types.Length > 0)
                method = method.MakeGenericMethod(types);

            target ??= accessor;
            var firstDelegate = (Delegate)method.Invoke(target, args)!;
            var secondDelegate = (Delegate)method.Invoke(target, args)!;
            Require(firstDelegate is not null && secondDelegate is not null, $"{label}: expected two delegates.");
            bool same = ReferenceEquals(firstDelegate, secondDelegate);
            Console.WriteLine($"ACCESSOR {label} same={same} expectSame={shouldCache}");
            if (same != shouldCache)
            {
                if (!patched && !shouldCache)
                    baselineViolations++;
                else
                    failures.Add(label);
            }
            return secondDelegate!;
        }

        var stable = new DefaultBase();
        PropertyInfo stableProperty = typeof(DefaultBase).GetProperty(nameof(DefaultBase.Inherited))!;
        FieldInfo stableField = typeof(DefaultBase).GetField(nameof(DefaultBase.BaseField))!;
        Require(!stableProperty.IsCollectible && !stableField.IsCollectible, "Stable control members must not be collectible.");
        Delegate stableGetter = Check("stable-property", "CreatePropertyGetter", [typeof(string)], [stableProperty], true);
        Require(Equals(stableGetter.DynamicInvoke(stable), stable.Inherited), "Stable property getter failed.");
        Check("stable-property-setter", "CreatePropertySetter", [typeof(string)], [stableProperty], true).DynamicInvoke(stable, "changed");
        Require(stable.Inherited == "changed", "Stable property setter failed.");
        Check("stable-field-setter", "CreateFieldSetter", [typeof(int)], [stableField], true).DynamicInvoke(stable, 31);
        Require(Equals(Check("stable-field", "CreateFieldGetter", [typeof(int)], [stableField], true).DynamicInvoke(stable), 31),
            "Stable field accessors failed.");
        Require(Check("stable-type-only", "CreateParameterlessConstructor", [], [typeof(ValueTuple<int>), null], true)
            .DynamicInvoke() is ValueTuple<int>, "Stable type-only constructor failed.");

        PropertyInfo wrappedProperty = new UnknownProperty(stableProperty);
        Require(wrappedProperty.IsCollectible && wrappedProperty.DeclaringType == typeof(DefaultBase)
            && wrappedProperty.ReflectedType == typeof(DefaultBase), "Wrapper must report unknown collectibility via MemberInfo's default.");
        Require(Equals(Check("unknown-property-wrapper", "CreatePropertyGetter", [typeof(string)], [wrappedProperty], false)
            .DynamicInvoke(stable), stable.Inherited), "Forwarding property wrapper failed.");

        Type crossContext = typeof(Pair<,>).MakeGenericType(first, typeof(Nest<>).MakeGenericType(second));
        Require(crossContext.IsCollectible && !crossContext.Assembly.IsCollectible, "Expected a collectible closed generic in a stable assembly.");
        Type crossTuple = typeof(ValueTuple<>).MakeGenericType(crossContext);
        Require(Check("cross-context-type-only", "CreateParameterlessConstructor", [], [crossTuple, null], false)
            .DynamicInvoke()!.GetType() == crossTuple, "Cross-context type-only constructor failed.");
        object firstValue = Check("collectible-parameterless-ctor", "CreateParameterlessConstructor", [], [first, first.GetConstructor(Type.EmptyTypes)], false)
            .DynamicInvoke()!;
        object secondValue = Activator.CreateInstance(second)!;
        object crossValue = Activator.CreateInstance(crossContext)!;
        PropertyInfo crossProperty = crossContext.GetProperty(nameof(Pair<object, object>.Left))!;
        Check("cross-context-property-setter", "CreatePropertySetter", [first], [crossProperty], false).DynamicInvoke(crossValue, firstValue);
        Require(ReferenceEquals(Check("cross-context-typed-property", "CreatePropertyGetter", [crossContext, first], [crossProperty], false)
            .DynamicInvoke(crossValue), firstValue), "Cross-context typed property accessors failed.");

        PropertyInfo reflectedProperty = first.GetProperty(stableProperty.Name)!;
        FieldInfo reflectedField = first.GetField(stableField.Name)!;
        Require(reflectedProperty.DeclaringType == typeof(DefaultBase) && reflectedProperty.ReflectedType == first
            && reflectedField.DeclaringType == typeof(DefaultBase) && reflectedField.ReflectedType == first,
            "Inherited members must have a stable declaring type and collectible reflected type.");
        Require(Equals(Check("collectible-reflected-property", "CreatePropertyGetter", [typeof(string)], [reflectedProperty], false)
            .DynamicInvoke(firstValue), ((DefaultBase)firstValue).Inherited), "Inherited property getter failed.");
        Require(Equals(Check("collectible-reflected-field", "CreateFieldGetter", [typeof(int)], [reflectedField], false)
            .DynamicInvoke(firstValue), ((DefaultBase)firstValue).BaseField), "Inherited field getter failed.");
        Require(Equals(Check("collectible-receiver-only", "CreatePropertyGetter", [first, typeof(string)], [stableProperty], false)
            .DynamicInvoke(firstValue), ((DefaultBase)firstValue).Inherited), "Typed getter with a stable member failed.");

        Delegate firstGetter = Check("first-context-property", "CreatePropertyGetter", [typeof(string)], [first.GetProperty("Text")], false);
        Delegate secondGetter = Check("second-context-property", "CreatePropertyGetter", [typeof(string)], [second.GetProperty("Text")], false);
        Require(!ReferenceEquals(firstGetter, secondGetter) && Equals(firstGetter.DynamicInvoke(firstValue), "collectible")
            && Equals(secondGetter.DynamicInvoke(secondValue), "collectible"), "Context-specific property getters were conflated.");
        Check("collectible-field-setter", "CreateFieldSetter", [typeof(int)], [first.GetField("Number")], false).DynamicInvoke(firstValue, 41);
        Require(Equals(first.GetField("Number")!.GetValue(firstValue), 41), "Collectible field setter failed.");

        Type large = FixtureType(firstAssembly, "LargeConstructor");
        Require(Check("collectible-ctor-member-only", "CreateParameterizedConstructor", [typeof(object)], [large.GetConstructors().Single()], false)
            .DynamicInvoke((object?)new object?[] { 1, 2, 3, 4, "end" })!.GetType() == large, "Covariant large constructor delegate failed.");

        ConstructorInfo stableCtor = typeof(StableConstructor).GetConstructors().Single();
        Require(!stableCtor.IsCollectible && !typeof(StableConstructor).IsCollectible, "Small-constructor cache key must be stable.");
        Require(((StableConstructor)Check("stable-single-ctor", "CreateSingleParameterConstructor", [typeof(StableConstructor)], [stableCtor], true)
            .DynamicInvoke(stable)!).Value == stable, "Stable single-argument constructor failed.");
        Require(((StableConstructor)Check("stable-small-ctor", "CreateParameterizedConstructor",
            [typeof(StableConstructor), typeof(object), typeof(object), typeof(object), typeof(object)], [stableCtor], true)
            .DynamicInvoke(stable, null, null, null)!).Value == stable, "Stable small constructor failed.");
        for (int slot = 0; slot < 4; slot++)
        {
            Type[] types = [typeof(StableConstructor), typeof(object), typeof(object), typeof(object), typeof(object)];
            types[slot + 1] = slot == 0 ? first : crossContext;
            Delegate create = Check($"collectible-small-ctor-targ{slot}", "CreateParameterizedConstructor", types, [stableCtor], false, NewAccessor());
            var result = (StableConstructor)create.DynamicInvoke(firstValue, null, null, null)!;
            Require(ReferenceEquals(result.Value, firstValue), $"Small constructor delegate with TArg{slot} failed.");
        }

        var stableStack = new Stack();
        Check("stable-add", "CreateAddMethodDelegate", [typeof(Stack)], [], true).DynamicInvoke(stableStack, stable);
        Require(ReferenceEquals(stableStack.Pop(), stable), "Stable stack add failed.");
        Type collectibleStack = typeof(AddCollection<>).MakeGenericType(crossContext);
        object firstStack = Activator.CreateInstance(collectibleStack)!;
        Check("collectible-add", "CreateAddMethodDelegate", [collectibleStack], [], false).DynamicInvoke(firstStack, firstValue);
        Require(ReferenceEquals(collectibleStack.GetProperty(nameof(AddCollection<object>.Last))!.GetValue(firstStack), firstValue),
            "Collectible collection add failed.");

        Type list = typeof(ImmutableList<>).MakeGenericType(crossContext);
        Array listItems = Array.CreateInstance(crossContext, 1);
        listItems.SetValue(crossValue, 0);
        Require(ReferenceEquals(((IEnumerable)Check("cross-context-immutable-enumerable", "CreateImmutableEnumerableCreateRangeDelegate",
            [list, crossContext], [], false).DynamicInvoke(listItems)!).Cast<object>().Single(), crossValue), "Immutable enumerable delegate failed.");

        Type dictionary = typeof(ImmutableDictionary<,>).MakeGenericType(first, second);
        Type entryType = typeof(KeyValuePair<,>).MakeGenericType(first, second);
        Array entries = Array.CreateInstance(entryType, 1);
        object entry = Activator.CreateInstance(entryType, firstValue, secondValue)!;
        entries.SetValue(entry, 0);
        Require(Equals(((IEnumerable)Check("cross-context-immutable-dictionary", "CreateImmutableDictionaryCreateRangeDelegate",
            [dictionary, first, second], [], false).DynamicInvoke(entries)!).Cast<object>().Single(), entry), "Immutable dictionary delegate failed.");

        Console.WriteLine($"ACCESSOR_RESULT patched={patched} baselineViolations={baselineViolations} failures={failures.Count}");
        Require(failures.Count == 0, $"Unexpected cache identities: {string.Join(", ", failures)}");
        Require(patched || baselineViolations > 0, "Baseline must violate at least one cache-bypass expectation.");
    }

    private static Type FixtureType(Assembly assembly, string name) =>
        assembly.GetType($"CollectibleCacheProbe.Fixture.{name}", throwOnError: true)!;

    private static object NewAccessor() => Activator.CreateInstance(s_accessorType)!;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
