using Verse.AI;

namespace RimRunner;

internal static class RimRunnerBuildSaveReloadCheck
{
    internal const int Ticks = 2500;
    private static readonly List<string> hits = [];
    private static readonly SortedDictionary<string, SortedSet<string>> tickDriven = new(StringComparer.Ordinal);

    internal static bool Armed { get; private set; }

    internal static void Arm()
    {
        hits.Clear();
        tickDriven.Clear();
        Armed = true;
    }

    internal static void Disarm()
    {
        Armed = false;
        hits.Clear();
        tickDriven.Clear();
    }

    internal static void OnBlockingWindowPushed(string line)
    {
        if (Armed)
        {
            hits.Add("阻断窗 " + line);
        }
    }

    internal static void OnConditionRegistered(GameConditionManager? manager, GameCondition? condition)
    {
        if (!Armed || condition == null)
        {
            return;
        }

        hits.Add("game condition " + (condition.def?.defName ?? condition.GetType().Name)
                 + " tick " + Find.TickManager.TicksGame + " 登记于"
                 + (manager?.ownerMap == null ? "世界层" : "地图 " + manager.ownerMap));
    }

    internal static void OnJobStarted(Pawn pawn, Job job)
    {
        if (Armed && IsResearch(job))
        {
            hits.Add(DescribeJob(pawn, job, "开工"));
        }
    }

    internal static int Run()
    {
        foreach (Pawn pawn in PawnsFinder.AllMaps_Spawned)
        {
            if (IsResearch(pawn.CurJob))
            {
                hits.Add(DescribeJob(pawn, pawn.CurJob, "载入时正在做"));
            }
        }

        RimRunnerStorytellerQuiesce.Ensure("建档回载验证");
        Dictionary<string, KeyValuePair<string, string>> before = RimRunnerComponentState.ForeignViews();
        int start = Find.TickManager.TicksGame;
        RimRunnerTimeDriver.AdvanceBurst(Ticks);
        Find.TickManager.Pause();
        foreach (KeyValuePair<string, KeyValuePair<string, string>> after in RimRunnerComponentState.ForeignViews())
        {
            if (!before.TryGetValue(after.Key, out KeyValuePair<string, string> view))
            {
                continue;
            }

            string[] changed = RimRunnerComponentState.ChangedElements(view.Value, after.Value.Value);
            if (changed.Length == 0)
            {
                continue;
            }

            if (!tickDriven.TryGetValue(after.Value.Key, out SortedSet<string>? elements))
            {
                elements = new SortedSet<string>(StringComparer.Ordinal);
                tickDriven[after.Value.Key] = elements;
            }

            elements.UnionWith(changed);
        }

        return Find.TickManager.TicksGame - start;
    }

    internal static SortedDictionary<string, string[]> TakeTickDriven()
    {
        SortedDictionary<string, string[]> taken = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, SortedSet<string>> pair in tickDriven)
        {
            taken[pair.Key] = [.. pair.Value];
        }

        return taken;
    }

    internal static List<string> TakeHits()
    {
        List<string> taken = [.. hits];
        Disarm();
        return taken;
    }

    private static bool IsResearch(Job? job)
    {
        Type? driver = job?.def?.driverClass;
        return driver != null
               && (typeof(JobDriver_Research).IsAssignableFrom(driver)
                   || typeof(JobDriver_StudyInteract).IsAssignableFrom(driver)
                   || typeof(JobDriver_StudyItem).IsAssignableFrom(driver));
    }

    private static string DescribeJob(Pawn pawn, Job job, string verb)
    {
        Thing target = job.targetA.Thing;
        return "研究 " + pawn.LabelShort + " tick " + Find.TickManager.TicksGame + " " + verb + " " + job.def.defName
               + (target == null ? "" : "（对象 " + target.def.defName + "）");
    }
}

[HarmonyPatch(typeof(GameConditionManager), nameof(GameConditionManager.RegisterCondition))]
internal static class Patch_GameConditionManager_RegisterCondition_RimRunnerBuildSaveReloadCheck
{
    private static void Postfix(GameConditionManager __instance, GameCondition cond)
    {
        RimRunnerBuildSaveReloadCheck.OnConditionRegistered(__instance, cond);
    }
}

[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
internal static class Patch_Pawn_JobTracker_StartJob_RimRunnerBuildSaveReloadCheck
{
    private static void Postfix(Pawn_JobTracker __instance, Job newJob, Pawn ___pawn)
    {
        if (RimRunnerBuildSaveReloadCheck.Armed && ReferenceEquals(__instance.curJob, newJob))
        {
            RimRunnerBuildSaveReloadCheck.OnJobStarted(___pawn, newJob);
        }
    }
}
