using RimWorld.Planet;

namespace RimRunner;

/// <summary>拆袋装图的入口。</summary>
public static class RimRunnerMapTeardown
{
    /// <summary>拆掉一张袋装图：图上 pawn 先回收，再拆图。</summary>
    /// <remarks>生成后没推过 tick 时拆前先推进 1 tick；图上 pawn（含容器里的）走 RimRunnerPawns.Cleanup，不进世界池；当前地图是该图时切回来源图，来源图已拆时切到任一其他图。map 为 null 或已拆时直接返回。</remarks>
    public static void TearDownPocketMap(Map? map)
    {
        if (map == null || map.Disposed)
        {
            return;
        }

        if (!Find.Maps.Contains(map))
        {
            return;
        }

        TearDownLive(map, (map.Parent as PocketMapParent)?.sourceMap);
    }

    private static void TearDownLive(Map target, Map? returnTo)
    {
        if (RimRunnerCurrentMapProbe.EngineCurrentMap() == target)
        {
            Current.Game.CurrentMap = returnTo != null && returnTo != target && IsLive(returnTo)
                ? returnTo
                : Find.Maps.FirstOrDefault(candidate => candidate != target);
        }

        if (RimRunnerMapLifecyclePatches.GeneratedThisTick(target))
        {
            RimRunnerTimeDriver.AdvanceBurst(1);
        }

        foreach (Pawn pawn in target.mapPawns.AllPawns.ToList())
        {
            RimRunnerPawns.Cleanup(pawn);
        }

        RimRunnerMapLifecyclePatches.RunAsEngineTeardown(() =>
        {
            if (target.Parent is PocketMapParent)
            {
                PocketMapUtility.DestroyPocketMap(target);
            }
            else
            {
                Current.Game.DeinitAndRemoveMap(target, notifyPlayer: false);
            }
        });
    }

    private static bool IsLive([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Map? candidate) =>
        candidate != null && !candidate.Disposed && Find.Maps.Contains(candidate);

    internal sealed class GeneratedMap
    {
        private RimRunnerContext ctx;
        private readonly int returnToId;
        private readonly HashSet<string> worldBefore;
        private readonly HashSet<string> orphansBefore;
        private Map? map;
        private int mapId = -1;

        internal Map? Map
        {
            get => map;
            set
            {
                map = value;
                mapId = value?.uniqueID ?? -1;
            }
        }

        internal string Description { get; }

        internal GeneratedMap(RimRunnerContext ctx, string description)
        {
            this.ctx = ctx;
            Description = description;
            returnToId = RimRunnerCurrentMapProbe.EngineCurrentMap()?.uniqueID ?? -1;
            worldBefore = new HashSet<string>(Find.WorldPawns.AllPawnsAliveOrDead.Select(p => p.ThingID),
                StringComparer.Ordinal);
            orphansBefore = new HashSet<string>(RimRunnerOrphanSweep.Snapshot().Select(p => p.ThingID),
                StringComparer.Ordinal);
        }

        internal bool Live => IsLive(map);

        internal void Rebind(RimRunnerContext? live)
        {
            if (live != null)
            {
                ctx = live;
            }

            map = mapId < 0 ? null : Find.Maps.FirstOrDefault(candidate => candidate.uniqueID == mapId);
        }

        internal void Finish()
        {
            RimRunnerIsolatedMap.ReleaseGenerated(map);
            if (IsLive(map))
            {
                TearDown(map);
            }

            HashSet<Pawn> keep = [.. Find.WorldPawns.AllPawnsAliveOrDead.Where(p =>
                worldBefore.Contains(p.ThingID) || RimRunnerSession.IsHeldByCheckpointBatch(p.ThingID))];
            List<Pawn> added = Find.WorldPawns.AllPawnsAliveOrDead.Where(p => !keep.Contains(p)).ToList();
            foreach (Pawn pawn in added)
            {
                if (ctx.TracksForRecycle(pawn))
                {
                    RimRunnerPawns.Cleanup(pawn);
                }
            }

            RimRunnerPawns.DiscardWorldPawnsAddedSince(keep);
            HashSet<Pawn> orphansKept = [.. RimRunnerOrphanSweep.Snapshot()
                .Where(p => orphansBefore.Contains(p.ThingID))];
            int swept = RimRunnerOrphanSweep.SweepAddedSince(orphansKept);
            ctx.AddDiagnostic(RimRunnerDiagnosticKind.Trace, "GeneratedMap-Discarded",
                Description + "：世界池新增 " + added.Count + "，孤儿 " + swept);

            List<Pawn> lingering = Find.WorldPawns.AllPawnsAliveOrDead.Where(p => !keep.Contains(p)).ToList();
            if (lingering.Count > 0)
            {
                RimRunnerSentinel.Raise("生成图收尾后基线之外的 pawn 仍滞留世界池——丢弃时未收走这些 pawn，"
                                        + "它们会进入此后每一次存档",
                    Description + " " + string.Join(",", lingering.Select(p => p.ThingID)));
            }
        }

        private void TearDown(Map target)
        {
            Map? returnTo = Find.Maps.FirstOrDefault(candidate => candidate.uniqueID == returnToId);
            TearDownLive(target, returnTo != null && returnTo != target ? returnTo : ctx.Map);
        }
    }
}
