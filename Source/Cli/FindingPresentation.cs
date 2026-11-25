namespace RimRunnerCli;

internal static class FindingPresentation
{
    internal const int ScreenSubjects = 12;

    internal const int ScreenTextsPerKey = 3;

    internal const int DiagnosticsPerCase = 12;

    internal const int OffendingPerAssert = 12;

    internal const int ScreenCases = 12;

    internal const int ScreenCauseGroups = 20;

    internal const int ScreenCauseChars = 120;

    internal const int ArtifactsListed = 5;

    internal const int ScreenLineChars = 400;

    internal const string CappedMark = "…（全文见 RimRunner 日志）";

    internal static string Omitted(int count, string unit, string? where) =>
        $"其余 {count} {unit}" + (where == null ? "未列" : "，" + where);

    internal const string InLog = "全列见 RimRunner 日志";

    internal const string LiveProgress = "rimrunner live progress";

    internal const string InLiveProgress = "全列见 " + LiveProgress;

    internal static string InReport(string field) => $"全列见报告 {field}";

    internal static string WithFlag(string flag) => $"加 {flag} 全列";

    internal static string ListLine(string head, IReadOnlyList<string> items, string separator, List<string> logOnly,
        string unit = "个", string? logHead = null, int maxChars = int.MaxValue)
    {
        string whole = head + string.Join(separator, items);
        if (items.Count <= ScreenSubjects && whole.Length <= maxChars)
            return whole;
        logOnly.Add((logHead ?? head) + string.Join(separator, items));

        string Line(int shown) => head + string.Join(separator, items.Take(shown))
            + "（" + Omitted(items.Count - shown, unit, InLog) + "）";
        int shown = Math.Min(items.Count, ScreenSubjects);
        while (shown > 0 && Line(shown).Length > maxChars)
            shown--;
        return Line(shown);
    }

    internal static void Items(IReadOnlyList<string> items, int shown, string indent, string unit, string logPrefix,
        List<string> screen, List<string> logOnly)
    {
        (List<string> lines, List<string> log) = ScreenTree.Render(
            [.. items.Select(item => new ScreenNode("item", SubjectStyle.Fact, null, item, ScreenNode.None))], indent, "  ",
            new Budget("item", shown, unit, InLog, logPrefix, LogOverflow: true));
        screen.AddRange(lines);
        logOnly.AddRange(log);
    }

    internal static string? AssertHandle(int ordinal) => ordinal > 0 ? "断言 " + ordinal : null;

    internal static void Facts(
        IEnumerable<EngineReportFinding> findings, string field, string logPrefix,
        List<string> screen, List<string> logOnly, string indent = "  ", string unit = "处")
    {
        (List<string> lines, List<string> log) = ScreenTree.Render(FactNodes(findings, "fact", field, logPrefix, unit), indent, "  ");
        screen.AddRange(lines);
        logOnly.AddRange(log);
    }

    internal static List<ScreenNode> FactNodes(IEnumerable<EngineReportFinding> findings, string kind, string field, string logPrefix,
        string unit = "处")
    {
        List<EngineReportFinding> all = [.. findings];
        List<ScreenNode> nodes = [];
        HashSet<string> summarized = new(StringComparer.Ordinal);
        foreach (IGrouping<string, EngineReportFinding> byKey in all
                     .Where(finding => finding.Key != null && finding.Subject != null)
                     .GroupBy(finding => finding.Key!, StringComparer.Ordinal))
        {
            int texts = byKey.Select(finding => finding.Text).Distinct(StringComparer.Ordinal).Count();
            if (texts <= ScreenTextsPerKey)
                continue;

            summarized.Add(byKey.Key);
            string[] subjects = Subjects(byKey);
            EngineReportFinding example = byKey.First();
            nodes.Add(new ScreenNode(kind, SubjectStyle.Fact, null, $"[{byKey.Key}] {subjects.Length} {unit}（{texts} 种正文）：{ScreenList(subjects, "、")}",
                [new ScreenNode(kind, SubjectStyle.Fact, null, $"例：{example.Subject}：{FirstLineWithPointer(example.Text, field)}", ScreenNode.None)])
            {
                LogAlways = [.. byKey.Select(finding => $"{logPrefix}：[{byKey.Key}] {finding.Subject}：{finding.Text}")],
            });
        }

        nodes.AddRange(all
            .Where(finding => finding.Key == null || finding.Subject == null || !summarized.Contains(finding.Key))
            .Select(finding => new ScreenNode(kind, SubjectStyle.Fact, finding.Subject, FirstLineWithPointer(finding.Text, field), ScreenNode.None)
            {
                Identity = finding.Key,
                Full = finding.Text.Contains('\n') ? finding.Text : null,
                LogPrefix = logPrefix,
                Unit = unit,
            }));
        return nodes;
    }

