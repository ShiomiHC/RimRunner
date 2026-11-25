namespace RimRunner;

public sealed class RimRunnerEarlyPatchesMod : Mod
{
    public RimRunnerEarlyPatchesMod(ModContentPack content) : base(content)
    {
        RimRunnerEarlyPatches.Install();
    }
}

internal static class RimRunnerEarlyPatches
{
    internal const string HarmonyOwner = "rimrunner.early";

    [ThreadStatic] internal static int BaseValueHits;

    private static bool installed;

    internal static List<Type> CheckedWorkerTypes { get; } = [];

    internal static void Install()
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            return;
        }

        var harmony = new Harmony(HarmonyOwner);
        harmony.Patch(AccessTools.Method(typeof(StatWorker), nameof(StatWorker.GetBaseValueFor), [typeof(StatRequest)]),
            postfix: new HarmonyMethod(typeof(Patch_StatWorker_GetBaseValueFor_RimRunnerDummy),
                nameof(Patch_StatWorker_GetBaseValueFor_RimRunnerDummy.Postfix)));
        installed = true;
    }

    internal static void AssertAtStartup()
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            return;
        }

        if (!installed)
        {
            RimRunnerEngineFault.Raise("stat 底值的补丁没有在 Mod 构造里挂上：WithStat 本进程不生效");
            return;
        }

        var findings = new Findings();
        try
        {
            CheckBaseValue(BaseWorkerStats(), () => BaseValueHits, findings, CheckedWorkerTypes);
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("启动期补丁核对自身失败，本进程的 WithStat 是否生效无从判定", exception);
            return;
        }

        if (findings.Unreached.Count > 0)
        {
            RimRunnerEngineFault.Raise("启动期补丁被调用方绕过（调用方在补丁挂上之前已把被补方法内联进去），"
                                       + "本进程里经这些调用方的 WithStat 不生效：" + string.Join("；", findings.Unreached));
        }

        if (findings.Undecided.Count > 0)
        {
            RimRunnerEngineFault.Raise("启动期补丁核对无从判定这些类型（到达被补方法之前就抛出）："
                                       + string.Join("；", findings.Undecided));
        }

        if (findings.Unreached.Count == 0 && findings.Undecided.Count == 0)
        {
            if (CheckedWorkerTypes.Count == 0)
            {
                RimRunnerEngineFault.Raise("启动期补丁核对一个 stat worker 类型都没取到，本进程的 WithStat 是否生效无从判定");
            }
            else
            {
                RimRunnerEngineLog.Info("启动期补丁核对：" + CheckedWorkerTypes.Count + " 个 stat worker 类型的求值都经过补丁");
            }
        }
    }

    internal sealed class Findings
    {
        internal List<string> Unreached { get; } = [];
        internal List<string> Undecided { get; } = [];
    }

    internal static IEnumerable<StatDef> BaseWorkerStats() =>
        DefDatabase<StatDef>.AllDefsListForReading
            .GroupBy(stat => stat.Worker.GetType())
            .Where(group => RimRunnerDummySpec.BaseHookBypasser(group.Key, out _) == null)
            .Select(group => group.First());

    internal static void CheckBaseValue(IEnumerable<StatDef> stats, Func<int> hits, Findings findings, List<Type> checkedTypes)
    {
        StatRequest request = StatRequest.For(ThingDefOf.Steel, null);
        foreach (StatDef stat in stats)
        {
            int before = hits();
            Exception? thrown = null;
            bool suppressed = stat.supressDisabledError;
            stat.supressDisabledError = true;
            try
            {
                stat.Worker.GetValueUnfinalized(request);
            }
            catch (Exception exception)
            {
                thrown = exception;
            }
            finally
            {
                stat.supressDisabledError = suppressed;
            }

            checkedTypes.Add(stat.Worker.GetType());
            if (hits() != before)
            {
                continue;
            }

            string subject = "worker " + stat.Worker.GetType().FullName + " 经 GetValueUnfinalized 取底值（stat " + stat.defName + "）";
            if (thrown != null)
            {
                findings.Undecided.Add(subject + "：" + thrown.GetType().Name);
            }
            else
            {
                findings.Unreached.Add(subject + "（WithStat）");
            }
        }
    }
}
