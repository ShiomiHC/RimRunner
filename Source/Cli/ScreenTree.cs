namespace RimRunnerCli;

internal enum SubjectStyle
{
    Fact,

    Case,

    Label,
}

internal sealed record Budget(string Kind, int Max, string Unit, string Where, string LogPrefix, bool LogOverflow,
    Func<ScreenNode, int>? Order = null, string Lead = "");

internal sealed record ScreenNode(string Kind, SubjectStyle Style, string? Subject, string Text, IReadOnlyList<ScreenNode> Children)
{
    public static readonly IReadOnlyList<ScreenNode> None = [];

    public string Suffix { get; init; } = "";

    public string? Identity { get; init; }

    public string? Handle { get; init; }

    public bool Trailing { get; init; }

    public int MarkAt { get; init; }

    public string? Name { get; init; }

    public string? Full { get; init; }

    public string? LogPrefix { get; init; }

    public IReadOnlyList<string>? LogAlways { get; init; }

    public Budget? Budget { get; init; }

    public string Unit { get; init; } = "处";

    public int ExtraIndent { get; init; }

    public string? Family { get; init; }
}

internal sealed record MergedNode(ScreenNode Node, IReadOnlyList<ScreenNode> Originals, IReadOnlyList<string> Subjects,
    IReadOnlyList<MergedNode> Children, int SharedTrailing)
{
    public int Count => Originals.Count;

    public IReadOnlyList<IReadOnlyList<MergedNode>> MemberChildren { get; init; } = [];
}

internal static class ScreenTree
{
    public static IReadOnlyList<MergedNode> Merge(IReadOnlyList<ScreenNode> nodes)
    {
        var groups = new List<(string Key, List<ScreenNode> Members, List<IReadOnlyList<MergedNode>> Children)>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ScreenNode node in nodes)
        {
            IReadOnlyList<MergedNode> children = Merge(node.Children);
            string key = Identity(node) + "\u0002" + node.Suffix + "\u0002" + Canonical(children);
            if (at.TryGetValue(key, out int index))
            {
                groups[index].Members.Add(node);
                groups[index].Children.Add(children);
                continue;
            }

            at[key] = groups.Count;
            groups.Add((key, [node], [children]));
        }

