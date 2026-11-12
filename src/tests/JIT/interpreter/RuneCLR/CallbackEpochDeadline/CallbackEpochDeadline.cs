// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Private RuneCLR experiment: end-to-end check of the opt-in callback deadline on the
// interpreter-required load context. The test binds the same private AssemblyLoadContext
// surface the trusted host uses, then runs author bodies loaded a second time into that
// context so the runtime under test interprets them.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Threading;

namespace RuneCLR.CallbackEpochDeadline
{
    public static class Author
    {
        public static long CleanupRan;

        public static long Spin(long iterations)
        {
            long acc = 0;
            for (long i = 0; i < iterations; i++)
            {
                acc = acc + (i * 3);
                acc = acc ^ (acc >> 7);
                acc = acc - (i & 15);
                if (acc < 0)
                    acc = -acc;
            }
            return acc;
        }

        public static int Swallow(long iterations)
        {
            try
            {
                return (int)Spin(iterations);
            }
            catch (OperationCanceledException)
            {
                // A terminated callback must not be able to continue by catching.
                return 1;
            }
        }

        public static int Filtered(long iterations)
        {
            try
            {
                return (int)Spin(iterations);
            }
            catch (Exception) when (FilterAlways(iterations))
            {
                return 2;
            }
        }

        private static bool FilterAlways(long value) => value >= 0;

        public static long FiniteCleanup(long iterations)
        {
            CleanupRan = 0;
            try
            {
                return Spin(iterations);
            }
            finally
            {
                CleanupRan = 1;
            }
        }

        public static bool InfiniteFinally(long iterations)
        {
            try
            {
                return Spin(iterations) >= 0;
            }
            finally
            {
                for (;;)
                {
                    CleanupRan++;
                }
            }
        }

        public static long Reenter(long iterations) => Spin(iterations) + Spin(2);

        public sealed class SpinConstructor
        {
            public long Value;

            public SpinConstructor(long iterations)
            {
                Value = Spin(iterations);
            }
        }
    }

    internal static class DeadlineApi
    {
        internal static readonly Type Alc = typeof(AssemblyLoadContext);
        private const BindingFlags StaticFlags = BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags InstanceFlags = BindingFlags.NonPublic | BindingFlags.Instance;

        private static MethodInfo Find(string name, BindingFlags flags)
        {
            MethodInfo? method = Alc.GetMethod(name, flags);
            if (method is null)
                throw new MissingMethodException(Alc.FullName, name);
            return method;
        }

        internal static readonly MethodInfo Version = Find("RuneCLR_GetCallbackDeadlineVersion", StaticFlags);
        internal static readonly MethodInfo Capabilities = Find("RuneCLR_GetCallbackDeadlineCapabilities", StaticFlags);
        internal static readonly MethodInfo Tick = Find("RuneCLR_TickCallbackDeadlineEpoch", StaticFlags);
        internal static readonly MethodInfo Read = Find("RuneCLR_ReadCallbackDeadlineEpoch", StaticFlags);
        internal static readonly MethodInfo CreateClock = Find("RuneCLR_CreateCallbackDeadline", StaticFlags);
        internal static readonly MethodInfo CreateEpoch = Find("RuneCLR_CreateCallbackDeadlineEpoch", StaticFlags);
        internal static readonly MethodInfo BeginDeadline = Find("RuneCLR_BeginCallbackDeadline", InstanceFlags);
        internal static readonly MethodInfo EndDeadline = Find("RuneCLR_EndCallbackDeadline", InstanceFlags);
        internal static readonly MethodInfo QueryDeadline = Find("RuneCLR_QueryCallbackDeadline", InstanceFlags);

        internal static long VersionValue() => (long)Version.Invoke(null, null)!;
        internal static int CapabilityValue() => (int)Capabilities.Invoke(null, null)!;
        internal static long TickValue() => (long)Tick.Invoke(null, null)!;
        internal static long ReadValue() => (long)Read.Invoke(null, null)!;
        internal static long Begin(AssemblyLoadContext context) => (long)BeginDeadline.Invoke(context, null)!;
        internal static bool End(AssemblyLoadContext context, long token) => (bool)EndDeadline.Invoke(context, new object[] { token })!;
        internal static long Query(AssemblyLoadContext context, int field) => (long)QueryDeadline.Invoke(context, new object[] { field })!;
    }

    public static class Program
    {
        private const long InfiniteIterations = long.MaxValue / 4;
        private const int PollInterval = 1;
        private const long CleanupBudget = 1000;
        private const long EpochPeriodMicros = 1000;
        private static readonly Func<AssemblyLoadContext, AssemblyName, Assembly?> Resolver = (context, name) => null;

