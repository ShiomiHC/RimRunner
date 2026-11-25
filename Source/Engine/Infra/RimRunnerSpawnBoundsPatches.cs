namespace RimRunner;

internal static class RimRunnerSpawnBoundsGuard
{
    private static readonly List<IntVec3> InteractionCells = [];

    internal static void Inspect(Thing? result, Thing? newThing, IntVec3 loc, Map? map, Rot4 rot)
    {
        string? caseId = RimRunnerObservationBuffer.ActiveCaseId;
        if (caseId == null || newThing == null)
        {
            return;
        }

        if (result == null)
        {
            ReportOutOfBoundsSpawn(caseId, newThing, loc, map, rot);
            return;
        }

        ReportOutOfBoundsInteractionCell(caseId, result);
    }

    private static void ReportOutOfBoundsSpawn(
        string caseId, Thing newThing, IntVec3 loc, Map? map, Rot4 rot)
    {
        if (map == null)
        {
            return;
        }

        IntVec2 size = newThing.def?.Size ?? IntVec2.One;
        bool cellOut = !loc.InBounds(map);
        bool rectOut = !GenAdj.OccupiedRect(loc, rot, size).InBounds(map);
        if (!cellOut && !rectOut)
        {
            return;
        }

        RimRunnerSentinel.Raise(caseId + " 落点越界：spawn 返回 null",
            Describe(newThing.def) + " loc=" + loc + " rot=" + rot
            + " size=" + size + " 图=" + map.Size
            + (cellOut ? " 中心格越界" : "") + (rectOut ? " 占块越界" : ""));
    }

    private static void ReportOutOfBoundsInteractionCell(string caseId, Thing thing)
    {
        Map map = thing.Map;
        if (!thing.Spawned || map == null || thing.def == null)
        {
            return;
        }

        ThingUtility.InteractionCellsWhenAt(
            InteractionCells, thing.def, thing.Position, thing.Rotation, map,
            allowFallbackCell: false);
        if (InteractionCells.Count == 0)
        {
            return;
        }

        List<IntVec3>? outside = null;
        for (int i = 0; i < InteractionCells.Count; i++)
        {
            int idx = map.cellIndices.CellToIndex(InteractionCells[i]);
            if (idx < 0 || idx >= map.cellIndices.NumGridCells)
            {
                outside ??= [];
                outside.Add(InteractionCells[i]);
            }
        }

        if (outside == null)
        {
            return;
        }

        RimRunnerSentinel.Warn(RimRunnerWarningKey.InteractionCell, caseId + " 交互格越界：CanReserve 会在 GetEdifice 抛 IndexOutOfRangeException",
            Describe(thing.def) + " 落在 " + thing.Position + " rot=" + thing.Rotation
            + " size=" + thing.def.Size + " 图=" + map.Size
            + "，界外交互格 " + string.Join("、", outside.ConvertAll(cell => cell.ToString()).ToArray()));
    }

    private static string Describe(ThingDef? def)
    {
        return def == null ? "<def=null>" : def.defName;
    }
}

[HarmonyPatch]
internal static class Patch_GenSpawn_Spawn_RimRunnerBounds
{
    private static MethodBase? target;

    private static bool Prepare()
    {
        if (target != null)
        {
            return true;
        }

        target = AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn),
        [
            typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4),
            typeof(WipeMode), typeof(bool), typeof(bool)
        ]);

        if (target == null)
        {
            RimRunnerEngineFault.Raise("落点守卫挂载失败：无法解析 GenSpawn.Spawn 的七参重载。"
                      + "本守卫本轮不生效");
            return false;
        }

        return true;
    }

    private static MethodBase TargetMethod()
    {
        return target!;
    }

    private static void Prefix(Thing newThing, Map map, bool respawningAfterLoad)
    {
        try
        {
            RimRunnerArrivalTag.OnSpawn(newThing, map, respawningAfterLoad);
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("入场打标自身异常：" + exception);
        }
    }

    private static void Postfix(Thing __result, Thing newThing, IntVec3 loc, Map map, Rot4 rot)
    {
        try
        {
            RimRunnerSpawnBoundsGuard.Inspect(__result, newThing, loc, map, rot);
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("落点守卫自身异常：" + exception);
        }

        try
        {
            RimRunnerFootprintProbe.OnSpawn(__result);
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("足迹探针自身异常：" + exception);
        }
    }
}
