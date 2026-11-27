// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CollectibleCacheProbe;

internal static class Program
{
    private static object? s_held;
    private static int s_roundTrips;
    private static bool s_required;

    private sealed class Context(string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) =>
            name.Name == typeof(DefaultBase).Assembly.GetName().Name ? typeof(DefaultBase).Assembly : null;
    }

    private sealed class Foreign(string path)
    {
        public readonly Context Context = new("foreign");
        public Type Type = null!;
        public object Value = null!;
        public JsonSerializerOptions Options = null!;
        public void Initialize()
        {
            Type = Context.LoadFromAssemblyPath(path).GetType("CollectibleCacheProbe.Fixture.Dto")!;
            Value = Activator.CreateInstance(Type)!;
            Options = NewOptions();
        }
    }

    private static int Main(string[] args)
    {
        string mode = args[0]; // no, session, default, held, equivalent
        string fixturePath = Path.GetFullPath(args[1]);
        bool patched = args.Length > 2 && args[2] == "patched";
        s_required = args.Contains("required");
        bool expectUnload = mode == "no" || (patched && mode == "session");
        Assembly stj = typeof(JsonSerializer).Assembly;
        Console.WriteLine($"IDENTITY runtime={Environment.Version} corelib={typeof(object).Assembly.Location} corelibTfm={typeof(object).Assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName} stj={stj.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(stj.Location)))} tfm={stj.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName}");
        Console.WriteLine($"MODE {mode} expectUnload={expectUnload} required={s_required} cleanup=none");

        if (mode == "accessor")
        {
            AccessorCases.Run(fixturePath, patched);
            return 0;
        }
        if (mode == "identity") return 0;

        (bool success, WeakReference foreignRef) = Run(mode, fixturePath, patched, expectUnload);
        bool foreignUnloaded = Collect([foreignRef]);
        Console.WriteLine($"FOREIGN_RELEASE collected={foreignUnloaded}");
        if (mode == "no" || patched) success &= foreignUnloaded;
        Console.WriteLine($"RESULT {(success ? "PASS" : "FAIL")} roundTrips={s_roundTrips}");
        return success ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (bool Success, WeakReference Foreign) Run(string mode, string fixturePath, bool patched, bool expectUnload)
    {

        Foreign? foreign = new(fixturePath);
        foreign.Initialize();
        if (mode != "no")
        {
            RoundTrip(new DefaultBase(), typeof(DefaultBase), NewOptions());
            RoundTrip(foreign.Value, foreign.Type, foreign.Options);
        }

        bool success = true;
        for (int round = 1; round <= 2; round++)
        {
            WeakReference[] refs = Exercise(mode, fixturePath);
            bool unloaded = Collect(refs);
            Console.WriteLine($"ROUND {round} authorAlive={refs[0].IsAlive} frameworkAlive={refs[1].IsAlive} collected={unloaded}");
            success &= unloaded == expectUnload;
            if (mode == "held")
            {
                s_held = null;
                bool afterRelease = Collect(refs);
                Console.WriteLine($"HELD_RELEASE round={round} collected={afterRelease}");
                success &= afterRelease == patched; // baseline accessor cache remains; patched must release
            }
            if (mode != "no")
            {
                RoundTrip(foreign.Value, foreign.Type, foreign.Options);
                RoundTrip(new DefaultBase(), typeof(DefaultBase), NewOptions());
                Console.WriteLine($"FOREIGN round={round} usable=True identity={AssemblyLoadContext.GetLoadContext(foreign.Type.Assembly)?.Name}");
            }
        }

        WeakReference foreignRef = ReleaseForeign(ref foreign);
        return (success, foreignRef);
    }

    private static JsonSerializerOptions NewOptions() => new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(), // unique, session-owned context identity
        IncludeFields = true,
        MaxDepth = 128,
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Exercise(string mode, string path)
    {
        AssemblyLoadContext author = CreateAuthor();
        Context framework = new("framework");
        Assembly a = author.LoadFromAssemblyPath(path), f = framework.LoadFromAssemblyPath(path);
        Type at = a.GetType("CollectibleCacheProbe.Fixture.Dto")!, ft = f.GetType("CollectibleCacheProbe.Fixture.Dto")!;
        if (at == ft || !at.IsCollectible || !ft.IsCollectible)
            throw new Exception("Context identity/collectibility failure.");
        object av = Activator.CreateInstance(at)!, fv = Activator.CreateInstance(ft)!;
        if (mode != "no")
        {
            JsonSerializerOptions options = mode == "default" ? JsonSerializerOptions.Default : NewOptions();
            if (mode == "equivalent")
            {
                // Same resolver/configuration as permanently live Default: intentional sharing control.
                options = new JsonSerializerOptions(JsonSerializerOptions.Default);
            }
            JsonTypeInfo info = options.GetTypeInfo(at);
            if (mode == "held") s_held = (options, info);
            Parallel.For(0, 4, _ =>
            {
                for (int i = 0; i < 12; i++)
                {
                    RoundTrip(av, at, options);
                    RoundTrip(fv, ft, options);
                }
            });

            Type pair = typeof(Pair<,>).MakeGenericType(at, ft);
            object pv = Activator.CreateInstance(pair)!;
            pair.GetProperty("Left")!.SetValue(pv, av);
            pair.GetProperty("Right")!.SetValue(pv, fv);
            RoundTrip(pv, pair, options);
            if (pair.Assembly.IsCollectible || !pair.IsCollectible) throw new Exception("Closed generic IsCollectible control.");
            PropertyInfo inherited = at.GetProperty(nameof(DefaultBase.Inherited))!;
            if (inherited.DeclaringType != typeof(DefaultBase) || !inherited.IsCollectible)
                throw new Exception("Inherited ReflectedType control.");

            Type deep = at;
            object dv = av;
            for (int i = 0; i < 12; i++)
            {
                deep = typeof(Nest<>).MakeGenericType(deep);
                object outer = Activator.CreateInstance(deep)!;
                deep.GetProperty("Value")!.SetValue(outer, dv);
                dv = outer;
            }
            RoundTrip(dv, deep, options);
            Array array = Array.CreateInstance(at, 1);
            array.SetValue(av, 0);
            RoundTrip(array, array.GetType(), options);
            RoundTrip(Activator.CreateInstance(typeof(Stack<>).MakeGenericType(at))!, typeof(Stack<>).MakeGenericType(at), options);
            RoundTrip(Activator.CreateInstance(typeof(ImmutableArray<>).MakeGenericType(at))!, typeof(ImmutableArray<>).MakeGenericType(at), options, "[]");
            Type dict = typeof(ImmutableDictionary<,>).MakeGenericType(typeof(string), at);
            object empty = dict.GetField("Empty", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
            RoundTrip(empty, dict, options);
            RoundTrip(null, a.GetType("CollectibleCacheProbe.Fixture.SmallConstructor")!, options, "{\"Text\":\"small\",\"Number\":9}");
            RoundTrip(null, a.GetType("CollectibleCacheProbe.Fixture.LargeConstructor")!, options, "{\"A\":1,\"B\":2,\"C\":3,\"D\":4,\"E\":\"large\"}");

            Type matrix = a.GetType("CollectibleCacheProbe.Fixture.Matrix`3")!;
            Type[] stable = [typeof(int), typeof(string), typeof(long), typeof(bool), typeof(double), typeof(byte), typeof(decimal), typeof(Guid)];
            for (int i = 0; i < 300; i++)
            {
                Type t = matrix.MakeGenericType(stable[i % 8], stable[(i / 8) % 8], stable[(i / 64) % 8]);
                RoundTrip(Activator.CreateInstance(t)!, t, options);
            }
            // Fresh final accessor prevents elapsed TTL/earlier evictions from hiding the regression.
            Type last = matrix.MakeGenericType(ft, typeof(int), typeof(int));
            RoundTrip(Activator.CreateInstance(last)!, last, options);
        }
        WeakReference[] result = [new(author), new(framework)];
        author.Unload();
        framework.Unload();
        return result;
    }

    private static void RoundTrip(object? value, Type type, JsonSerializerOptions options, string? input = null)
    {
        string json = input ?? JsonSerializer.Serialize(value, type, options);
        object? result = JsonSerializer.Deserialize(json, type, options);
        string actual = JsonSerializer.Serialize(result, type, options);
        if (input is null && actual != json) throw new Exception($"Round-trip mismatch: {type}");
        if (result is null) throw new Exception("Unexpected null.");
        Interlocked.Increment(ref s_roundTrips);
    }

    private static AssemblyLoadContext CreateAuthor()
    {
        if (!s_required) return new Context("author");
        // Optional matching-fork loader check. This creates the context only; it performs no cache cleanup.
        MethodInfo factory = typeof(AssemblyLoadContext).GetMethod("RuneCLR_CreateInterpreterRequired", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new PlatformNotSupportedException("Matching interpreter-required loader is absent.");
        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolver = static (_, name) =>
            name.Name == typeof(DefaultBase).Assembly.GetName().Name ? typeof(DefaultBase).Assembly : null;
        AssemblyLoadContext context = (AssemblyLoadContext)factory.Invoke(null, ["author-required", resolver])!;
        MethodInfo policy = typeof(AssemblyLoadContext).GetMethod("RuneCLR_GetExecutionPolicy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (!context.IsCollectible || (int)policy.Invoke(context, null)! != 1) throw new Exception("Interpreter-required policy mismatch.");
        Console.WriteLine("POLICY author=1 collectible=True");
        return context;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReleaseForeign(ref Foreign? foreign)
    {
        WeakReference result = new(foreign!.Context);
        foreign.Context.Unload();
        foreign = null;
        return result;
    }

    private static bool Collect(WeakReference[] refs)
    {
        for (int i = 0; i < 24; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (refs.All(r => !r.IsAlive)) return true;
            Thread.Sleep(60); // no serialization/cache activity while observing unload
        }
        return false;
    }
}
