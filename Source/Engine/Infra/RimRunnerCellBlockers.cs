namespace RimRunner;

internal sealed class RimRunnerCellBlockers
{
    internal enum Reason
    {
        OutOfBounds,
        Fogged,
        Roofed,
        Thing,
        Unstandable,
        CellPredicate
    }

    internal enum Origin
    {
        DuringCase,
        AtCaseStart,
        Unrecorded
    }

    private readonly Map map;
    private readonly RimRunnerContext? ctx = RimRunnerContext.Active;
    private readonly HashSet<IntVec3> seen = [];
    private readonly Dictionary<Reason, int> cells = [];
    private readonly Dictionary<Origin, Dictionary<string, int>> thingDefs = [];
    private readonly List<(string Label, int Count)> placements = [];

    internal RimRunnerCellBlockers(Map map)
    {
        this.map = map;
    }

    internal void AddCell(IntVec3 cell, Reason reason, Thing? thing = null)
    {
        if (!seen.Add(cell))
        {
            return;
        }

        if (reason != Reason.Thing || thing == null)
        {
            cells[reason] = cells.TryGetValue(reason, out int n) ? n + 1 : 1;
            return;
        }

        Origin origin = ctx?.PresentAtCaseStart(map, thing) switch
        {
            true => Origin.AtCaseStart,
            false => Origin.DuringCase,
            null => Origin.Unrecorded
        };
        if (!thingDefs.TryGetValue(origin, out Dictionary<string, int>? defs))
        {
            defs = [];
            thingDefs[origin] = defs;
        }

        string defName = thing.def.defName;
        defs[defName] = defs.TryGetValue(defName, out int count) ? count + 1 : 1;
    }

    internal void AddPlacements(string label, int count)
    {
        if (count > 0)
        {
            placements.Add((label, count));
        }
    }

    internal static string MapLabel(Map map)
        => (RimRunnerContext.Active?.BaselineLabelOf(map) ?? map.ToString()) + " " + map.Size.x + "×" + map.Size.z;

    internal int CellsOf(Reason reason) => cells.TryGetValue(reason, out int count) ? count : 0;

    internal IReadOnlyDictionary<string, int> ThingCells(Origin origin) =>
        thingDefs.TryGetValue(origin, out Dictionary<string, int>? defs) ? defs : [];

    internal IReadOnlyList<(string Label, int Count)> Placements => placements;

    internal string Describe()
    {
        List<string> parts = [];
        AppendThings(parts, Origin.DuringCase, "本用例新增的 Thing");
        AppendThings(parts, Origin.AtCaseStart, "用例开始时已在的 Thing");
        AppendThings(parts, Origin.Unrecorded, "未记来路的 Thing");
        AppendCells(parts, Reason.OutOfBounds, "越界");
        AppendCells(parts, Reason.Fogged, "迷雾");
        AppendCells(parts, Reason.Roofed, "有屋顶");
        AppendCells(parts, Reason.Unstandable, "不可站");
        AppendCells(parts, Reason.CellPredicate, "不满足调用方逐格判据");
        string text = parts.Count == 0 ? "" : "不合格格：" + string.Join("；", parts);
        if (placements.Count > 0)
        {
            text += (text.Length == 0 ? "" : "。") + "不合格位置："
                + string.Join("；", placements.Select(p => p.Label + " " + p.Count + " 处"));
        }

        return text;
    }

    private void AppendCells(List<string> parts, Reason reason, string label)
    {
        if (cells.TryGetValue(reason, out int n))
        {
            parts.Add(label + " " + n + " 格");
        }
    }

    private void AppendThings(List<string> parts, Origin origin, string label)
    {
        if (!thingDefs.TryGetValue(origin, out Dictionary<string, int>? defs))
        {
            return;
        }

        List<KeyValuePair<string, int>> ordered = defs
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        string shown = string.Join("、", ordered.Select(kv => kv.Key + " " + kv.Value));

        parts.Add(label + " " + ordered.Sum(kv => kv.Value) + " 格（" + shown + "）");
    }
}
