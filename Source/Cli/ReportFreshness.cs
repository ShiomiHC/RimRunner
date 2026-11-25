namespace RimRunnerCli;

public enum ReportFreshnessVerdict
{
    Unknown,
    Current,
    Stale,
}

public sealed record ReportFreshnessNote(
    ReportFreshnessVerdict Verdict, IReadOnlyList<string> Lines);

public static class ReportFreshness
{
    public static ReportFreshnessNote Describe(
        EngineReport report,
        IReadOnlyDictionary<string, string> declared,
        string productRoot,
        string currentContentFingerprint)
    {
        var stale = new List<string>();
        var unknown = new List<string>();

        DllFreshnessResult dll = DllFreshnessGate.Check(report.AssemblyMvids, declared, productRoot);
        if (dll.FlowError != null)
            unknown.Add(dll.FlowError);
        else
            stale.AddRange(dll.Stale);

        if (report.ContentFingerprint == null)
            unknown.Add("这份报告未记录 contentFingerprint");
        else if (!string.Equals(report.ContentFingerprint, currentContentFingerprint, StringComparison.Ordinal))
            stale.Add(ContentFingerprint.MismatchNote);

        var lines = new List<string>();
        if (stale.Count > 0)
        {
            lines.Add("[WARN] 这份报告与磁盘不匹配：");
            foreach (string entry in stale)
                lines.Add("  " + entry);
            foreach (string reason in unknown)
                lines.Add("  未比对：" + reason);
            AppendSkipped(lines, dll);
            return new ReportFreshnessNote(ReportFreshnessVerdict.Stale, lines);
        }

        if (unknown.Count > 0)
            return Unknown(string.Join("；", unknown));

        lines.Add($"[代码] 与磁盘匹配，已比对 {dll.ComparedCount} 个程序集及内容指纹");
        AppendSkipped(lines, dll);
        return new ReportFreshnessNote(ReportFreshnessVerdict.Current, lines);
    }

    private static void AppendSkipped(List<string> lines, DllFreshnessResult? result)
    {
        if (result != null && result.Skipped.Count > 0)
            lines.Add("  未比对（生成本报告时未加载）：" + string.Join("、", result.Skipped));
    }

    private static ReportFreshnessNote Unknown(string reason) =>
        new(ReportFreshnessVerdict.Unknown,
            ["[代码] 无从判断这份报告是否为当前代码的结论：" + reason]);
}
