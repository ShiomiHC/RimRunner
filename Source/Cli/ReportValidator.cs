using System.Text.Json;

namespace RimRunnerCli;

public sealed record ReportValidationResult
{
    public bool Valid { get; init; }
    public int ExitCode { get; init; }
    public string Message { get; init; } = "";
    public EngineReport? Report { get; init; }

    public string ReportPath { get; init; } = "";

    public bool Retryable { get; init; }

    public IReadOnlyList<EngineReportFinding> OutputFaults { get; init; } = [];
}

public static class ReportValidator
{
    private static bool HasSection(EngineReport report, string section) => section switch
    {
        "regressions" => report.Regressions != null,
        "readouts" => report.Readouts != null,
        _ => throw new ArgumentOutOfRangeException(
            nameof(section), section, "报告里没有这个段"),
    };

    private static bool IdentityMatches(
        JsonElement root, string key, string expected, out string actual)
    {
        actual = root.TryGetProperty(key, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "（缺席）";
        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    internal static string InputsUnreadText(IEnumerable<string> paths) =>
        "指定的输入件没有用例读到：" + string.Join("、", paths);

    private static readonly byte[] PngMagic =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static ReportValidationResult Validate(
        string path, string? expectedRunId, string? expectedCommand,
        IReadOnlyList<string>? expectedSections = null)
    {
        ReportValidationResult result = ValidateReport(path, expectedRunId, expectedCommand, expectedSections);
        return result.Report is { OutputFaults.Count: > 0 } report
            ? result with { ExitCode = ExitPolicy.Worst(result.ExitCode, ExitCodes.Incomplete), OutputFaults = report.OutputFaults,
                Message = Summary(report, outputFaults: true) }
            : result;
    }

    private static string? SchemaMismatch(JsonElement root)
    {
        const int expected = RimRunner.RimRunnerWireKeys.Report.SchemaVersion;
        if (!root.TryGetProperty(RimRunner.RimRunnerWireKeys.Report.Schema, out JsonElement schema)
            || schema.ValueKind != JsonValueKind.Number)
            return $"报告没有格式版本，本 CLI 支持的格式为 {expected}";
        return schema.TryGetInt32(out int version) && version == expected
            ? null
            : $"报告格式 {schema.GetRawText()}，本 CLI 支持的格式为 {expected}";
    }

    private static ReportValidationResult ValidateReport(
        string path, string? expectedRunId, string? expectedCommand,
        IReadOnlyList<string>? expectedSections = null)
    {
        if (!File.Exists(path))
            return Incomplete($"报告不存在：{path}");
        var file = new FileInfo(path);
        if (file.Length <= 0)
            return Incomplete($"报告为空：{path}");

        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException exception)
        {
            return new ReportValidationResult { Valid = false, ExitCode = ExitCodes.Incomplete, Retryable = true,
                Message = $"无法读取报告：{path}；{exception.Message}" };
        }

        try
        {
            using JsonDocument probe = StrictJson.ParseObject(text);
            if (expectedRunId != null
                && !IdentityMatches(probe.RootElement, "runId", expectedRunId, out string probedRunId))
                return Retryable($"报告身份不匹配：runId={probedRunId}，预期={expectedRunId}");
            if (expectedCommand != null
                && !IdentityMatches(probe.RootElement, "command", expectedCommand, out string probedCommand))
                return Retryable($"报告身份不匹配：command={probedCommand}，预期={expectedCommand}");
            if (SchemaMismatch(probe.RootElement) is string mismatch)
                return Incomplete($"{mismatch}：{path}");
        }
        catch (JsonException exception)
        {
            return Incomplete($"报告 JSON 无效：{path}；{exception.Message}");
        }

        EngineReport report;
        try
        {
            report = JsonSerializer.Deserialize<EngineReport>(text, EngineReport.SerializerOptions)
                ?? throw new JsonException("反序列化结果为 null");
        }
        catch (JsonException exception)
        {
            return Incomplete($"报告 JSON 无效或缺少必需字段：{path}；{exception.Message}");
        }

        if ((!report.HasAnySection && report.Rejections.Count == 0 && report.CaseRejections.Count == 0
             && report.EngineFaults.Count == 0)
            || report.ExcludedCases is null
            || report.ForeignStartupErrors is null
            || report.ClaimedStartupErrors is null
            || report.Acks is null
            || report.AllCases.Any(c =>
                c.Asserts is null || c.Diagnostics is null || c.FailureFingerprints is null
                || c.Artifacts is null
                || c.Screenshots is null || c.ReadoutTexts is null || c.ExpectedLogs is null
                || c.ForeignErrors is null || c.DegradedNotes is null || c.LogAcks is null
                || string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(c.Status)))
        {
            return Incomplete($"报告缺少必需字段：{path}");
        }

        string[] unknownStatuses = report.AllCases
            .Where(c => !RimRunner.RimRunnerOutcomes.IsKnown(c.Status))
            .Select(c => c.Id + "=" + c.Status)
            .ToArray();
        if (unknownStatuses.Length > 0)
        {
            return Incomplete($"报告里有本版本无法识别的 status：{string.Join("、", unknownStatuses)}；{path}");
        }

        if (expectedRunId != null && !string.Equals(report.RunId, expectedRunId, StringComparison.Ordinal))
            return Retryable($"报告身份不匹配：runId={report.RunId}，预期={expectedRunId}");
        if (expectedCommand != null && !string.Equals(report.Command, expectedCommand, StringComparison.Ordinal))
            return Retryable($"报告身份不匹配：command={report.Command}，预期={expectedCommand}");

        string[] misplaced =
        [
            .. report.AdjudicatingCases.Where(c => !RegressionStatuses.Contains(c.Status)).Select(c => $"regressions.{c.Id}={c.Status}"),
            .. report.DiagnosticCases.Where(c => !ReadoutStatuses.Contains(c.Status)).Select(c => $"readouts.{c.Id}={c.Status}"),
        ];
        if (misplaced.Length > 0)
            return Incomplete($"报告里有不属于所在段的 status：{string.Join("、", misplaced)}；{path}");

        var valid = new ReportValidationResult { Valid = true, ExitCode = ReportVerdict.ExitCode(report),
            Message = Summary(report, outputFaults: false), Report = report };

        if (report.Rejections.Count > 0)
            return valid;

        foreach (EngineReportCase result in report.AllCases)
        {
            foreach (EngineReportArtifact artifact in result.Artifacts)
            {
                ReportValidationResult? failure = ValidateArtifact(
                    artifact, expectedRunId ?? report.RunId,
                    expectedCommand ?? report.Command, result.Id);
                if (failure != null) return failure;
            }

            foreach (EngineReportScreenshot screenshot in result.Screenshots)
            {
                ReportValidationResult? failure = ValidateScreenshot(screenshot, result);
                if (failure != null) return failure;
            }

            foreach (EngineReportReadoutText readoutText in result.ReadoutTexts)
            {
                ReportValidationResult? failure = ValidateReadoutText(readoutText, result);
                if (failure != null) return failure;
            }
        }

        bool sectionsExplained = report.EngineFaults.Count > 0
                                 || report.AllCases.Any(c => c.Status == RimRunner.RimRunnerOutcomes.EngineFault)
                                 || report.CaseRejections.Count > 0 || report.InputsUnread.Count > 0;
        if (expectedSections != null && !sectionsExplained)
        {
            string[] missing =
            [
                .. expectedSections
                    .Where(section => !HasSection(report, section))
            ];
            if (missing.Length > 0)
                return Incomplete(
                    $"报告缺少本轮应有的段：{string.Join(" ", missing)}");
        }

        return valid;
    }

    private static readonly HashSet<string> RegressionStatuses =
    [
        RimRunner.RimRunnerOutcomes.Pass, RimRunner.RimRunnerOutcomes.Fail, RimRunner.RimRunnerOutcomes.NoAssertions,
        RimRunner.RimRunnerOutcomes.UnclaimedFault, RimRunner.RimRunnerOutcomes.SentinelFault, RimRunner.RimRunnerOutcomes.EngineFault,
    ];

    private static readonly HashSet<string> ReadoutStatuses =
    [
        RimRunner.RimRunnerOutcomes.Done, RimRunner.RimRunnerOutcomes.Incomplete,
        RimRunner.RimRunnerOutcomes.UnclaimedFault, RimRunner.RimRunnerOutcomes.SentinelFault, RimRunner.RimRunnerOutcomes.EngineFault,
    ];

    internal static string Summary(EngineReport report, bool outputFaults)
    {
        if (report.Rejections.Count > 0 && ReportVerdict.ExitCode(report) != ExitCodes.InternalFault)
            return "批次拒绝：" + DescribeRejections(report.Rejections);
        string[] items =
        [
            .. ReportVerdict.CaseItems(report).Concat(ReportVerdict.BatchItems(report)).Select(item => item.Text),
            .. outputFaults ? new[] { ReportVerdict.OutputFaults } : [],
        ];
        string[] parts =
        [
            .. report.CaseRejections.Count > 0 ? new[] { "用例拒绝：" + DescribeRejections(report.CaseRejections) } : [],
            .. items.Length > 0 ? new[] { $"待处理 {items.Length} 项：{string.Join(" ", items)}" } : [],
        ];
        if (parts.Length > 0)
            return string.Join("；", parts);
        string readouts = report.Readouts != null ? $" · 完成 {report.DiagnosticCases.Count} 项" : "";
        return report.AdjudicatingCases.Count > 0
            ? $"报告全部通过：{report.AdjudicatingCases.Count} 项{readouts}"
            : $"报告全部完成：{report.DiagnosticCases.Count} 项";
    }

    internal static string RejectionGateLabel(string gate) => gate switch
    {
        RimRunner.RimRunnerRejectionGate.Startup => "启动错误级日志",
        RimRunner.RimRunnerRejectionGate.Contract => "契约闸",
        RimRunner.RimRunnerRejectionGate.Command => "命令形态",
        RimRunner.RimRunnerRejectionGate.Package => "包依赖",
        RimRunner.RimRunnerRejectionGate.Quiet => "缺独占",
        RimRunner.RimRunnerRejectionGate.Registry => "登记期",
        RimRunner.RimRunnerRejectionGate.Texture => "贴图档位",
        RimRunner.RimRunnerRejectionGate.Shard => "分片",
        RimRunner.RimRunnerRejectionGate.Engine => "引擎故障",
        _ => "未登记闸：" + gate,
    };

    public static string DescribeRejections(IReadOnlyList<EngineReportRejection> rejections)
    {
        var parts = new List<string>();
        foreach (IGrouping<string, EngineReportRejection> group in
                 rejections.GroupBy(r => r.Gate))
        {
            string label = RejectionGateLabel(group.Key);
            string[] subjects =
            [
                .. group
                    .Select(r => r.Subject)
                    .Where(subject => !string.IsNullOrEmpty(subject))
                    .Cast<string>()
            ];
            parts.Add(label + " " + group.Count()
                + (subjects.Length > 0 ? "（" + string.Join(" ", subjects) + "）" : ""));
        }

        return string.Join(" · ", parts);
    }

    private static ReportValidationResult? ValidateArtifact(
        EngineReportArtifact artifact, string expectedRunId, string expectedCommand,
        string caseId)
    {
        string where = artifact.Path ?? $"artifactId={artifact.ArtifactId ?? "—"}";
        if (!string.Equals(artifact.RunId, expectedRunId, StringComparison.Ordinal))
            return Incomplete($"artifact runId 不匹配：{where}");
        if (!string.Equals(artifact.Command, expectedCommand, StringComparison.Ordinal))
            return Incomplete($"artifact command 不匹配：{where}");
        if (!string.Equals(artifact.CaseId, caseId, StringComparison.Ordinal))
            return Incomplete(
                $"artifact caseId 不匹配：{artifact.CaseId}，所在用例={caseId}；{where}");

        if (artifact.State == "error")
            return string.IsNullOrWhiteSpace(artifact.Error)
                ? Incomplete($"artifact 出图失败但未记录原因：[{caseId}] {where}")
                : null;

        if (string.IsNullOrWhiteSpace(artifact.Path)) return Incomplete($"artifact path 为空：[{caseId}] {where}");
        if (!File.Exists(artifact.Path)) return Incomplete($"artifact 文件不存在：{artifact.Path}");
        var file = new FileInfo(artifact.Path);
        if (file.Length <= 0) return Incomplete($"artifact 文件为空：{artifact.Path}");

        if (artifact.State != "complete")
            return Incomplete($"artifact 未完成：state={artifact.State} path={artifact.Path}");

        byte[] header = new byte[PngMagic.Length];
        using (FileStream stream = File.OpenRead(artifact.Path))
        {
            int read = stream.Read(header, 0, header.Length);
            if (read != header.Length || !header.SequenceEqual(PngMagic))
                return Incomplete($"artifact 的文件头不是 PNG：{artifact.Path}");
        }

        if (artifact.Frame < 0 || artifact.CompletedFrame < artifact.Frame
            || artifact.CleanupFrame < artifact.CompletedFrame)
            return Incomplete(
                $"artifact 帧序非法：frame={artifact.Frame} completedFrame={artifact.CompletedFrame} cleanupFrame={artifact.CleanupFrame} path={artifact.Path}");
        return null;
    }

    private static ReportValidationResult? ValidateScreenshot(
        EngineReportScreenshot screenshot, EngineReportCase result)
    {
        if (!string.IsNullOrWhiteSpace(screenshot.Error))
            return null;

        string where = $"[{result.Id}] {screenshot.Caption}";
        if (string.IsNullOrWhiteSpace(screenshot.Path))
            return Incomplete($"截图 path 为空：{where}");

        bool linked = result.Artifacts.Any(artifact =>
            string.Equals(artifact.ArtifactId, screenshot.ArtifactId, StringComparison.Ordinal));
        return linked
            ? null
            : Incomplete($"报告里找不到 artifactId={screenshot.ArtifactId}：{where}");
    }

    private static ReportValidationResult? ValidateReadoutText(
        EngineReportReadoutText text, EngineReportCase result)
    {
        string where = $"[{result.Id}] {text.Title}";
        if (string.IsNullOrWhiteSpace(text.Title))
            return Incomplete($"读数正文缺少标题：[{result.Id}]");
        if (string.IsNullOrWhiteSpace(text.Path))
            return Incomplete($"读数正文 path 为空：{where}");
        return File.Exists(text.Path)
            ? null
            : Incomplete($"读数正文文件不存在：{where}；{text.Path}");
    }

    private static ReportValidationResult Incomplete(string message) =>
        new() { Valid = false, ExitCode = ExitCodes.Incomplete, Message = message };

    private static ReportValidationResult Retryable(string message) =>
        new() { Valid = false, ExitCode = ExitCodes.Incomplete, Retryable = true, Message = message };
}