        List<MergedNode> merged =
        [
            .. groups.Select(group => new MergedNode(group.Members[0], group.Members, SubjectsOf(group.Members), group.Children[0], 0)
            {
                MemberChildren = group.Children,
            })
        ];
        return ShareAttributes(HoistTrailing(ShareRows(merged)));
    }

    private static string Identity(ScreenNode node) =>
        node.Kind + "\u0001" + node.Style + "\u0001" + (node.Style == SubjectStyle.Fact
            ? node.Text + "\u0001" + node.Identity
            : node.Identity ?? node.Full ?? node.Text);

    private static IReadOnlyList<string> SubjectsOf(List<ScreenNode> members)
    {
        IEnumerable<string> subjects = members.Select(member => member.Subject).OfType<string>();
        return [.. members[0].Style == SubjectStyle.Label ? subjects : subjects.Distinct(StringComparer.Ordinal)];
    }

    private static string Canonical(IReadOnlyList<MergedNode> nodes) =>
        string.Join("\u0003", nodes.Select(node =>
            Identity(node.Node) + "\u0002" + node.Node.Suffix + "\u0002" + node.Count + "\u0002" + string.Join("\u0001", node.Subjects)
            + "\u0002" + node.SharedTrailing + "\u0004" + Canonical(node.Children) + "\u0005"));

    private static int TrailingStart(MergedNode sibling)
    {
        int start = sibling.Children.Count;
        while (start > 0 && sibling.Children[start - 1].Node.Trailing) start--;
        return start;
    }

    private static List<MergedNode> ShareRows(List<MergedNode> siblings)
    {
        var shown = new Dictionary<string, (string Handle, int Position)>(StringComparer.Ordinal);
        var result = new List<MergedNode>(siblings.Count);
        foreach (MergedNode sibling in siblings)
        {
            int trailing = TrailingStart(sibling);
            var kept = new List<MergedNode>();
            var shared = new List<(string Handle, int Position, MergedNode Row)>();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MergedNode row in sibling.Children.Take(trailing))
            {
                string? key = row.Children.Count == 0 && row.Node.Text.Trim().Length >= CliLog.RepeatMinChars
                    ? sibling.Node.Kind + "\u0006" + Canonical([row])
                    : null;
                if (key != null && shown.TryGetValue(key, out (string Handle, int Position) at))
                {
                    shared.Add((at.Handle, at.Position, row));
                    continue;
                }

                kept.Add(row);
                int position = positions[row.Node.Kind] = positions.GetValueOrDefault(row.Node.Kind) + 1;
                Budget? budget = sibling.Node.Budget;
                bool onScreen = budget == null || budget.Kind != row.Node.Kind || (budget.Order == null && position <= budget.Max);
                if (key != null && sibling.Node.Handle is string handle && onScreen)
                    shown[key] = (handle, position);
            }

            if (shared.Count == 0)
            {
                result.Add(sibling);
                continue;
            }

            string unit = sibling.Node.Budget?.Unit ?? "行";
            string logPrefix = sibling.Node.Budget?.LogPrefix ?? sibling.Node.LogPrefix ?? "";
            IEnumerable<MergedNode> pointers = shared.GroupBy(row => row.Handle, StringComparer.Ordinal).Select(group =>
            {
                string text = (kept.Count > 0 ? "另 " : "") + $"{group.Count()} {unit}与{group.Key} 的第 "
                    + string.Join("、", group.Select(row => row.Position).Order()) + $" {unit}相同";
                var node = new ScreenNode("shared", SubjectStyle.Fact, null, text, ScreenNode.None)
                {
                    LogAlways = [.. group.Select(row => logPrefix + row.Row.Node.Text)],
                };
                return new MergedNode(node, [node], [], [], 0);
            });
            result.Add(sibling with { Children = [.. kept, .. pointers, .. sibling.Children.Skip(trailing)] });
        }

        return result;
    }

    private static IReadOnlyList<MergedNode> ShareAttributes(IReadOnlyList<MergedNode> siblings)
    {
        if (!siblings.Any(sibling => sibling.Node.Style == SubjectStyle.Case)) return siblings;
        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<MergedNode>(siblings.Count);
        foreach (MergedNode sibling in siblings)
        {
            string? owner = sibling.Node.Style == SubjectStyle.Case ? sibling.Subjects.FirstOrDefault() : null;
            if (owner == null)
            {
                result.Add(sibling);
                continue;
            }

            var firsts = new List<(string Key, string Owner)>();
            var children = new List<MergedNode>(sibling.Children.Count);
            foreach (MergedNode child in sibling.Children)
            {
                int start = TrailingStart(child);
                MergedNode[] run = [.. child.Children.Skip(start)];
                if (run.Length == 0 || !run.Any(row => row.Node.Text.Trim().Length >= CliLog.RepeatMinChars))
                {
                    children.Add(child);
                    continue;
                }

                string key = Canonical([.. run.Select(row => row with { SharedTrailing = 0 })]);
                if (!shown.TryGetValue(key, out string? target))
                {
                    firsts.Add((key, owner));
                    children.Add(child);
                    continue;
                }

                string names = string.Join("、", run.Select(row => row.Node.Name ?? row.Node.Kind).Distinct(StringComparer.Ordinal));
                string mark = run[0].SharedTrailing > 0 ? $"（以上 {run[0].SharedTrailing} 条同）" : "";
                var pointer = new ScreenNode("attribute-pointer", SubjectStyle.Fact, null, $"{names}{mark}: 同用例 {target}", ScreenNode.None)
                {
                    LogAlways = [.. sibling.Subjects.SelectMany(id => run.Select(row => $"[用例] {id}：{row.Node.Text}"))],
                };
                children.Add(child with { Children = [.. child.Children.Take(start), new MergedNode(pointer, [pointer], [], [], 0)] });
            }

            foreach ((string key, string first) in firsts)
                shown.TryAdd(key, first);
            result.Add(sibling with { Children = children });
        }

        return result;
    }

    private static IReadOnlyList<MergedNode> HoistTrailing(List<MergedNode> siblings)
    {
        var runs = siblings.Select(sibling =>
        {
            int start = sibling.Children.Count;
            while (start > 0 && sibling.Children[start - 1].Node.Trailing) start--;
            return start == sibling.Children.Count ? null : sibling.Node.Kind + "\u0006" + Canonical([.. sibling.Children.Skip(start)]);
        }).ToList();

        var byRun = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < siblings.Count; i++)
            if (runs[i] is string run)
                (byRun.TryGetValue(run, out List<int>? members) ? members : byRun[run] = []).Add(i);
        if (byRun.Values.All(members => members.Count < 2))
            return siblings;

        var result = new List<MergedNode>();
        var placed = new HashSet<int>();
        for (int i = 0; i < siblings.Count; i++)
        {
            if (placed.Contains(i)) continue;
            if (runs[i] is not string run || byRun[run].Count < 2)
            {
                result.Add(siblings[i]);
                continue;
            }

            List<int> members = byRun[run];
            int total = members.Sum(member => siblings[member].Count);
            foreach (int member in members)
            {
                placed.Add(member);
                MergedNode sibling = siblings[member];
                int start = sibling.Children.Count;
                while (start > 0 && sibling.Children[start - 1].Node.Trailing) start--;
                result.Add(member == members[^1]
                    ? sibling with
                    {
                        Children = [.. sibling.Children.Take(start), sibling.Children[start] with { SharedTrailing = total }, .. sibling.Children.Skip(start + 1)],
                    }
                    : sibling with { Children = [.. sibling.Children.Take(start)] });
            }
        }

        return result;
    }

    public static (List<string> Screen, List<string> LogOnly) Render(IReadOnlyList<MergedNode> nodes, string prefix, string childIndent,
        Budget? budget = null)
    {
        List<string> screen = [];
        List<string> logOnly = [];
        Render(nodes, prefix, childIndent, 0, budget, null, screen, logOnly);
        return (screen, logOnly);
    }

    public static (List<string> Screen, List<string> LogOnly) Render(IReadOnlyList<ScreenNode> nodes, string prefix, string childIndent,
        Budget? budget = null)
    {
        List<string> screen = [];
        List<string> logOnly = [];
        Render(Merge(nodes), prefix, childIndent, 0, budget, nodes, screen, logOnly);
        return (screen, logOnly);
    }

    private static void Render(IReadOnlyList<MergedNode> nodes, string prefix, string childIndent, int depth, Budget? budget,
        IReadOnlyList<ScreenNode>? raw, List<string> screen, List<string> logOnly)
    {
        HashSet<MergedNode>? kept = null;
        int lastOfKind = -1;
        if (budget != null)
        {
            List<int> ofKind = [.. Enumerable.Range(0, nodes.Count).Where(i => nodes[i].Node.Kind == budget.Kind)];
            lastOfKind = ofKind.Count == 0 ? -1 : ofKind[^1];
            kept = [.. ofKind.OrderBy(i => budget.Order?.Invoke(nodes[i].Node) ?? 0).Take(budget.Max).Select(i => nodes[i])];
            if (ofKind.Count <= budget.Max)
                budget = null;
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            MergedNode node = nodes[i];
            bool budgeted = budget != null && node.Node.Kind == budget.Kind;
            string indent = prefix + string.Concat(Enumerable.Repeat(childIndent, depth + node.Node.ExtraIndent));
            if (!budgeted || kept!.Contains(node))
                RenderNode(node, indent, prefix, childIndent, depth, screen, logOnly);
            if (budgeted && i == lastOfKind)
            {
                int dropped = nodes.Where(other => other.Node.Kind == budget!.Kind && !kept!.Contains(other)).Sum(other => other.Count);
                screen.Add(indent + budget!.Lead + FindingPresentation.Omitted(dropped, budget.Unit, budget.Where));
                if (budget.LogOverflow)
                    logOnly.AddRange((raw ?? [.. nodes.SelectMany(other => other.Originals)])
                        .Where(original => original.Kind == budget.Kind).Select(original => budget.LogPrefix + original.Text));
            }
        }
    }

    private static void RenderNode(MergedNode merged, string indent, string prefix, string childIndent, int depth,
        List<string> screen, List<string> logOnly)
    {
        ScreenNode node = merged.Node;
        string text = merged.SharedTrailing > 0 ? node.Text.Insert(node.MarkAt, $"（以上 {merged.SharedTrailing} 条同）") : node.Text;
        switch (node.Style)
        {
            case SubjectStyle.Fact:
                RenderFact(merged, text, indent, screen, logOnly);
                break;
            case SubjectStyle.Case:
                screen.Add(merged.Subjects.Count <= 1
                    ? $"{indent}{text} {merged.Subjects.FirstOrDefault()}{node.Suffix}"
                    : FindingPresentation.ListLine($"{indent}{text} {merged.Subjects.Count} 条明细相同：", merged.Subjects, " ", logOnly, "条",
                        maxChars: FindingPresentation.ScreenLineChars) + node.Suffix);
                break;
            case SubjectStyle.Label:
                screen.Add(indent + text);
                if (merged.Subjects.Count > 1)
                {
                    int others = merged.Subjects.Count - 1;
                    screen.Add(FindingPresentation.ListLine($"{indent}{childIndent}同一判定另 {others} 条：", [.. merged.Subjects.Skip(1)], "、", logOnly, "条",
                        logHead: $"{node.LogPrefix}{merged.Subjects[0]} 另 {others} 条：", maxChars: FindingPresentation.ScreenLineChars));
                }

                break;
        }

        logOnly.AddRange(merged.Originals.SelectMany(original => original.LogAlways ?? []).Distinct(StringComparer.Ordinal));
        int start = logOnly.Count;
        Render(merged.Children, prefix, childIndent, depth + 1, node.Budget, node.Children, screen, logOnly);
        var logged = new HashSet<string>(logOnly.Skip(start), StringComparer.Ordinal);
        for (int i = 1; i < merged.MemberChildren.Count; i++)
        {
            List<string> member = [];
            Render(merged.MemberChildren[i], prefix, childIndent, depth + 1, node.Budget, merged.Originals[i].Children, [], member);
            logOnly.AddRange(member.Where(logged.Add));
        }
    }

    private static void RenderFact(MergedNode merged, string text, string indent, List<string> screen, List<string> logOnly)
    {
        ScreenNode node = merged.Node;
        IReadOnlyList<string> subjects = merged.Subjects;
        string times = merged.Count > 1 ? $" ×{merged.Count}" : "";
        screen.Add(indent + subjects.Count switch
        {
            0 => text + times,
            1 => subjects[0] + "：" + text + times,
            _ => $"{text} —— {subjects.Count} {node.Unit}：{FindingPresentation.ScreenList([.. subjects], "、")}",
        });

        foreach (IGrouping<string, ScreenNode> full in merged.Originals.Where(original => original.Full != null)
                     .GroupBy(original => original.Full!, StringComparer.Ordinal))
        {
            int count = full.Count();
            string[] owners = [.. full.Select(original => original.Subject).OfType<string>().Distinct(StringComparer.Ordinal)];
            logOnly.Add($"{node.LogPrefix}{(count > 1 ? $" ×{count}" : "")}：" + (owners.Length == 1 ? owners[0] + "：" : "") + full.Key);
        }

        if (subjects.Count > FindingPresentation.ScreenSubjects)
            logOnly.Add($"{node.LogPrefix}：{text} —— {subjects.Count} {node.Unit}：{string.Join("、", subjects)}");
    }
}