        private static int s_failures;
        private static volatile bool s_tickerStop;
        private static long s_tickCount;

        public static int Main()
        {
            using Timer watchdog = new Timer(_ => { Console.WriteLine("WATCHDOG: test did not finish in time"); Environment.Exit(2); }, null, 180_000, Timeout.Infinite);

            Check("version", DeadlineApi.VersionValue() == 1);
            int capabilities = DeadlineApi.CapabilityValue();
            Check("capability-clock", (capabilities & 1) != 0);
            Check("capability-epoch", (capabilities & 2) != 0);

            StartTicker();
            try
            {
                EpochTerminates();
                ClockTerminates();
                CatchCannotContinue();
                FilterCannotContinue();
                FiniteCleanupRuns();
                InfiniteFinallyIsAbandoned();
                ConstructorTerminates();
                ReentryTerminates();
                UnconfiguredContextIsUnchanged();
                NextCallbackAfterTermination();
                Overhead();
            }
            finally
            {
                StopTicker();
            }

            Console.WriteLine(s_failures == 0 ? "PASS" : "FAIL: " + s_failures + " check(s) failed");
            return s_failures == 0 ? 0 : 1;
        }

        private static void StartTicker()
        {
            Thread ticker = new Thread(() =>
            {
                while (!s_tickerStop)
                {
                    DeadlineApi.TickValue();
                    s_tickCount++;
                    Thread.Sleep(1);
                }
            });
            ticker.IsBackground = true;
            ticker.Start();
        }

        private static void StopTicker() => s_tickerStop = true;

        private static AssemblyLoadContext CreateEpochContext(long deadline100ns, long cleanupBudget)
            => CreateEpochContextAt(deadline100ns, cleanupBudget, PollInterval);

        private static AssemblyLoadContext CreateEpochContextAt(long deadline100ns, long cleanupBudget, int interval)
            => (AssemblyLoadContext)DeadlineApi.CreateEpoch.Invoke(null, new object[] { "epoch", Resolver, deadline100ns, interval, cleanupBudget, EpochPeriodMicros })!;

        private static AssemblyLoadContext CreateClockContext(long deadline100ns, long cleanupBudget)
            => (AssemblyLoadContext)DeadlineApi.CreateClock.Invoke(null, new object[] { "clock", Resolver, deadline100ns, PollInterval, cleanupBudget })!;

        private static Assembly LoadAuthor(AssemblyLoadContext context)
            => context.LoadFromAssemblyPath(typeof(Program).Assembly.Location);

