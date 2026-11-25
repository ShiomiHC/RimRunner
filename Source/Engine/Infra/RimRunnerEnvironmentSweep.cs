namespace RimRunner;

/// <summary>测试场环境清扫：清走地图上非交棒的 pawn、尸体、产 pawn 设施、火与孤儿。</summary>
public static class RimRunnerEnvironmentSweep
{
    private static bool loggedOnce;

    /// <summary>装配期整场清扫图上残骸并归一组件状态，引擎已在 World 级用例开窗前与场面立场时调。</summary>
    /// <remarks>清走非交棒的全部 pawn（含休眠的）、尸体及其内层 pawn、非玩家阵营带 CompSpawnerPawn 的可毁建筑（不掉落）、火与孤儿 pawn，pawn 销毁并丢弃。只许在场景装配期调，用例体内调即抛。</remarks>
    public static void SweepAll(RimRunnerContext ctx)
    {
        SweepAll(RequireMap(ctx, nameof(SweepAll)), ctx.IsHeldForCheckpoint);
    }

    internal static void SweepAll(Map map, Func<Thing, bool> held)
    {
        RequireOrchestrated(nameof(SweepAll));
        RequireAssembling();
        int pawns = RecyclePawns(PawnCandidates(map), held);
        int corpses = RecycleCorpses(CorpseCandidates(map), held);
        int spawners = DestroyPawnSpawners(map, PawnSpawnerCandidates(map), held);
        int fires = DestroyFires(FireCandidates(map), held);
        int orphans = RimRunnerOrphanSweep.Sweep();
        RimRunnerComponentState.Normalize();

        if (!loggedOnce || pawns > 0 || corpses > 0 || spawners > 0 || fires > 0 || orphans > 0)
        {
            loggedOnce = true;
            RimRunnerEngineLog.Info("环境清扫：销毁 " + pawns + " 个非测试 pawn、"
                                    + corpses + " 具尸体、" + spawners + " 座产 pawn 设施、"
                                    + fires + " 处火、" + orphans + " 个孤儿 pawn");
        }
    }

    /// <summary>清走图上非交棒的全部 pawn（含休眠的），返回清走数量。</summary>
    /// <remarks>装配期与用例体内都可调。本用例经引擎生成并登记的 pawn 同样清走，其收尾回收随之撤销。</remarks>
    public static int SweepPawns(RimRunnerContext ctx)
    {
        RequireOrchestrated(nameof(SweepPawns));
        return RecyclePawns(PawnCandidates(RequireMap(ctx, nameof(SweepPawns))), ctx.IsHeldForCheckpoint);
    }

    internal static List<Pawn> PawnCandidates(Map map) =>
        map.listerThings.ThingsInGroup(ThingRequestGroup.Pawn).Cast<Pawn>().ToList();

    internal static List<Corpse> CorpseCandidates(Map map) =>
        map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Cast<Corpse>().ToList();

    internal static List<Thing> PawnSpawnerCandidates(Map map) =>
        map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial)
            .Where(thing => thing.Faction != Faction.OfPlayer && thing.def.destroyable
                                                              && thing.TryGetComp<CompSpawnerPawn>() != null)
            .ToList();

    internal static List<Thing> FireCandidates(Map map) =>
        map.listerThings.ThingsOfDef(ThingDefOf.Fire).ToList();

    internal static int RecyclePawns(IEnumerable<Pawn> candidates, Func<Thing, bool> held)
    {
        int swept = 0;
        foreach (Pawn pawn in candidates.ToList())
        {
            if (!pawn.Spawned || held(pawn))
            {
                continue;
            }

            using (Subject(pawn, pawn))
            {
                RimRunnerPawns.Cleanup(pawn);
            }

            swept++;
        }

        return swept;
    }

    private static IDisposable Subject(Thing thing, Pawn? arrivalOf) =>
        RimRunnerLogMonitor.GapSubject(DescribeSubject(thing, arrivalOf));

    internal static string DescribeSubject(Thing thing, Pawn? arrivalOf)
    {
        string? arrival = RimRunnerArrivalTag.Describe(thing, null)
                         ?? (arrivalOf != null && arrivalOf != thing ? RimRunnerArrivalTag.Describe(arrivalOf, null) : null);
        return "清 " + thing.ThingID + (arrival != null ? "（" + arrival + "）" : "");
    }

    internal static int RecycleCorpses(IEnumerable<Corpse> candidates, Func<Thing, bool> held)
    {
        int swept = 0;
        foreach (Corpse corpse in candidates.ToList())
        {
            if (corpse.Destroyed || held(corpse))
            {
                continue;
            }

            Pawn? inner = corpse.Bugged ? null : corpse.InnerPawn;
            using (Subject(corpse, inner))
            {
                corpse.Destroy();
                if (inner != null)
                {
                    RimRunnerPawns.Cleanup(inner);
                }
            }

            swept++;
        }

        return swept;
    }

    internal static int DestroyPawnSpawners(Map map, IEnumerable<Thing> candidates, Func<Thing, bool> held)
    {
        List<Thing> spawners = candidates.Where(thing => !thing.Destroyed && !held(thing)).ToList();
        if (spawners.Count == 0)
        {
            return 0;
        }

        List<Pawn> colonists = ColonistsWithoutHiveMemory(map);
        foreach (Thing spawner in spawners)
        {
            using (Subject(spawner, null))
            {
                spawner.Destroy();
            }
        }

        RemoveHiveMemory(colonists);
        return spawners.Count;
    }

    internal static int DestroyFires(IEnumerable<Thing> candidates, Func<Thing, bool> held)
    {
        int swept = 0;
        foreach (Thing fire in candidates.ToList())
        {
            if (fire.Destroyed || held(fire))
            {
                continue;
            }

            using (Subject(fire, null))
            {
                fire.Destroy();
            }

            swept++;
        }

        return swept;
    }

    internal static List<Pawn> ColonistsWithoutHiveMemory(Map map) =>
        map.mapPawns.FreeColonistsSpawned
            .Where(pawn => pawn.needs?.mood?.thoughts?.memories?
                .GetFirstMemoryOfDef(ThoughtDefOf.DefeatedInsectHive) == null)
            .ToList();

    internal static void RemoveHiveMemory(IEnumerable<Pawn> colonists)
    {
        foreach (Pawn pawn in colonists)
        {
            pawn.needs?.mood?.thoughts?.memories?.RemoveMemoriesOfDef(ThoughtDefOf.DefeatedInsectHive);
        }
    }

    private static void RequireOrchestrated(string entry)
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " RimRunnerEnvironmentSweep." + entry
                + " 只在 -" + RimRunnerEngineArgs.Enable + " 实例上执行");
        }
    }

    private static void RequireAssembling()
    {
        if (!RimRunnerScenarioManager.Assembling)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " RimRunnerEnvironmentSweep.SweepAll 仅允许在场景装配期（Scenario.Ensure）调用");
        }
    }

    private static Map RequireMap(RimRunnerContext ctx, string entry)
    {
        return ctx.Map ?? throw new InvalidOperationException(RimRunnerLog.Prefix + " RimRunnerEnvironmentSweep." + entry
            + " 需要 ctx.Map：只有 Level 为 World 的用例在装配期与用例体内有地图");
    }
}
