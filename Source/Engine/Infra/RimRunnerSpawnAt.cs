namespace RimRunner;

/// <summary>pawn 落点：锚定干净格、距锚点的距离带、或指定格；没有格即中止。</summary>
/// <remarks>地图缺省取 ctx.Map，取格在 pawn 生成之后、落图之前。没有格时回收已生成的 pawn 再中止：出界、不可站、找不到格按用例前提不成立认领。</remarks>
public sealed class RimRunnerSpawnAt
{
    private readonly Kind kind;
    private readonly IntVec3 cell;
    private readonly int minDistance;
    private readonly int maxDistance;
    private readonly Func<IntVec3, bool>? cellFilter;
    private readonly Map? map;

    private RimRunnerSpawnAt(Kind kind, IntVec3 cell, int minDistance, int maxDistance, Func<IntVec3, bool>? cellFilter, Map? map)
    {
        this.kind = kind;
        this.cell = cell;
        this.minDistance = minDistance;
        this.maxDistance = maxDistance;
        this.cellFilter = cellFilter;
        this.map = map;
    }

    private enum Kind
    {
        Anchored,
        Near,
        Cell
    }

    /// <summary>锚定落点：从 ctx.Map 中心起由近及远的第一个干净格。</summary>
    /// <remarks>取格不消耗随机流，干净格判据同 RimRunnerPawns.AnchoredDropSpot，前一个占的格，下一个跳过。</remarks>
    public static RimRunnerSpawnAt Anchored { get; } = new(Kind.Anchored, IntVec3.Invalid, 0, 0, null, null);

    /// <summary>锚定落点，取指定地图。</summary>
    public static RimRunnerSpawnAt AnchoredOn(Map map)
        => new(Kind.Anchored, IntVec3.Invalid, 0, 0, null,
            map ?? throw new ArgumentNullException(nameof(map), RimRunnerLog.Prefix + " AnchoredOn 须给地图"));

    /// <summary>距锚点的距离带内取可站、可达且有视线的随机格，可加谓词。</summary>
    /// <remarks>cellFilter 与距离带、可站、从锚点不穿关着的门与围栏可达、有视线这四条并列筛格，半径内无一格全中才抛。选格消耗随机流。</remarks>
    public static RimRunnerSpawnAt Near(IntVec3 anchor, int minDistance, int maxDistance,
        Func<IntVec3, bool>? cellFilter = null, Map? map = null)
    {
        if (minDistance < 0 || maxDistance < minDistance)
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " RimRunnerSpawnAt.Near 距离带无效：" + minDistance + ".." + maxDistance);
        }

        return new(Kind.Near, anchor, minDistance, maxDistance, cellFilter, map);
    }

    /// <summary>指定格，格不在地图内、不可站或已有 pawn 即中止。</summary>
    /// <remarks>map 未指定时取 ctx.Map，用例不在图上时须传。</remarks>
    public static RimRunnerSpawnAt Cell(IntVec3 cell, Map? map = null)
        => new(Kind.Cell, cell, 0, 0, null, map);

    internal Map MapFor(RimRunnerContext ctx) => map ?? ctx.Map
        ?? throw new InvalidOperationException(RimRunnerLog.Prefix + " 落点未指定地图，而 ctx.Map 为空");

    internal IntVec3 Resolve(RimRunnerContext ctx)
    {
        Map target = MapFor(ctx);
        switch (kind)
        {
            case Kind.Anchored:
                return RimRunnerPawns.AnchoredDropSpot(target);
            case Kind.Cell:
                RimRunnerFault.Require(cell.InBounds(target), CellOutOfBoundsReason, cell + "（图尺寸 " + target.Size + "）");
                RimRunnerFault.Require(cell.Standable(target), CellUnstandableReason, cell.ToString());
                Pawn? occupant = cell.GetFirstPawn(target);
                if (occupant != null)
                {
                    throw new InvalidOperationException(RimRunnerLog.Prefix + " 指定落点 " + cell + " 上已有 " + occupant.LabelShort);
                }

                return cell;
            case Kind.Near:
                int minDistanceSquared = minDistance * minDistance;
                if (!CellFinder.TryRandomClosewalkCellNear(cell, target, maxDistance, out IntVec3 found,
                        c => c.DistanceToSquared(cell) >= minDistanceSquared
                             && GenSight.LineOfSight(cell, c, target)
                             && (cellFilter == null || cellFilter(c))))
                {
                    RimRunnerFault.Require(false, NoNearCellReason,
                        "距 " + cell + " " + minDistance + ".." + maxDistance + " 格" + (cellFilter != null ? "（含调用方 cellFilter）" : ""));
                }

                return found;
            default:
                throw new InvalidOperationException(RimRunnerLog.Prefix + " 未知落点种类 " + kind);
        }
    }

    internal const string CellOutOfBoundsReason = "指定落点不在图内";

    internal const string CellUnstandableReason = "指定落点不可站";

    internal const string NoNearCellReason = "距离带内找不到可站、可达且有视线的格";

    /// <summary>落点全文：anchored、cell 加格、near 加锚点与距离带（带谓词加 +filter）。</summary>
    public override string ToString()
        => kind switch
        {
            Kind.Anchored => "anchored",
            Kind.Cell => "cell " + cell,
            _ => "near " + cell + " " + minDistance + ".." + maxDistance + (cellFilter != null ? " +filter" : "")
        };
}