    internal static void FactsInFull(IEnumerable<EngineReportFinding> findings, List<string> lines, string indent)
    {
        foreach (IGrouping<(string? Key, string Text), EngineReportFinding> group in findings
                     .GroupBy(finding => (finding.Key, finding.Text)))
        {
            int count = group.Count();
            string times = count > 1 ? $" ×{count}" : "";
            string[] subjects = Subjects(group);
            lines.Add(indent + subjects.Length switch
            {
                0 => group.Key.Text + times,
                1 => subjects[0] + "：" + group.Key.Text + times,
                _ => $"{group.Key.Text} —— {subjects.Length} 处：{string.Join("、", subjects)}",
            });
        }
    }

    internal static void Keyed(IEnumerable<EngineReportFinding> findings, string logPrefix, List<string> screen, List<string> logOnly,
        bool markFresh = false)
    {
        (List<string> lines, List<string> logLines) = ScreenTree.Render(KeyedNodes(findings, logPrefix, markFresh), "", "  ");
        screen.AddRange(lines);
        logOnly.AddRange(logLines);
    }

    internal static List<ScreenNode> KeyedNodes(IEnumerable<EngineReportFinding> findings, string logPrefix, bool markFresh)
    {
        List<ScreenNode> nodes = [];
        foreach (IGrouping<string, EngineReportFinding> group in findings
                     .GroupBy(finding => finding.Key ?? "", StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            string[] subjects = [.. group.Select(finding => finding.Subject ?? "窗外").Distinct(StringComparer.Ordinal)];
            List<string> log = [];
            if (subjects.Length > ScreenSubjects)
                log.Add($"{logPrefix}[{group.Key}] ×{group.Count()}：{string.Join(" ", subjects)}");
            List<ScreenNode> children = [];
            if (markFresh)
            {
                (string Subject, string Text)[] fresh =
                [
                    .. group.Where(finding => finding.Fresh == true)
                        .Select(finding => (finding.Subject ?? "窗外", finding.Text)).Distinct()
                ];
                children.AddRange(fresh.Take(ScreenTextsPerKey).Select(item =>
                    new ScreenNode("fresh", SubjectStyle.Fact, null, $"[新] {item.Subject}：{FirstLineWithPointer(item.Text, "warnings")}", ScreenNode.None)));
                if (fresh.Length > ScreenTextsPerKey)
                    children.Add(new ScreenNode("fresh", SubjectStyle.Fact, null, "[新] " + Omitted(fresh.Length - ScreenTextsPerKey, "条", InLog), ScreenNode.None));
                log.AddRange(fresh.Select(item => $"{logPrefix}[新] [{group.Key}] {item.Subject}：{item.Text}"));
            }

            nodes.Add(new ScreenNode("keyed", SubjectStyle.Fact, null, $"[{group.Key}] ×{group.Count()}：{ScreenList(subjects, " ")}", children)
                { LogAlways = log, Family = "keyed\u0001" + group.Key });
        }

        return nodes;
    }

    internal static string FirstLineWithPointer(string text, string field) =>
        text.Contains('\n') ? FirstLine(text) + Pointer(field) : text;

    internal static string FirstLinesWithPointer(string field, params string[] parts) =>
        string.Concat(parts.Select(FirstLine)) + (parts.Any(part => part.Contains('\n')) ? Pointer(field) : "");

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline].TrimEnd('\r');
    }

    private static string Pointer(string field) =>
        field.Length == 0 ? "（全文见 RimRunner 日志）" : $"（全文见报告 {field}）";

    internal static string CapScreenLine(string line)
    {
        int newline = line.IndexOf('\n');
        string head = newline < 0 ? line : line[..newline].TrimEnd('\r');
        if (newline < 0 && head.Length <= ScreenLineChars)
            return line;
        return head.Length <= ScreenLineChars ? head + "（全文见 RimRunner 日志）" : head[..ScreenLineChars] + CappedMark;
    }

    internal static string FitKeepingTail(string body, string tail, List<string> logOnly)
    {
        if (body.Length + tail.Length <= ScreenLineChars)
            return body + tail;
        logOnly.Add(body + tail);
        int room = Math.Max(0, ScreenLineChars - tail.Length - CappedMark.Length);
        int space = body.LastIndexOf(' ', room);
        return body[..(space > room / 2 ? space : room)] + CappedMark + tail;
    }

    internal static void ScreenCapped(string line, Action<string> write)
    {
        string shown = CapScreenLine(line);
        if (shown == line)
            write(line);
        else
            CliLog.ScreenAs(shown, line, write);
    }

    internal static string ScreenList(string[] subjects, string separator, string unit = "个") =>
        subjects.Length <= ScreenSubjects
            ? string.Join(separator, subjects)
            : string.Join(separator, subjects.Take(ScreenSubjects))
              + "（" + Omitted(subjects.Length - ScreenSubjects, unit, InLog) + "）";

    private static string[] Subjects(IEnumerable<EngineReportFinding> group) =>
    [
        .. group.Select(finding => finding.Subject).OfType<string>().Distinct(StringComparer.Ordinal)
    ];
}
