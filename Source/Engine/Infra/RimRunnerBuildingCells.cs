using Verse.AI;

namespace RimRunner;

/// <summary>多格建筑落点筛选：占地与交互格均须在界内且可站。</summary>
/// <remarks>占地与交互格按朝北计，每格须在界内、可站、无建筑、无 pawn，由近及远取第一个合格格。找不到合格格时按用例前提不成立认领，对象按格列出挡格理由与 Thing 来路。</remarks>
public static class RimRunnerBuildingCells
{
    /// <summary>以 origin 为心按半径找一个可放 def 的格。</summary>
    public static IntVec3 FindBuildingCell(
        Map map, ThingDef def, IntVec3 origin, float radius)
    {
        if (map == null)
        {
            throw new ArgumentNullException(nameof(map));
        }

        if (def == null)
        {
            throw new ArgumentNullException(nameof(def));
        }

        foreach (IntVec3 cell in GenRadial.RadialCellsAround(origin, radius, useCenter: true))
        {
            if (IsRectClear(map, cell, def, requireClearItems: false)
                && InteractionClear(map, cell, def, requireClearItems: false, reachFrom: null))
            {
                return cell;
            }
        }

        RimRunnerFault.Require(false, NoCellReason,
            RimRunnerCellBlockers.MapLabel(map) + " 上原点 " + origin + " 半径 " + radius + " 内的 " + def.defName
            + Blockers(map, def, GenRadial.RadialCellsAround(origin, radius, useCenter: true), false, null));
        throw new InvalidOperationException(NoCellReason);
    }

    internal const string NoCellReason = "半径内找不到建筑空位";

    internal const string NoReachableCellReason = "半径内找不到 worker 可达的建筑空位";

    /// <summary>以 worker 为心找可放 def 的格，另须各格无物品且 worker 走得到交互格。</summary>
    /// <remarks>def 没有交互格时要求 worker 走得到落点格；radius 缺省 50。</remarks>
    public static IntVec3 FindReachableBuildingCell(
        Map map, ThingDef def, Pawn worker, float radius = 50f)
    {
        if (map == null)
        {
            throw new ArgumentNullException(nameof(map));
        }

        if (def == null)
        {
            throw new ArgumentNullException(nameof(def));
        }

        if (worker == null)
        {
            throw new ArgumentNullException(nameof(worker));
        }

        foreach (IntVec3 cell in GenRadial.RadialCellsAround(
                     worker.Position, radius, useCenter: true))
        {
            if (IsRectClear(map, cell, def, requireClearItems: true)
                && InteractionClear(map, cell, def, requireClearItems: true, reachFrom: worker))
            {
                return cell;
            }
        }

        RimRunnerFault.Require(false, NoReachableCellReason,
            RimRunnerCellBlockers.MapLabel(map) + " 上 " + (worker.LabelShort ?? worker.ThingID) + " 周围 " + radius + " 格内的 "
            + def.defName + Blockers(map, def, GenRadial.RadialCellsAround(worker.Position, radius, useCenter: true), true, worker));
        throw new InvalidOperationException(NoReachableCellReason);
    }

    internal static string Blockers(Map map, ThingDef def, IEnumerable<IntVec3> candidates, bool requireClearItems,
        Pawn? reachFrom)
    {
        string described = BlockerStats(map, def, candidates, requireClearItems, reachFrom).Describe();
        return described.Length == 0 ? "" : "。" + described;
    }

    internal static RimRunnerCellBlockers BlockerStats(Map map, ThingDef def, IEnumerable<IntVec3> candidates,
        bool requireClearItems, Pawn? reachFrom)
    {
        RimRunnerCellBlockers blockers = new(map);
        int unreachable = 0;
        foreach (IntVec3 cell in candidates)
        {
            bool clear = true;
            foreach (IntVec3 footprint in FootprintOf(cell, def))
            {
                RimRunnerCellBlockers.Reason? reason = Check(map, footprint, requireClearItems, out Thing? thing);
                if (reason != null)
                {
                    blockers.AddCell(footprint, reason.Value, thing);
                    clear = false;
                }
            }

            if (clear && reachFrom != null)
            {
                unreachable++;
            }
        }

        blockers.AddPlacements("落点格都合格但 worker 无法到达", unreachable);
        return blockers;
    }

    private static IEnumerable<IntVec3> FootprintOf(IntVec3 cell, ThingDef def)
    {
        foreach (IntVec3 occupied in GenAdj.OccupiedRect(cell, Rot4.North, def.Size))
        {
            yield return occupied;
        }

        if (def.hasInteractionCell)
        {
            yield return cell + def.interactionCellOffset.RotatedBy(Rot4.North);
        }
    }

    private static bool IsRectClear(
        Map map, IntVec3 cell, ThingDef def, bool requireClearItems)
    {
        CellRect rect = GenAdj.OccupiedRect(cell, Rot4.North, def.Size);
        return rect.Cells.All(candidate => IsClear(map, candidate, requireClearItems));
    }

    private static bool InteractionClear(
        Map map, IntVec3 cell, ThingDef def, bool requireClearItems, Pawn? reachFrom)
    {
        if (!def.hasInteractionCell)
        {
            return reachFrom == null || reachFrom.CanReach(cell, PathEndMode.OnCell, Danger.Deadly);
        }

        IntVec3 interaction = cell + def.interactionCellOffset.RotatedBy(Rot4.North);
        if (!IsClear(map, interaction, requireClearItems))
        {
            return false;
        }

        return reachFrom == null
               || reachFrom.CanReach(interaction, PathEndMode.OnCell, Danger.Deadly);
    }

    private static bool IsClear(Map map, IntVec3 cell, bool requireClearItems)
        => Check(map, cell, requireClearItems, out _) == null;

    private static RimRunnerCellBlockers.Reason? Check(Map map, IntVec3 cell, bool requireClearItems, out Thing? blocker)
    {
        blocker = null;
        if (!cell.InBounds(map))
        {
            return RimRunnerCellBlockers.Reason.OutOfBounds;
        }

        blocker = cell.GetFirstBuilding(map) ?? (Thing?)cell.GetFirstPawn(map)
            ?? (requireClearItems ? cell.GetFirstItem(map) : null);
        if (blocker != null)
        {
            return RimRunnerCellBlockers.Reason.Thing;
        }

        return cell.Standable(map) ? null : RimRunnerCellBlockers.Reason.Unstandable;
    }
}
