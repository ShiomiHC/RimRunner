namespace RimRunner;

/// <summary>铺场面的原语：按一个 ThingDef 在 ctx.Map 的矩形里放物，回执给出放下的与摆不出的。</summary>
/// <remarks>放下与生成失败的物按回执读，不抛。玩家无法建造的地形、无法站立的交互格、dev 菜单不生成的 Def 照放，并记在回执的 Warnings。只放非 pawn 的 ThingDef。</remarks>
public static class RimRunnerSpawn
{
    internal static RimRunnerSpawnReceipt PlaceCore(Map map, IReadOnlyList<ThingDef> defs, CellRect area, int count,
        ThingDef? stuff, Action<Thing>? onPlaced)
    {
        var placed = new List<Thing>();
        var unplaced = new List<(ThingDef Def, int Count)>();
        var failed = new List<RimRunnerSpawnFailure>();
        var warnings = new List<(ThingDef Def, string Reason, int Count, IntVec3 First)>();

        CellRect clipped = area.ClipInsideMap(map);
        var cells = new List<IntVec3>();
        for (int z = clipped.minZ; z <= clipped.maxZ; z++)
        {
            for (int x = clipped.minX; x <= clipped.maxX; x++)
            {
                cells.Add(new IntVec3(x, 0, z));
            }
        }

        var cursors = new Dictionary<ThingDef, int>();
        int units = UnitCount(defs, count);
        bool stacks = defs.Count == 1 && defs[0].category == ThingCategory.Item;
        int produced = 0;
        for (int unit = 0; unit < units; unit++)
        {
            ThingDef def = defs[unit % defs.Count];
            int pieces = stacks ? Math.Min(count - produced, Math.Max(1, def.stackLimit)) : 1;
            produced += pieces;

            Rot4 rot = def.defaultPlacingRot;
            IntVec3 cell = IntVec3.Invalid;
            bool fits = false;
            int cursor = cursors.GetValueOrDefault(def);
            while (cursor < cells.Count)
            {
                IntVec3 candidate = cells[cursor++];
                if (!Fits(map, def, candidate, clipped, rot))
                {
                    continue;
                }

                cell = candidate;
                fits = true;
                break;
            }

            cursors[def] = cursor;

            if (!fits)
            {
                AddUnplaced(unplaced, def, pieces);
                continue;
            }

            Thing? thing = Spawn(map, def, cell, rot, pieces, stuff, out string? failure);
            if (thing == null)
            {
                failed.Add(new RimRunnerSpawnFailure(def, cell, failure ?? "生成失败"));
                continue;
            }

            placed.Add(thing);
            onPlaced?.Invoke(thing);
            CollectWarnings(warnings, map, def, cell, rot);
        }

        return new RimRunnerSpawnReceipt(placed,
            [.. unplaced.Select(entry => new RimRunnerSpawnShortfall(entry.Def, entry.Count))],
            failed,
            [.. warnings.Select(entry => new RimRunnerSpawnWarning(entry.Def, entry.Reason, entry.Count, entry.First))]);
    }

    private static int UnitCount(IReadOnlyList<ThingDef> defs, int count) =>
        defs.Count == 1 && defs[0].category == ThingCategory.Item
            ? (count + Math.Max(1, defs[0].stackLimit) - 1) / Math.Max(1, defs[0].stackLimit)
            : count;