        private static object? Call(Assembly assembly, string typeName, string methodName, params object?[] arguments)
        {
            Type type = assembly.GetType(typeName) ?? throw new InvalidOperationException("missing type " + typeName);
            MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("missing method " + methodName);
            try
            {
                return method.Invoke(null, arguments);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static Exception? RunTerminated(AssemblyLoadContext context, string methodName, long iterations)
        {
            Assembly author = LoadAuthor(context);
            long token = DeadlineApi.Begin(context);
            if (token == 0)
                throw new InvalidOperationException("begin failed");
            try
            {
                Call(author, "RuneCLR.CallbackEpochDeadline.Author", methodName, iterations);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
            finally
            {
                DeadlineApi.End(context, token);
            }
        }

        private static void EpochTerminates()
        {
            long start = DeadlineApi.ReadValue();
            AssemblyLoadContext context = CreateEpochContext(50_000, CleanupBudget);
            WaitForQuery(context, 9, budget => budget > 0);
            Stopwatch watch = Stopwatch.StartNew();
            Exception? error = RunTerminated(context, "Spin", InfiniteIterations);
            watch.Stop();
            long end = DeadlineApi.ReadValue();
            long budgetTicks = DeadlineApi.Query(context, 9);

            Check("epoch-throws", error is OperationCanceledException);
            Check("epoch-mode-flag", DeadlineApi.Query(context, 8) == 1);
            Check("epoch-budget", budgetTicks == 6);
            Check("epoch-elapsed-ticks", end - start >= budgetTicks);
            Check("epoch-reason", DeadlineApi.Query(context, 6) == 2);
            Check("epoch-admissions", DeadlineApi.Query(context, 2) > 0);
            long startEpoch = DeadlineApi.Query(context, 15);
            long lastEpoch = DeadlineApi.Query(context, 16);
            long detected = lastEpoch - startEpoch;
            Check("epoch-detected-at-budget", detected >= budgetTicks && detected <= budgetTicks + 2);
            Console.WriteLine("epoch: budget=" + budgetTicks + " ticks detected=" + detected + " observed=" + (end - start) + " wall=" + watch.ElapsedMilliseconds + "ms");
            context.Unload();
        }

        private static void ClockTerminates()
        {
            AssemblyLoadContext context = CreateClockContext(50_000, CleanupBudget);
            Stopwatch watch = Stopwatch.StartNew();
            Exception? error = RunTerminated(context, "Spin", InfiniteIterations);
            watch.Stop();
            Check("clock-throws", error is OperationCanceledException);
            Check("clock-mode-flag", DeadlineApi.Query(context, 8) == 0);
            Check("clock-reason", DeadlineApi.Query(context, 6) == 2);
            Console.WriteLine("clock: wall=" + watch.ElapsedMilliseconds + "ms");
            context.Unload();
        }

        private static void CatchCannotContinue()
        {
            AssemblyLoadContext context = CreateEpochContext(50_000, CleanupBudget);
            Exception? error = RunTerminated(context, "Swallow", InfiniteIterations);
            Check("catch-cannot-continue", error is OperationCanceledException);
            context.Unload();
        }

        private static void FilterCannotContinue()
        {
            AssemblyLoadContext context = CreateEpochContext(50_000, CleanupBudget);
            Exception? error = RunTerminated(context, "Filtered", InfiniteIterations);
            Check("filter-cannot-continue", error is OperationCanceledException);
            context.Unload();
        }

        private static void FiniteCleanupRuns()
        {
            AssemblyLoadContext context = CreateEpochContext(50_000, CleanupBudget);
            Assembly author = LoadAuthor(context);
            long token = DeadlineApi.Begin(context);
            Exception? error = null;
            try
            {
                Call(author, "RuneCLR.CallbackEpochDeadline.Author", "FiniteCleanup", InfiniteIterations);
            }
            catch (Exception caught)
            {
                error = caught;
            }
            finally
            {
                DeadlineApi.End(context, token);
            }
            object? ran = author.GetType("RuneCLR.CallbackEpochDeadline.Author")!
                .GetField("CleanupRan", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
            Check("cleanup-throws", error is OperationCanceledException);
            Check("cleanup-ran", (long)ran! == 1);
            Console.WriteLine("cleanup: remaining=" + DeadlineApi.Query(context, 11) + " grants=" + DeadlineApi.Query(context, 12) + " funclets=" + DeadlineApi.Query(context, 13) + " trace=0x" + DeadlineApi.Query(context, 14).ToString("X"));
            context.Unload();
        }

        private static void InfiniteFinallyIsAbandoned()
        {
            AssemblyLoadContext context = CreateEpochContext(50_000, CleanupBudget);
            Stopwatch watch = Stopwatch.StartNew();
            Exception? error = RunTerminated(context, "InfiniteFinally", InfiniteIterations);
            watch.Stop();
            Check("infinite-finally-throws", error is OperationCanceledException);
            Check("infinite-finally-bounded", watch.ElapsedMilliseconds < 30_000);
            Check("infinite-finally-phase", DeadlineApi.Query(context, 6) == 2);
            context.Unload();
        }

        private static void ConstructorTerminates()
        {
            AssemblyLoadContext context = CreateEpochContext(50_000, CleanupBudget);
            Assembly author = LoadAuthor(context);
            Type type = author.GetType("RuneCLR.CallbackEpochDeadline.Author+SpinConstructor")!;
            long token = DeadlineApi.Begin(context);
            Exception? error = null;
            try
            {
                Activator.CreateInstance(type, new object[] { InfiniteIterations });
            }
            catch (TargetInvocationException caught) when (caught.InnerException is not null)
            {
                error = caught.InnerException;
            }
            catch (Exception caught)
            {
                error = caught;
            }
            finally
            {
                DeadlineApi.End(context, token);
            }
            Check("ctor-throws", error is OperationCanceledException);
            context.Unload();
        }

        private static void ReentryTerminates()
        {
            AssemblyLoadContext context = CreateEpochContext(50_000, CleanupBudget);
            Exception? error = RunTerminated(context, "Reenter", InfiniteIterations);
            Check("reentry-throws", error is OperationCanceledException);
            context.Unload();
        }

        private static void UnconfiguredContextIsUnchanged()
        {
            MethodInfo create = DeadlineApi.Alc.GetMethod("RuneCLR_CreateInterpreterRequired",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            AssemblyLoadContext context = (AssemblyLoadContext)create.Invoke(null, new object[] { "plain", Resolver })!;
            Assembly author = LoadAuthor(context);
            object? value = Call(author, "RuneCLR.CallbackEpochDeadline.Author", "Spin", 2_000_000L);
            Check("off-completes", value is long && (long)value == ReferenceSpin(2_000_000));
            Check("off-begin-rejected", IsRejected(() => DeadlineApi.Begin(context)));
            context.Unload();
        }

        private static bool IsRejected(Action action)
        {
            try { action(); return false; }
            catch (Exception error) { return error is PlatformNotSupportedException || error.InnerException is PlatformNotSupportedException; }
        }

        private static long ReferenceSpin(long iterations) => Author.Spin(iterations);

        private static void NextCallbackAfterTermination()
        {
            AssemblyLoadContext terminated = CreateEpochContext(50_000, CleanupBudget);
            Exception? error = RunTerminated(terminated, "Spin", InfiniteIterations);
            Check("next-terminated", error is OperationCanceledException);
            terminated.Unload();

            AssemblyLoadContext next = CreateEpochContext(60_000_000, CleanupBudget);
            Assembly author = LoadAuthor(next);
            long token = DeadlineApi.Begin(next);
            object? value = null;
            bool endResult = false;
            try
            {
                value = Call(author, "RuneCLR.CallbackEpochDeadline.Author", "Spin", 100_000L);
            }
            finally
            {
                endResult = DeadlineApi.End(next, token);
            }
            Check("next-end-ok", endResult);
            Check("next-good", value is long && (long)value == ReferenceSpin(100_000));
            next.Unload();
        }

        private static void Overhead()
        {
            const long iterations = 3_000_000;
            MethodInfo create = DeadlineApi.Alc.GetMethod("RuneCLR_CreateInterpreterRequired",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            AssemblyLoadContext plain = (AssemblyLoadContext)create.Invoke(null, new object[] { "plain2", Resolver })!;
            Assembly plainAuthor = LoadAuthor(plain);
            AssemblyLoadContext metered = CreateEpochContext(60_000_000, CleanupBudget);
            Assembly meteredAuthor = LoadAuthor(metered);

            long offBest = long.MaxValue;
            long onBest = long.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                Call(plainAuthor, "RuneCLR.CallbackEpochDeadline.Author", "Spin", iterations);
                watch.Stop();
                offBest = Math.Min(offBest, watch.ElapsedTicks);

                long token = DeadlineApi.Begin(metered);
                watch.Restart();
                Call(meteredAuthor, "RuneCLR.CallbackEpochDeadline.Author", "Spin", iterations);
                watch.Stop();
                Check("overhead-end-ok", DeadlineApi.End(metered, token));
                onBest = Math.Min(onBest, watch.ElapsedTicks);
            }

            double ratio = (double)onBest / offBest;
            Console.WriteLine("overhead: off=" + TicksToMs(offBest).ToString("F1") + "ms on=" + TicksToMs(onBest).ToString("F1") + "ms ratio=" + ratio.ToString("F3"));

            // The host chooses the poll cadence; interval 100 is the coarse profile.
            AssemblyLoadContext coarse = CreateEpochContextAt(60_000_000, CleanupBudget, 100);
            Assembly coarseAuthor = LoadAuthor(coarse);
            long coarseBest = long.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                long token = DeadlineApi.Begin(coarse);
                Stopwatch watch = Stopwatch.StartNew();
                Call(coarseAuthor, "RuneCLR.CallbackEpochDeadline.Author", "Spin", iterations);
                watch.Stop();
                Check("coarse-end-ok", DeadlineApi.End(coarse, token));
                coarseBest = Math.Min(coarseBest, watch.ElapsedTicks);
            }
            Console.WriteLine("overhead-coarse100: on=" + TicksToMs(coarseBest).ToString("F1") + "ms ratio=" + ((double)coarseBest / offBest).ToString("F3"));
            plain.Unload();
            metered.Unload();
            coarse.Unload();
        }

        private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static void WaitForQuery(AssemblyLoadContext context, int field, Func<long, bool> condition)
        {
            for (int i = 0; i < 1000; i++)
            {
                if (condition(DeadlineApi.Query(context, field)))
                    return;
                Thread.Sleep(1);
            }
        }

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                Console.WriteLine("PASS " + name);
            }
            else
            {
                s_failures++;
                Console.WriteLine("FAIL " + name);
            }
        }
    }
}