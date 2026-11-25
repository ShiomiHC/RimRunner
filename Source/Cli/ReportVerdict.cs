namespace RimRunnerCli;

internal static class ReportVerdict
{
    internal sealed record Item(string Text, int Grade, string? CaseId = null);

    internal static int ExitCode(EngineReport report) => ExitPolicy.Worst(
    [
        report.Rejections.Count > 0 ? ExitCodes.Rejected : 0,
        report.CaseRejections.Count > 0 ? 1 : 0,
        .. CaseItems(report).Select(item => item.Grade),
        .. BatchItems(report).Select(item => item.Grade),
    ]);

    internal static Item? CaseItem(EngineReportCase result, bool regression) =>
        (regression ? RimRunner.RimRunnerVerdict.IsVerdictAttention(result.Status) : RimRunner.RimRunnerVerdict.IsDiagnosticAttention(result.Status))
            ? new($"{result.Status}:{result.Id}", result.Status == RimRunner.RimRunnerOutcomes.EngineFault ? ExitCodes.InternalFault : 1, result.Id)
            : null;

    internal static IEnumerable<Item> CaseItems(EngineReport report) =>
    [
        .. report.AdjudicatingCases.Where(result => !IsExecutedOnly(result)).Select(result => CaseItem(result, regression: true)).OfType<Item>(),
        .. report.DiagnosticCases.Select(result => CaseItem(result, regression: false)).OfType<Item>(),
        .. report.AdjudicatingCases.Where(IsExecutedOnly).Select(result => CaseItem(result, regression: true)).OfType<Item>(),
    ];

    internal static IEnumerable<Item> BatchItems(EngineReport report)
    {
        List<Item> items = [];
        if (report.EngineFaults.Count > 0)
            items.Add(new($"{RimRunner.RimRunnerOutcomes.EngineFault}:窗外×{report.EngineFaults.Count}", ExitCodes.InternalFault));
        if (report.BatchSentinels.Count > 0)
            items.Add(new($"{RimRunner.RimRunnerOutcomes.SentinelFault}:窗外×{report.BatchSentinels.Count}", 1));
        items.AddRange(report.InputsUnread.Select(path =>
            new Item("指定输入件未读到:" + FindingPresentation.FirstLineWithPointer(path, "inputsUnread"), 1)));
        if (items.Count == 0 && report.AllCases.Count == 0 && report.Rejections.Count == 0 && report.CaseRejections.Count == 0)
            items.Add(new(NoResults, 1));
        return items;
    }

    internal const string NoResults = "没有任何用例结果";

    internal const string OutputFaults = "产出未写入磁盘";

    internal static bool IsBad(EngineReportCase result) =>
        !IsExecutedOnly(result)
        && (RimRunner.RimRunnerVerdict.IsVerdictAttention(result.Status) || RimRunner.RimRunnerVerdict.IsDiagnosticAttention(result.Status));

    internal static bool IsExecutedOnly(EngineReportCase result) => result.Status == RimRunner.RimRunnerOutcomes.NoAssertions;
}