    private static bool Fits(Map map, ThingDef def, IntVec3 cell, CellRect area, Rot4 rot)
    {
        CellRect footprint = GenAdj.OccupiedRect(cell, rot, def.Size);
        if (!footprint.InBounds(map))
        {
            return false;
        }

        foreach (IntVec3 at in footprint)
        {
            if (!area.Contains(at))
            {
                return false;
            }

            List<Thing> things = map.thingGrid.ThingsListAtFast(at);
            for (int i = 0; i < things.Count; i++)
            {
                ThingCategory category = things[i].def.category;
                if (category is not (ThingCategory.Plant or ThingCategory.Filth or ThingCategory.Mote))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static Thing? Spawn(Map map, ThingDef def, IntVec3 cell, Rot4 rot, int pieces, ThingDef? stuff,
        out string? failure)
    {
        failure = null;
        ThingDef? stuffToUse = stuff ?? (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
        try
        {
            Thing thing = ThingMaker.MakeThing(def, stuffToUse);
            if (def.category == ThingCategory.Item)
            {
                thing.stackCount = pieces;
            }

            Thing? spawned = GenSpawn.Spawn(thing, cell, map, rot);
            if (spawned == null)
            {
                failure = "原版 spawn 返回 null";
                return null;
            }

            if (!spawned.Spawned)
            {
                failure = "落图后未 spawned";
                return null;
            }

            return spawned;
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name + "：" + exception.Message;
            return null;
        }
    }

    private static void AddUnplaced(List<(ThingDef Def, int Count)> unplaced, ThingDef def, int pieces)
    {
        for (int i = 0; i < unplaced.Count; i++)
        {
            if (unplaced[i].Def == def)
            {
                unplaced[i] = (def, unplaced[i].Count + pieces);
                return;
            }
        }

        unplaced.Add((def, pieces));
    }

    private static void CollectWarnings(List<(ThingDef Def, string Reason, int Count, IntVec3 First)> warnings,
        Map map, ThingDef def, IntVec3 cell, Rot4 rot)
    {
        if (def.category == ThingCategory.Building && !GenConstruct.CanBuildOnTerrain(def, cell, map, rot))
        {
            AddWarning(warnings, def, "地形上玩家无法建造", cell);
        }

        if (def.HasSingleOrMultipleInteractionCells && InteractionCellBlocked(def, cell, rot, map))
        {
            AddWarning(warnings, def, "交互格不可站", cell);
        }

        if (!DebugThingPlaceHelper.IsDebugSpawnable(def, allowPlayerBuildable: true))
        {
            AddWarning(warnings, def, "原版 dev 菜单不生成这个 Def", cell);
        }
    }

    private static readonly List<IntVec3> InteractionCells = [];

    private static bool InteractionCellBlocked(ThingDef def, IntVec3 cell, Rot4 rot, Map map)
    {
        ThingUtility.InteractionCellsWhenAt(InteractionCells, def, cell, rot, map, allowFallbackCell: false);
        foreach (IntVec3 at in InteractionCells)
        {
            if (!at.InBounds(map) || !at.Standable(map))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddWarning(List<(ThingDef Def, string Reason, int Count, IntVec3 First)> warnings,
        ThingDef def, string reason, IntVec3 cell)
    {
        for (int i = 0; i < warnings.Count; i++)
        {
            if (warnings[i].Def == def && warnings[i].Reason == reason)
            {
                warnings[i] = (def, reason, warnings[i].Count + 1, warnings[i].First);
                return;
            }
        }

        warnings.Add((def, reason, 1, cell));
    }

    /// <summary>放下 count 个物：物品按件数分堆，其余一件一个。</summary>
    /// <remarks>落点只落在 area 内，块上除植物、污渍、mote 外没有物，地图取 ctx.Map。放下的物在用例收尾销毁或 DeSpawn。生成时原版清掉的植物与污渍保持清掉。</remarks>
    /// <param name="ctx">用例上下文，地图取 ctx.Map。</param>
    /// <param name="def">要放的 ThingDef，只收非 Pawn 类 Def。</param>
    /// <param name="area">落点搜索的格矩形；超出地图的部分裁掉。</param>
    /// <param name="count">物件的总个数；物品按件数分堆。</param>
    /// <param name="stuff">材料，null 时按 Def 取缺省材料。传入原版用不上的材料时会记错误级日志。</param>
    /// <returns>回执：放下的物、放不下的件数、生成失败与警告。</returns>
    /// <exception cref="ArgumentException">def 是 Pawn 类时抛。</exception>
    /// <exception cref="ArgumentOutOfRangeException">count 小于 1 时抛。</exception>
    /// <exception cref="InvalidOperationException">ctx.Map 为空时抛。</exception>
    public static RimRunnerSpawnReceipt Place(RimRunnerContext ctx, ThingDef def, CellRect area, int count = 1,
        ThingDef? stuff = null)
    {
        if (def == null)
        {
            throw new ArgumentNullException(nameof(def), RimRunnerLog.Prefix + " 铺场面须给 ThingDef");
        }

        if (def.category == ThingCategory.Pawn)
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " 铺场面不生成 pawn：" + def.defName, nameof(def));
        }

        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, RimRunnerLog.Prefix + " count 须至少为 1");
        }

        Map map = ctx.Map ?? throw new InvalidOperationException(RimRunnerLog.Prefix + " 铺场面没有地图：用例不在图上时 Map 为空");

        return PlaceCore(map, [def], area, count, stuff, thing =>
        {
            if (def.destroyable)
            {
                ctx.TrackThing(thing);
            }
            else
            {
                ctx.TrackBorrowed(thing);
            }
        });
    }
}

/// <summary>铺一次场面的回执：放下的物、放不下的件数、生成失败与警告。</summary>
public sealed class RimRunnerSpawnReceipt
{
    internal RimRunnerSpawnReceipt(IReadOnlyList<Thing> placed, IReadOnlyList<RimRunnerSpawnShortfall> unplaced,
        IReadOnlyList<RimRunnerSpawnFailure> failed, IReadOnlyList<RimRunnerSpawnWarning> warnings)
    {
        Placed = placed;
        Unplaced = unplaced;
        Failed = failed;
        Warnings = warnings;
    }

    /// <summary>已经放在图上的物，按放下先后。</summary>
    public IReadOnlyList<Thing> Placed { get; }

    /// <summary>放不下的件数，每个 Def 一条。</summary>
    public IReadOnlyList<RimRunnerSpawnShortfall> Unplaced { get; }

    /// <summary>生成失败的物逐条。</summary>
    public IReadOnlyList<RimRunnerSpawnFailure> Failed { get; }

    /// <summary>照放但玩家摆不出来的那些，按 Def 与原因聚合。</summary>
    public IReadOnlyList<RimRunnerSpawnWarning> Warnings { get; }
}

/// <summary>放不下的件数：按 Def 计。</summary>
/// <param name="Def">放不下的那个 ThingDef。</param>
/// <param name="Count">没放下的件数：物品按堆里的件数计，其余一件一个。</param>
public readonly record struct RimRunnerSpawnShortfall(ThingDef Def, int Count);

/// <summary>一件生成失败：Def、落点与原因。</summary>
/// <param name="Def">要放的 ThingDef。</param>
/// <param name="Cell">选中的格。</param>
/// <param name="Reason">失败原因；异常时带类型与消息。</param>
public readonly record struct RimRunnerSpawnFailure(ThingDef Def, IntVec3 Cell, string Reason);

/// <summary>一条铺场警告：Def、原因、次数与第一次的格。</summary>
/// <param name="Def">警告指向的 ThingDef。</param>
/// <param name="Reason">原因。</param>
/// <param name="Count">同 Def 同原因的落点个数。</param>
/// <param name="First">第一个落点。</param>
public readonly record struct RimRunnerSpawnWarning(ThingDef Def, string Reason, int Count, IntVec3 First);
