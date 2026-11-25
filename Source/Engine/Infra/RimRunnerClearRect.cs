namespace RimRunner;

/// <summary>矩形空地查找：在地图上找一块 width×height 的空矩形。</summary>
/// <remarks>每格须在界内、未起雾、无屋顶、地形可站，格上除 mote 外没有 Thing。同一地图状态恒返回同一块。找不到合格块时按用例前提不成立认领，对象按格列出挡格理由与 Thing 来路。</remarks>
public static class RimRunnerClearRect
{
    /// <summary>找一块空矩形：在全部界内位置里取中心离 near 最近的第一块合格矩形。</summary>
    /// <remarks>距离按 Chebyshev 距离算，同距按曼哈顿距离、再按 z、x 取。只按地图状态取块，同一地图状态再调一次返回同一块。near 缺省为地图中心，植物与污物缺省算挡格。</remarks>
    /// <param name="map">要找的地图。</param>
    /// <param name="width">x 向格数。</param>
    /// <param name="height">z 向格数。</param>
    /// <param name="near">矩形中心离 near 越近越先取；缺省 map.Center。</param>
    /// <param name="cellFilter">追加的逐格判据，每格都须为真。</param>
    /// <param name="rectFilter">追加的整块判据：逐格都合格的块再交给它判定。</param>
    /// <param name="ignorePlantsAndFilth">为真时格上的植物（含树）与污物不算挡格。</param>
    /// <returns>找到的矩形。</returns>
    public static CellRect Find(Map map, int width, int height, IntVec3? near = null,
        Func<IntVec3, bool>? cellFilter = null, Func<CellRect, bool>? rectFilter = null, bool ignorePlantsAndFilth = false)
    {
        CellRect found = Search(map, width, height, near, cellFilter, rectFilter, ignorePlantsAndFilth, out Failure? failure);
        if (failure != null)
        {
            RimRunnerFault.Require(false, NoRectReason, failure.Text);
            throw new InvalidOperationException(failure.Text);
        }

        return found;
    }

    internal const string NoRectReason = "找不到空矩形";
    internal const string RectFilterLabel = "不满足调用方整块判据";

    internal enum FailureKind
    {
        TooLarge,
        NoCandidate,
    }

    internal sealed class Failure
    {
        internal FailureKind Kind;
        internal required string Head;
        internal IntVec3 Origin;
        internal int Candidates;
        internal RimRunnerCellBlockers? Blockers;

        internal string Text => Kind == FailureKind.TooLarge
            ? Head + "：图比矩形小"
            : Head + "：中心 " + Origin + "，界内位置 " + Candidates + " 处，无一合格"
              + (Blockers?.Describe() is { Length: > 0 } described ? "。" + described : "");
    }

    internal static CellRect Search(Map map, int width, int height, IntVec3? near,
        Func<IntVec3, bool>? cellFilter, Func<CellRect, bool>? rectFilter, bool ignorePlantsAndFilth, out Failure? failure)
    {
        if (map == null)
        {
            throw new ArgumentNullException(nameof(map));
        }

        if (width < 1 || height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width + "×" + height, "矩形边长须不小于 1");
        }

        int sizeX = map.Size.x;
        int sizeZ = map.Size.z;
        string head = RimRunnerCellBlockers.MapLabel(map) + " 上找不到 " + width + "×" + height + " 的空矩形";
        if (width > sizeX || height > sizeZ)
        {
            failure = new Failure { Kind = FailureKind.TooLarge, Head = head };
            return default;
        }

        IntVec3 origin = near ?? map.Center;
        int[] bad = new int[(sizeX + 1) * (sizeZ + 1)];
        for (int z = 0; z < sizeZ; z++)
        {
            for (int x = 0; x < sizeX; x++)
            {
                bool blocked = Check(map, new IntVec3(x, 0, z), cellFilter, ignorePlantsAndFilth, out _) != null;
                bad[(z + 1) * (sizeX + 1) + x + 1] = (blocked ? 1 : 0)
                    + bad[z * (sizeX + 1) + x + 1] + bad[(z + 1) * (sizeX + 1) + x] - bad[z * (sizeX + 1) + x];
            }
        }

        List<(int Ring, int Manhattan, int Z, int X)> candidates = [];
        for (int z = 0; z + height <= sizeZ; z++)
        {
            for (int x = 0; x + width <= sizeX; x++)
            {
                int dx = Math.Abs(2 * x + width - 1 - 2 * origin.x);
                int dz = Math.Abs(2 * z + height - 1 - 2 * origin.z);
                candidates.Add((Math.Max(dx, dz), dx + dz, z, x));
            }
        }

        candidates.Sort();
        int rejected = 0;
        foreach ((_, _, int z, int x) in candidates)
        {
            int count = bad[(z + height) * (sizeX + 1) + x + width] - bad[z * (sizeX + 1) + x + width]
                        - bad[(z + height) * (sizeX + 1) + x] + bad[z * (sizeX + 1) + x];
            if (count > 0)
            {
                continue;
            }

            CellRect rect = new(x, z, width, height);
            if (rectFilter == null || rectFilter(rect))
            {
                failure = null;
                return rect;
            }

            rejected++;
        }

        RimRunnerCellBlockers blockers = new(map);
        foreach (IntVec3 cell in map.AllCells)
        {
            RimRunnerCellBlockers.Reason? reason = Check(map, cell, cellFilter, ignorePlantsAndFilth, out Thing? thing);
            if (reason != null)
            {
                blockers.AddCell(cell, reason.Value, thing);
            }
        }

        blockers.AddPlacements(RectFilterLabel, rejected);
        failure = new Failure
        {
            Kind = FailureKind.NoCandidate,
            Head = head,
            Origin = origin,
            Candidates = candidates.Count,
            Blockers = blockers,
        };
        return default;
    }

    private static RimRunnerCellBlockers.Reason? Check(Map map, IntVec3 cell, Func<IntVec3, bool>? cellFilter,
        bool ignorePlantsAndFilth, out Thing? blocker)
    {
        blocker = null;
        if (cell.Fogged(map))
        {
            return RimRunnerCellBlockers.Reason.Fogged;
        }

        if (cell.Roofed(map))
        {
            return RimRunnerCellBlockers.Reason.Roofed;
        }

        List<Thing> things = cell.GetThingList(map);
        for (int i = 0; i < things.Count; i++)
        {
            ThingCategory category = things[i].def.category;
            if (category == ThingCategory.Mote
                || (ignorePlantsAndFilth && category is ThingCategory.Plant or ThingCategory.Filth))
            {
                continue;
            }

            blocker = things[i];
            return RimRunnerCellBlockers.Reason.Thing;
        }

        if (cell.GetTerrain(map).passability != Traversability.Standable)
        {
            return RimRunnerCellBlockers.Reason.Unstandable;
        }

        if (cellFilter != null && !cellFilter(cell))
        {
            return RimRunnerCellBlockers.Reason.CellPredicate;
        }

        return null;
    }
}
