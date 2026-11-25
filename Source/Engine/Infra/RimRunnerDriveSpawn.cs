namespace RimRunner;

internal sealed class RimRunnerDriveSpawn : RimRunnerDriveVerb
{
    private const int MaxDefLines = 20;

    private const int MaxSuggestions = 8;

    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Spawn;

    internal override string? Refusal(RimRunnerDriveOp op)
    {
        Map? map = RimRunnerCurrentMapProbe.EngineCurrentMap();
        if (map == null)
        {
            return "没有当前地图";
        }

        string selector = op.Arg("selector")!;
        if (op.Arg("stuff") is { } stuffName && ThingDefNamed(stuffName) == null)
        {
            return "没有 ThingDef「" + stuffName + "」" + Suggest(stuffName);
        }

        if (IsCategory(selector))
        {
            string name = CategoryName(selector);
            if (!Enum.TryParse(name, ignoreCase: true, out ThingCategory category) || !Enum.IsDefined(category))
            {
                return "没有 ThingCategory「" + name + "」；可用：" + string.Join("、", Enum.GetNames<ThingCategory>());
            }

            if (category == ThingCategory.Pawn)
            {
                return "spawn 不生成 pawn";
            }

            return CategoryDefs(category).Count == 0 ? "category:" + category + " 下没有可生成的 ThingDef" : null;
        }

        ThingDef? def = ThingDefNamed(selector);
        if (def == null)
        {
            return "没有 ThingDef「" + selector + "」" + Suggest(selector);
        }

        return def.category == ThingCategory.Pawn ? "spawn 不生成 pawn：" + selector + " 是 pawn 类 Def" : null;
    }

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        Map map = RimRunnerCurrentMapProbe.EngineCurrentMap()!;
        string selector = op.Arg("selector")!;
        IReadOnlyList<ThingDef> defs = IsCategory(selector)
            ? CategoryDefs(Enum.Parse<ThingCategory>(CategoryName(selector), ignoreCase: true))
            : [ThingDefNamed(selector)!];
        ThingDef? stuff = op.Arg("stuff") is { } stuffName ? ThingDefNamed(stuffName) : null;
        int count = op.Arg("count") is { } countText ? RimRunnerDriveTable.ParsePositiveInt(countText)!.Value : 1;
        CellRect area = op.Arg("area") is { } areaText ? Area(areaText) : CellRect.WholeMap(map);

        RimRunnerSpawnReceipt receipt = RimRunnerSpawn.PlaceCore(map, defs, area, count, stuff, onPlaced: null);
        WriteReceipt(op, receipt);
        yield break;
    }

    private static bool IsCategory(string selector) =>
        selector.StartsWith("category:", StringComparison.OrdinalIgnoreCase);

    private static string CategoryName(string selector) => selector["category:".Length..].Trim();

    private static IReadOnlyList<ThingDef> CategoryDefs(ThingCategory category) =>
        [.. DefDatabase<ThingDef>.AllDefs
            .Where(def => def.category == category && DebugThingPlaceHelper.IsDebugSpawnable(def, allowPlayerBuildable: true))
            .OrderBy(def => def.defName, StringComparer.Ordinal)];

    private static CellRect Area(string text)
    {
        int[] v = RimRunnerDriveTable.ParseInts(text, 4)!;
        return CellRect.FromLimits(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }

    private static ThingDef? ThingDefNamed(string name) => DefDatabase<ThingDef>.GetNamedSilentFail(name);

    private static string Suggest(string input)
    {
        ThingDef[] all = DefDatabase<ThingDef>.AllDefs.ToArray();
        var found = new List<ThingDef>();
        found.AddRange(all
            .Where(def => def.defName.Contains(input, StringComparison.OrdinalIgnoreCase))
            .OrderBy(def => def.defName.Length)
            .ThenBy(def => def.defName, StringComparer.Ordinal));
        if (found.Count < MaxSuggestions)
        {
            found.AddRange(all
                .Where(def => !found.Contains(def))
                .Select(def => (Def: def, Distance: Distance(def.defName, input)))
                .OrderBy(pair => pair.Distance)
                .ThenBy(pair => pair.Def.defName, StringComparer.Ordinal)
                .Select(pair => pair.Def));
        }

        return found.Count == 0
            ? ""
            : "；相近：" + string.Join("、", found.Take(MaxSuggestions).Select(def => def.defName));
    }

    private static int Distance(string left, string right)
    {
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int substitution = previous[j - 1] + (char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static void WriteReceipt(RimRunnerDriveOp op, RimRunnerSpawnReceipt receipt)
    {
        List<(ThingDef Def, int Things, int Pieces, ThingDef? Stuff, IntVec3 First)> groups = Groups(receipt.Placed);
        op.Line("放置 " + groups.Sum(group => group.Things) + " 个物（共 " + groups.Sum(group => group.Pieces) + " 件，"
                + groups.Count + " 个 Def）");
        foreach ((ThingDef def, int things, int pieces, ThingDef? stuff, IntVec3 first) in groups.Take(MaxDefLines))
        {
            op.Line(def.defName + " ×" + things + "（" + pieces + " 件" + (stuff != null ? "，材料 " + stuff.defName : "")
                    + "，首个在 " + Cell(first) + "）");
        }

        if (groups.Count > MaxDefLines)
        {
            op.Line("… 其余 " + (groups.Count - MaxDefLines) + " 个 Def："
                    + string.Join("、", groups.Skip(MaxDefLines).Select(group => group.Def.defName + " ×" + group.Things)));
        }

        if (receipt.Unplaced.Count > 0)
        {
            op.Line("无法放置 " + receipt.Unplaced.Sum(entry => entry.Count) + " 件："
                    + string.Join("、", receipt.Unplaced.Select(entry => entry.Def.defName + " ×" + entry.Count)));
        }

        foreach (RimRunnerSpawnFailure entry in receipt.Failed)
        {
            op.Line("生成失败：" + entry.Def.defName + " " + Cell(entry.Cell) + "：" + entry.Reason);
        }

        foreach (RimRunnerSpawnWarning entry in receipt.Warnings)
        {
            op.Line("[WARN] " + entry.Def.defName + "：" + entry.Reason + " ×" + entry.Count + "（首个在 " + Cell(entry.First) + "）");
        }
    }

    private static List<(ThingDef Def, int Things, int Pieces, ThingDef? Stuff, IntVec3 First)> Groups(IReadOnlyList<Thing> placed)
    {
        List<(ThingDef Def, int Things, int Pieces, ThingDef? Stuff, IntVec3 First)> groups = [];
        foreach (Thing thing in placed)
        {
            int at = groups.FindIndex(group => group.Def == thing.def);
            if (at < 0)
            {
                groups.Add((thing.def, 1, thing.stackCount, thing.Stuff, thing.Position));
                continue;
            }

            (ThingDef def, int things, int pieces, ThingDef? stuff, IntVec3 first) = groups[at];
            groups[at] = (def, things + 1, pieces + thing.stackCount, stuff ?? thing.Stuff, first);
        }

        return groups;
    }

    private static string Cell(IntVec3 cell) => "(" + cell.x + "," + cell.z + ")";
}
