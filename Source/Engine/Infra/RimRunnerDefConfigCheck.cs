using System.Diagnostics;

namespace RimRunner;

internal static class RimRunnerDefConfigCheck
{
    internal enum Outcome
    {
        NotRun,
        Ran,
        VanillaRan
    }

    internal static Outcome State { get; private set; }

    internal static readonly HashSet<Type> CheckedTypes = [];

    internal static bool RanBeforeSnapshot { get; private set; }

    internal static string? PassStackSample { get; private set; }

    internal static bool FromThisPass(string? stackTrace) =>
        stackTrace != null
        && (stackTrace.Contains(PassFrame + "." + nameof(RunUnlessVanillaDid), StringComparison.Ordinal)
            || stackTrace.Contains(PassFrame + ":" + nameof(RunUnlessVanillaDid), StringComparison.Ordinal));

    private static readonly string PassFrame = typeof(RimRunnerDefConfigCheck).FullName!;

    internal static void RunUnlessVanillaDid()
    {
        if (State != Outcome.NotRun)
        {
            return;
        }

        if (Prefs.DevMode)
        {
            State = Outcome.VanillaRan;
            return;
        }

        RanBeforeSnapshot = !RimRunnerStartupErrors.Captured;
        PassStackSample = StackTraceUtility.ExtractStackTrace();
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            foreach (Type defType in typeof(Def).AllSubclasses())
            {
                typeof(DefDatabase<>).MakeGenericType(defType)
                    .GetMethod(nameof(DefDatabase<Def>.ErrorCheckAllDefs), BindingFlags.Public | BindingFlags.Static)!
                    .Invoke(null, null);
                CheckedTypes.Add(defType);
            }
        }
        catch (Exception exception)
        {
            Exception cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            RimRunnerEngineFault.Raise("Def 配置检查中途失败，已查 " + CheckedTypes.Count + " 个 Def 类型："
                                       + cause.GetType().Name + ": " + cause.Message);
        }

        State = Outcome.Ran;
        RimRunnerEngineLog.Info("Def 配置检查：本实例 devMode 关闭，由引擎运行 vanilla 的 ErrorCheckAllDefs，共 "
                                + CheckedTypes.Count + " 个 Def 类型，用时 " + watch.ElapsedMilliseconds + " ms");
    }
}
