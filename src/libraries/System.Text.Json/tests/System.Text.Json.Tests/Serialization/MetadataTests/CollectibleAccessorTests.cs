// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;

namespace System.Text.Json.Serialization.Tests
{
    public static class CollectibleAccessorTests
    {
        private static object? s_retained;

        public static bool IsSupported => RemoteExecutor.IsSupported && RuntimeFeature.IsDynamicCodeSupported;

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public static void SessionOwnedMetadataDoesNotPreventUnload(bool serialize)
        {
            RemoteExecutor.Invoke(static argument =>
            {
                // Isolate the process-static accessor cache from unrelated serializer requests.
                for (int round = 0; round < 2; round++)
                {
                    WeakReference[] references = Exercise(bool.Parse(argument), retain: false, useDefault: false);
                    Assert.True(WaitForCollection(references), Describe(references));
                }
            }, serialize.ToString()).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public static void RetainedMetadataKeepsBothContextsAliveUntilReleased()
        {
            RemoteExecutor.Invoke(static () =>
            {
                WeakReference[] references = Exercise(serialize: true, retain: true, useDefault: false);
                try
                {
                    Collect();
                    Assert.All(references, reference => Assert.True(reference.IsAlive));
                }
                finally
                {
                    s_retained = null;
                }

                Assert.True(WaitForCollection(references), Describe(references));
            }).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public static void DefaultOptionsStillOwnTheirMetadata()
        {
            RemoteExecutor.Invoke(static () =>
            {
                WeakReference[] references = Exercise(serialize: true, retain: false, useDefault: true);
                // Overwrite Default's root-type fast slot with a stable type. Its live context
                // dictionary must still own the earlier collectible metadata.
                JsonSerializer.Serialize(42, JsonSerializerOptions.Default);
                Collect();
                Assert.All(references.Take(6), reference => Assert.True(reference.IsAlive));
            }).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public static void StableAccessorsAreReusedAndCollectibleDelegateArgumentsAreNotCached()
        {
            RemoteExecutor.Invoke(static () =>
            {
                Type accessorType = typeof(JsonSerializer).Assembly.GetType(
                    "System.Text.Json.Serialization.Metadata.ReflectionEmitCachingMemberAccessor", throwOnError: true)!;
                object accessor = Activator.CreateInstance(accessorType)!;
                MethodInfo getterFactory = accessorType.GetMethods().Single(method =>
                    method.Name == "CreatePropertyGetter" && method.GetGenericArguments().Length == 1).MakeGenericMethod(typeof(int));
                PropertyInfo stableProperty = typeof(StableBase).GetProperty(nameof(StableBase.Value))!;
                Delegate stable = (Delegate)getterFactory.Invoke(accessor, new object[] { stableProperty })!;
                Assert.Same(stable, getterFactory.Invoke(accessor, new object[] { stableProperty }));
                Assert.Equal(13, stable.DynamicInvoke(new StableBase { Value = 13 }));

                AssemblyLoadContext context = new("accessor-delegate", isCollectible: true);
                try
                {
                    Type collectible = context.LoadFromAssemblyPath(typeof(CollectibleDto).Assembly.Location)
                        .GetType(typeof(InheritedDto).FullName!, throwOnError: true)!;
                    object value = Activator.CreateInstance(collectible)!;
                    PropertyInfo reflectedProperty = collectible.GetProperty(nameof(Collection<int>.Count))!;
                    Assert.Same(typeof(Collection<int>), reflectedProperty.DeclaringType);
                    Assert.True(reflectedProperty.IsCollectible);
                    Delegate getter = (Delegate)getterFactory.Invoke(accessor, new object[] { reflectedProperty })!;
                    Assert.NotSame(getter, getterFactory.Invoke(accessor, new object[] { reflectedProperty }));
                    Assert.Equal(0, getter.DynamicInvoke(value));

                    MethodInfo constructorFactory = accessorType.GetMethods().Single(method =>
                        method.Name == "CreateParameterizedConstructor" && method.GetGenericArguments().Length == 5);
                    ConstructorInfo constructor = typeof(StableConstructor).GetConstructors().Single();
                    for (int slot = 0; slot < 4; slot++)
                    {
                        Type[] arguments = { typeof(StableConstructor), typeof(object), typeof(object), typeof(object), typeof(object) };
                        arguments[slot + 1] = collectible;
                        MethodInfo factory = constructorFactory.MakeGenericMethod(arguments);
                        Delegate create = (Delegate)factory.Invoke(accessor, new object[] { constructor })!;
                        Assert.NotSame(create, factory.Invoke(accessor, new object[] { constructor }));
                        Assert.Same(value, ((StableConstructor)create.DynamicInvoke(value, null, null, null)!).Value);
                    }
                }
                finally
                {
                    context.Unload();
                }
            }).Dispose();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] Exercise(bool serialize, bool retain, bool useDefault)
        {
            AssemblyLoadContext first = new("first", isCollectible: true);
            AssemblyLoadContext second = new("second", isCollectible: true);
            Assembly a = first.LoadFromAssemblyPath(typeof(CollectibleDto).Assembly.Location);
            Assembly b = second.LoadFromAssemblyPath(typeof(CollectibleDto).Assembly.Location);
            Type at = a.GetType(typeof(CollectibleDto).FullName!, throwOnError: true)!;
            Type bt = b.GetType(typeof(CollectibleDto).FullName!, throwOnError: true)!;
            object av = Activator.CreateInstance(at)!;
            object bv = Activator.CreateInstance(bt)!;
            at.GetProperty(nameof(CollectibleDto.Text))!.SetValue(av, "first-value");
            bt.GetProperty(nameof(CollectibleDto.Text))!.SetValue(bv, "second-value");
            Assert.NotSame(at, bt);

            List<WeakReference> references = new() { new(first), new(second), new(at), new(bt), new(a), new(b) };
            if (serialize)
            {
                JsonSerializerOptions options = useDefault ? JsonSerializerOptions.Default : new()
                {
                    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                };
                using Barrier barrier = new(4);
                Task[] workers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
                {
                    barrier.SignalAndWait();
                    RoundTrip(av, at, options);
                    RoundTrip(bv, bt, options);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                Task.WaitAll(workers);

                Type pairType = typeof(KeyValuePair<,>).MakeGenericType(at, bt);
                object pair = Activator.CreateInstance(pairType, av, bv)!;
                RoundTrip(pair, pairType, options);
                JsonTypeInfo info = options.GetTypeInfo(pairType);
                Assert.True(options.IsReadOnly);
                Assert.Same(info, options.GetTypeInfo(pairType));
                references.AddRange(new[] { new WeakReference(options), new(options.TypeInfoResolver!), new(info) });
                if (retain)
                {
                    s_retained = info;
                }
            }

            first.Unload();
            second.Unload();
            return references.ToArray();
        }

        private static void RoundTrip(object value, Type type, JsonSerializerOptions options)
        {
            string json = JsonSerializer.Serialize(value, type, options);
            object clone = JsonSerializer.Deserialize(json, type, options)!;
            Assert.Same(type, clone.GetType());
            Assert.Equal(json, JsonSerializer.Serialize(clone, type, options));
        }

        private static bool WaitForCollection(WeakReference[] references)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                Collect();
                if (references.All(reference => !reference.IsAlive))
                {
                    return true;
                }

                Thread.Sleep(20);
            }

            return false;
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static string Describe(WeakReference[] references) =>
            "Retained references: " + string.Join(", ", references.Select(reference => reference.IsAlive));

        public class StableBase
        {
            public int Value { get; set; }
        }

        public sealed class CollectibleDto
        {
            public string? Text { get; set; }
        }

        public sealed class InheritedDto : Collection<int>
        {
        }

        public sealed class StableConstructor
        {
            public StableConstructor(object value) => Value = value;
            public object Value { get; }
        }
    }
}
#endif
