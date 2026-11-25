namespace RimRunnerCli;

public static class ReportPresentation
{
    public static IReadOnlyList<string> Format(
        EngineReport report, int exitCode, ReportFreshnessNote? freshness = null, SkewNote? skew = null,
        TimeSpan? wallClock = null, bool identityChecked = false,
        Func<string, string?>? otherLedgers = null, bool hasOutputFaults = false) =>
    [
        .. FormatTiered(report, exitCode, freshness, skew, wallClock, identityChecked, otherLedgers, hasOutputFaults)
            .Where(line => line.Screen != null)
            .Select(line => line.Screen!)
    ];

    internal static (IReadOnlyList<ReportLine> Lines, bool Attention) FormatCase(
        EngineReport report, string caseId, Func<string, string?>? otherLedgers)
    {
        List<string> lines = [];
        List<string> logOnly = [];
        AppendHeader(lines, report, identityChecked: false);
        EngineReportCase? result = report.AllCases.FirstOrDefault(c => c.Id == caseId);
        List<EngineReportRejection> rejections = [.. report.CaseRejections.Where(rejection => rejection.Subject == caseId)];
        if (result == null && report.Rejections.Count > 0)
            AppendRejections(lines, "[拒绝/", report.Rejections);
        else if (result == null && rejections.Count > 0)
            AppendRejections(lines, "[用例拒绝/", rejections);
        else if (result == null)
            lines.Add($"[FAIL] 报告里没有用例 {caseId}");
        else if (ReportVerdict.IsExecutedOnly(result))
            AppendExecutedOnly(lines, result);
        else
            AppendCase(lines, logOnly, result, otherLedgers);
        return ([.. lines.Select(ReportLine.Both), .. logOnly.Select(ReportLine.LogOnly)],
            result == null || ReportVerdict.IsBad(result) || ReportVerdict.IsExecutedOnly(result));
    }

    internal static IReadOnlyList<ReportLine> OutputFaultLines(IEnumerable<EngineReportFinding> faults,
        string level = "[FAIL]", string prefix = "")
    {
        List<string> screen = [];
        List<string> logOnly = [];
        FindingPresentation.Facts(faults, "outputFaults", $"[产出未写入磁盘] {prefix}", screen, logOnly,
            indent: $"{level} {prefix}产出未写入磁盘：", unit: "份");
        return [.. screen.Select(ReportLine.Both), .. logOnly.Select(ReportLine.LogOnly)];
    }

    public static IReadOnlyList<ReportLine> FormatTiered(
        EngineReport report, int exitCode, ReportFreshnessNote? freshness = null, SkewNote? skew = null,
        TimeSpan? wallClock = null, bool identityChecked = false,
        Func<string, string?>? otherLedgers = null, bool hasOutputFaults = false) =>
        Render(Compose(report, exitCode, freshness, wallClock, identityChecked, otherLedgers, hasOutputFaults), skew);

    internal readonly record struct Caveat(string Text, string Content);

    internal readonly record struct Artifact(string Id, string Text, string? Path, string Suffix);

    internal sealed record Composition(
        List<string> Lines, Group Errors, Group Warnings, Group Scope,
        List<Artifact> Screenshots, List<Artifact> ReadoutTexts,
        List<string> Tail, List<string> LogOnly, List<Caveat> Caveats, string Conclusion);

    internal static Composition ComposeSlice(EngineReport report) =>
        Compose(report, 0, null, null, identityChecked: true, otherLedgers: null);

    private static IReadOnlyList<ReportLine> Render(Composition composition, SkewNote? skew)
    {
        var tail = new List<string>();
        var artifactLog = new List<string>();
        AppendArtifacts(tail, artifactLog, composition.Screenshots, composition.ReadoutTexts);
        tail.AddRange(composition.Tail);

        var output = new List<ReportLine>();
        output.AddRange(composition.Lines.Select(ReportLine.Both));
        composition.Errors.EmitTo(output);
        composition.Warnings.EmitTo(output);
        composition.Scope.EmitTo(output);
        output.AddRange(composition.LogOnly.Select(ReportLine.LogOnly));
        output.AddRange(artifactLog.Select(ReportLine.LogOnly));
        output.AddRange(tail.Select(ReportLine.Both));
        output.Add(ReportLine.Both(composition.Conclusion));
        if (skew != null)
            output.AddRange(skew.Lines.Select(ReportLine.Both));
        return output;
    }

    private static Composition Compose(
        EngineReport report, int exitCode, ReportFreshnessNote? freshness,
        TimeSpan? wallClock, bool identityChecked, Func<string, string?>? otherLedgers,
        bool hasOutputFaults = false)
    {
        var lines = new List<string>();
        var tail = new List<string>();
        var logOnly = new List<string>();
        var errors = new Group(CliLog.ErrorTag);
        var warnings = new Group(CliLog.WarnTag);
        var scope = new Group("[本轮范围] ");
        AppendHeader(lines, report, identityChecked);
        if (freshness != null)
            lines.AddRange(freshness.Lines);
        IReadOnlyList<EngineReportCase> adjudicating = report.AdjudicatingCases;
        IReadOnlyList<EngineReportCase> results = report.AllCases;
        IReadOnlyList<EngineReportCase> verdictless = report.DiagnosticCases;
        List<EngineReportCase> bad = [.. adjudicating.Where(ReportVerdict.IsBad), .. verdictless.Where(ReportVerdict.IsBad)];
        List<EngineReportCase> executed = [.. adjudicating.Where(ReportVerdict.IsExecutedOnly)];

        AppendRejections(lines, "[拒绝/", report.Rejections);
        EngineReportAck[] missedStartupAcks = report.Rejections.Any(r => r.Gate == RimRunner.RimRunnerRejectionGate.Startup)
            ? [.. report.Acks.Where(ack => ack.Kind == EngineReportAck.StartupKind && ack.Hits == 0)]
            : [];
        AppendMissedAcks(lines, missedStartupAcks);
        AppendRejections(lines, "[用例拒绝/", report.CaseRejections);
        AppendBatchFaults(lines, logOnly, report);

        AppendCases(lines, logOnly, [.. bad.Select(result => (result, (string?)null))], otherLedgers);

        foreach (EngineReportCase result in executed)
            AppendExecutedOnly(lines, result);

        string[] excludedIds =
        [
            .. report.ExcludedCases
                .Where(s => !string.IsNullOrEmpty(s))
        ];
        string[] inapplicableIds = InapplicableIds(report);
        if (excludedIds.Length + inapplicableIds.Length > 0)
        {
            string Join(string inapplicable) => string.Join(" · ", new[]
            {
                string.Join(" ", excludedIds),
                inapplicable
            }.Where(part => part.Length > 0));
            scope.Section("本轮排除").Add("[本轮排除] " + Join(inapplicableIds.Length > 0 ? "不适用 " + string.Join(" ", inapplicableIds) : ""));
            logOnly.Add($"[本轮排除] {excludedIds.Length + inapplicableIds.Length} 条：" + Join(DescribeInapplicable(report)));
        }

        string[] groupSkipped = [.. report.GroupSkippedCases.Where(id => !string.IsNullOrEmpty(id))];
        if (groupSkipped.Length > 0)
        {
            string head = $"[按组跳过] {groupSkipped.Length} 条产品用例不在组 "
                          + string.Join("、", StructuredRunRequest.GroupsFromCommand(report.Command)) + " 里，本轮未执行";
            scope.Section("按组跳过").Add(head);
            logOnly.Add(head + "：" + string.Join(" ", groupSkipped));
        }

        if (report.AbsentCases is { Count: > 0 } absent)
        {
            var byPackage = absent
                .GroupBy(kv => kv.Value, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
            string head = $"[本轮缺席] {absent.Count} 个用例类未装载，所依赖的包不在名单上也未装载：";
            scope.Section("本轮缺席").Add(head + string.Join(" · ", byPackage.Select(g => g.Key + " ×" + g.Count())));
            logOnly.Add(head + string.Join(" · ", byPackage.Select(g =>
                g.Key + " → " + string.Join(" ", g.Select(kv => kv.Key).Order(StringComparer.Ordinal)))));
        }

        if (report.OutOfBatchFaults.Count > 0)
        {
            errors.Section("批外用例问题", $"{report.OutOfBatchFaults.Count} 条批外用例问题");
            List<string> outOfBatch = [$"[批外用例问题] {report.OutOfBatchFaults.Count} 条，不改本批结论；这些用例选进批时被拒："];
            AppendRejections(outOfBatch, "[批外用例问题/", report.OutOfBatchFaults);
            errors.AddBlock("批外用例问题", outOfBatch);
        }

        if (report.InputsUnread.Count > 0)
        {
            List<string> unread = errors.Section("输入件", $"{report.InputsUnread.Count} 份指定的输入件没有用例读到");
            unread.Add("[输入件] " + ReportValidator.InputsUnreadText(
                    report.InputsUnread.Select(path => FirstLineWithPointer(path, "inputsUnread"))));
        }

        AppendWarnings(warnings, logOnly,
            [.. report.Warnings, .. report.AllCases.SelectMany(c => c.Warnings)],
            [.. report.WarningsAcked, .. report.AllCases.SelectMany(c => c.WarningsAcked)],
            report.SeenLogsFile != null);

        string[] onParallelSite = [.. report.AllCases.Where(c => c.OnParallelSite).Select(c => c.Id)];
        if (onParallelSite.Length > 0)
            lines.Add(FindingPresentation.ListLine($"[并行落点替补] {onParallelSite.Length} 条：", onParallelSite, " ", logOnly, "条"));

        if (!string.IsNullOrEmpty(report.CompatOff))
            scope.Section("兼容关闭").Add($"[兼容关闭] {CompatMirror.Describe(report.CompatOff)}：{string.Join("、", report.CompatOff.Split(',').Select(CompatMirror.Short))}");

        if (!string.IsNullOrEmpty(report.CustomSave))
        {
            var customSection = scope.Section("指定存档");
            customSection.Add($"[指定存档] {report.CustomSave}");
            foreach (string note in report.CustomSaveNotes ?? [])
                customSection.Add("[指定存档] " + note);
        }

        var specified = report.Inputs.Where(input => input.Source == RimRunner.RimRunnerWireKeys.Report.InputSpecified).ToList();
        if (specified.Count > 0)
        {
            var inputSection = scope.Section("指定输入件");
            foreach (EngineReportInput input in specified)
                inputSection.Add($"[指定输入件] {input.Path} ← " + (input.Cases.Count == 0 ? "用例窗外" : string.Join(",", input.Cases)));
        }

        if (report.Experiments != null
            && !RimRunner.RimRunnerExperiments.Listed(report.Experiments, RimRunner.RimRunnerExperiments.Trace))
            scope.Section("溯源档").Add("[溯源档] 关");
        string experiments = RimRunner.RimRunnerExperiments.Join(name => name != RimRunner.RimRunnerExperiments.Trace
                                                                          && RimRunner.RimRunnerExperiments.Listed(report.Experiments, name));
        if (experiments.Length > 0)
        {
            int forced = report.ForcedMainMap?.Count(s => !string.IsNullOrEmpty(s)) ?? 0;
            scope.Section("实验开关").Add($"[实验开关] {experiments}" + (forced > 0 ? $" · 强制主图 {forced} 条" : ""));
        }

        AppendSection(warnings.Section("环境窗口"), "[环境窗口]",
            report.SeenLogsFile != null ? report.AmbientWindowsFresh : report.AmbientWindows);
        bool seenBaseline = report.SeenLogsFile != null;
        string[] expectedLines =
        [
            .. results.SelectMany(c =>
                (seenBaseline ? c.ExpectedLogsFresh : c.ExpectedLogs)
                .Where(s => !string.IsNullOrEmpty(s)).Select(s => $"[{c.Id}] {s}"))
        ];
        if (expectedLines.Length > 0)
        {
            int expectedAll = results.Sum(c => NonEmpty(c.ExpectedLogs).Length);
            string header = seenBaseline ? "[预期错误级日志·新]" : "[预期错误级日志]";
            logOnly.Add(header + (seenBaseline
                ? $" 本名单下首次出现 {expectedLines.Length} 条；其余 {expectedAll - expectedLines.Length} 条复现，全列见报告 expectedLogs"
                : $" 无跨轮基线，{expectedAll} 条全文输出"));
            logOnly.AddRange(expectedLines.Select(line => header + " " + line));
        }
        AppendLogAcks(logOnly, results, seenBaseline);
        AppendMissedAcks(logOnly, [.. report.Acks.Except(missedStartupAcks)]);
        AppendSection(errors.Section("台账故障", $"{NonEmpty(report.LedgerFaults).Length} 处台账故障"), "[台账故障]",
            report.LedgerFaults.Select(fault => FirstLineWithPointer(fault, "ledgerFaults")));
        if (NonEmpty(report.ReloadErrors) is { Length: > 0 } reloadErrors)
        {
            errors.Section("载档错误级日志", $"{reloadErrors.Length} 条载档错误级日志").Add(ReloadErrorsHead);
            logOnly.AddRange(errors.AddNodes("载档错误级日志", ReloadErrorNodes(reloadErrors), ReloadErrorsPrefix));
        }

        AppendGapErrors(errors, logOnly, report);
        logOnly.AddRange(warnings.AddNodes("日志封顶", FindingPresentation.FactNodes(report.LogCapHits, "fact", "logCapHits", "[日志封顶]"),
            "[日志封顶] "));
        AppendByCase(warnings, "警告级日志", logOnly, "[警告级日志]", "newWarnings", results.SelectMany(c =>
            c.NewWarnings.Where(s => !string.IsNullOrEmpty(s))
                .Select(s => (c.Id, s + ReferenceLossTagOf(s)))));
        string[] foreignStartupTexts = NonEmpty(report.ForeignStartupErrors);
        int foreignStartup = foreignStartupTexts.Length;
        int foreignRuntime = results.Sum(c => NonEmpty(c.ForeignErrors).Length);
        errors.Section("他 mod 启动错误级日志", $"不参与裁决的 {foreignStartup} 条他 mod 启动错误级日志", foreignStartupTexts);
        logOnly.AddRange(errors.AddNodes("他 mod 启动错误级日志", FindingPresentation.FactNodes(
            foreignStartupTexts.Select(text => new EngineReportFinding { Key = null, Subject = null, Text = text }),
            "fact", "foreignStartupErrors", "[他 mod 启动错误级日志]"), "[他 mod 启动错误级日志] "));
        string[] unattributedStartupTexts = NonEmpty(report.UnattributedStartupErrors ?? []);
        errors.Section("来源未定的启动错误级日志", $"不参与裁决的 {unattributedStartupTexts.Length} 条来源未定的启动错误级日志",
            unattributedStartupTexts);
        logOnly.AddRange(errors.AddNodes("来源未定的启动错误级日志", FindingPresentation.FactNodes(
            unattributedStartupTexts.Select(text => new EngineReportFinding { Key = null, Subject = null, Text = text }),
            "fact", RimRunner.RimRunnerWireKeys.Report.UnattributedStartupErrors, "[来源未定的启动错误级日志]"),
            "[来源未定的启动错误级日志] "));
        AppendSection(logOnly, "[名单认领的启动错误级日志]", report.ClaimedStartupErrors);
        errors.Section("他 mod 运行错误级日志", $"不参与裁决的 {foreignRuntime} 条他 mod 运行错误级日志",
            results.SelectMany(c => NonEmpty(c.ForeignErrors).Select(s => c.Id + " " + s)));
        AppendByCase(errors, "他 mod 运行错误级日志", logOnly, "[他 mod 运行错误级日志]", "foreignErrors", results.SelectMany(c =>
                c.ForeignErrors.Where(s => !string.IsNullOrEmpty(s)).Select(s => (c.Id, s))));

        List<string> reloads = warnings.Section("回档");
        if (report.FaultReloads > 0)
        {
            reloads.Add($"[回档] 本轮因用例出错回档 {report.FaultReloads} 次");
        }

        if (report.MapReloads > 0)
        {
            reloads.Add($"[回档] 本轮因主图无法还原回档 {report.MapReloads} 次");
        }

        warnings.Section("屏障重新执行").AddRange(report.CheckpointRetries.Select(line => "[屏障重新执行] " + FirstLineWithPointer(line, "checkpointRetries")));

        AppendBreakerNotes(warnings.Section("熔断"), report);

        (List<Artifact> screenshots, List<Artifact> readoutTexts) = ArtifactsOf(report);
        List<string> degraded = [];
        AppendDegraded(degraded, report);
        warnings.AddBlock("本次降级", degraded);

        AppendFlakyHistory(tail, logOnly, results);

        DiagnosticCounts counts = CountDiagnostics(report);
        string[] nonZero =
        [
            .. new (string Label, int Count)[]
            {
                ("截图", counts.Screenshots), ("降级", counts.Degraded), ("偶发", counts.Flaky),
                ("残留", counts.Residue), ("排除", counts.Excluded), ("预期错误级日志", counts.ExpectedLog),
                ("许可认领", counts.LogAckHit),
                ("警告级日志", counts.Warning), ("他 mod 启动错误级日志", counts.ForeignStartup),
                ("来源未定的启动错误级日志", counts.UnattributedStartup),
                ("名单认领启动错误级日志", counts.ClaimedStartup), ("他 mod 运行错误级日志", counts.ForeignRuntime),
                ("诊断", counts.Diagnostic), ("窗外错误级日志", report.GapErrorCount),
            }.Where(item => item.Count != 0).Select(item => item.Label + " " + item.Count)
        ];
        logOnly.Add("[汇总] " + (nonZero.Length == 0 ? "全零" : string.Join(" · ", nonZero)));

        AppendElapsed(tail, logOnly, report, results, wallClock);
        AppendMachineLoad(tail, logOnly, results, BusyCap.PerformanceSets()?.Select(set => (set.Group, set.Core)).Distinct().Count());

        string modListName = string.IsNullOrEmpty(report.ModList) ? "无（继承本机 ModsConfig）" : report.ModList;
        bool compatOff = !string.IsNullOrEmpty(report.CompatOff);
        string modListLine = "[名单] " + modListName
            + " · 台账 " + (compatOff
                ? "兼容关闭的批次不计台账"
                : string.IsNullOrEmpty(report.LedgerFile)
                    ? "本轮未入账"
                    : report.LedgerFile);
        if (string.IsNullOrEmpty(report.LedgerFile) && !compatOff)
            warnings.Section("名单").Add(modListLine);
        else
            logOnly.Add(modListLine);
        logOnly.Add("[能力] " + Capabilities(report.ProductCapabilities));
        if (BorrowedLine(report.Borrowed) is string borrowedLine)
            logOnly.Add(borrowedLine);
        if (InputsLine(report.Inputs) is string inputsLine)
            logOnly.Add(inputsLine);
        string modListSuffix = " · 名单 " + (string.IsNullOrEmpty(report.ModList) ? "无" : report.ModList);

        var caveats = new List<Caveat>(errors.Caveats());
        if (report.LogCapHits.Count > 0)
            caveats.Add(new Caveat($"日志封顶 {report.LogCapHits.Count} 次",
                string.Join("\n", report.LogCapHits.Select(hit => hit.Key + " " + hit.Text))));
        if (SpecifiedReaders(report.Inputs) is { Count: > 0 } readers)
            caveats.Add(new Caveat("读指定输入件的用例 " + string.Join(",", readers),
                string.Join("\n", report.Inputs.Where(input => input.Source == RimRunner.RimRunnerWireKeys.Report.InputSpecified)
                    .Select(input => input.Path + " ← " + (input.Cases.Count == 0 ? "用例窗外" : string.Join(",", input.Cases))))));
        List<ReportVerdict.Item> items = [.. ReportVerdict.CaseItems(report), .. ReportVerdict.BatchItems(report)];
        int judged = ReportVerdict.ExitCode(report);
        int finalExitCode = ExitPolicy.Worst(exitCode, judged);
        if (hasOutputFaults || report.OutputFaults.Count > 0 || (finalExitCode == ExitCodes.Incomplete && finalExitCode != judged))
            items.Add(new(ReportVerdict.OutputFaults, ExitCodes.Incomplete));
        string conclusion = Conclusion(new ConclusionFacts(
            finalExitCode, SectionsOf(report), [.. caveats.Select(caveat => caveat.Text)],
            finalExitCode == ExitCodes.Rejected && report.Rejections.Count > 0 ? ReportValidator.DescribeRejections(report.Rejections) : null,
            report.CaseRejections.Count > 0 ? ReportValidator.DescribeRejections(report.CaseRejections) : null,
            [.. items.Select(item => item.Text)], modListSuffix), logOnly);

        return new Composition(lines, errors, warnings, scope, screenshots, readoutTexts, tail, logOnly, caveats, conclusion);
    }

    internal static void AppendCases(
        List<string> lines, List<string> logOnly, IReadOnlyList<(EngineReportCase Case, string? Note)> cases,
        Func<string, string?>? otherLedgers)
    {
        List<ScreenNode> nodes = [.. cases.Select(item => CaseNode(item.Case, otherLedgers, item.Note))];
        IReadOnlyList<MergedNode> merged = ScreenTree.Merge(nodes);
        if (merged.Count <= FindingPresentation.ScreenCases)
        {
            (List<string> screen, List<string> log) = ScreenTree.Render(merged, "", "  ");
            lines.AddRange(screen);
            logOnly.AddRange(log);
            return;
        }

        foreach (ScreenNode node in nodes)
        {
            (List<string> block, List<string> blockLogOnly) = ScreenTree.Render([node], "", "  ");
            logOnly.AddRange(block);
            logOnly.AddRange(blockLogOnly);
        }

        lines.Add($"[待处理摘要] {cases.Count} 条，全列见 RimRunner 日志");
        var groups = cases
            .GroupBy(item => (item.Case.Status, Cause: CauseOf(item.Case)))
            .OrderBy(group => group.Key.Status == RimRunner.RimRunnerOutcomes.EngineFault ? 0 : 1)
            .ThenByDescending(group => group.Count())
            .ToList();
        foreach (var group in groups.Take(FindingPresentation.ScreenCauseGroups))
        {
            lines.Add(FindingPresentation.ListLine(
                $"  [{group.Key.Status}] {group.Key.Cause} —— {group.Count()} 条：",
                [.. group.Select(item => item.Case.Id)], " ", logOnly, "条", maxChars: FindingPresentation.ScreenLineChars));
        }

        if (groups.Count > FindingPresentation.ScreenCauseGroups)
        {
            lines.Add("  " + FindingPresentation.Omitted(groups.Count - FindingPresentation.ScreenCauseGroups, "组", FindingPresentation.InLog));
            logOnly.AddRange(groups.Skip(FindingPresentation.ScreenCauseGroups).Select(group =>
                $"[待处理摘要] [{group.Key.Status}] {group.Key.Cause} —— {group.Count()} 条：{string.Join(" ", group.Select(item => item.Case.Id))}"));
        }
    }

    private static string[] AssertParts(EngineReportAssert assert) =>
        RimRunner.RimRunnerAssertLine.Parts(assert.Label, assert.Expected, assert.Actual);

    private static string CauseOf(EngineReportCase result)
    {
        string? cause = result.EngineFaults.FirstOrDefault()
            ?? (result.Status is not ("SENTINEL_FAULT" or "ENGINE_FAULT") && !string.IsNullOrEmpty(result.ErrorSummary) ? result.ErrorSummary : null)
            ?? result.SentinelFaults.FirstOrDefault()?.Text
            ?? result.Asserts.Where(a => !a.Pass).Select(a => string.Concat(AssertParts(a))).FirstOrDefault();
        if (string.IsNullOrEmpty(cause))
            return "未记录成因";
        int newline = cause.IndexOf('\n');
        string head = FieldPrefix.Replace((newline < 0 ? cause : cause[..newline]).TrimEnd('\r'), "");
        return head.Length <= FindingPresentation.ScreenCauseChars ? head : head[..FindingPresentation.ScreenCauseChars] + "…";
    }

    private static readonly System.Text.RegularExpressions.Regex FieldPrefix = new(@"^[A-Za-z]+\[\d+\]: ",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static void AppendCase(
        List<string> lines, List<string> logOnly, EngineReportCase result, Func<string, string?>? otherLedgers,
        string? note = null)
    {
        (List<string> screen, List<string> log) = ScreenTree.Render([CaseNode(result, otherLedgers, note)], "", "  ");
        lines.AddRange(screen);
        logOnly.AddRange(log);
    }

    private static ScreenNode CaseNode(EngineReportCase result, Func<string, string?>? otherLedgers, string? note)
    {
        static ScreenNode Line(string kind, string text) => new(kind, SubjectStyle.Fact, null, text, ScreenNode.None);
        List<ScreenNode> children = [];
        if (note != null)
            children.Add(Line("note", note));
        if (otherLedgers != null && (result.Status is "FAIL" or "UNCLAIMED_FAULT") && HasNoHistory(result)
            && otherLedgers(result.Id) is string other)
            children.Add(Line("ledger", other));
        children.AddRange(result.EngineFaults.Select(fault => Line("engine", FirstLineWithPointer(fault, "engineFaults"))));
        if (result.Status is not ("SENTINEL_FAULT" or "ENGINE_FAULT") && !string.IsNullOrEmpty(result.ErrorSummary))
            children.Add(Line("summary", FirstLineWithPointer(result.ErrorSummary, "errorSummary") + ExceptionStackPointer(result.ErrorSummary)));
        int stacks = result.NewErrors.Count(error => error.Stack != null);
        if (stacks > 0)
            children.Add(Line("stacks", $"调用栈 {stacks} 块见报告 newErrors"));
        children.AddRange(FindingPresentation.FactNodes(result.SentinelFaults, "sentinel", "sentinelFaults", "[哨兵] " + result.Id));
        children.AddRange(FindingPresentation.FactNodes(result.FailureFingerprints, "fingerprint", "failureFingerprints", "[失败指纹] " + result.Id));
        if (result.Status != RimRunner.RimRunnerOutcomes.Incomplete)
            children.AddRange(result.Asserts.Where(a => !a.Pass).Select(a => VerdictNode(result.Id, a)));
        children.AddRange(DiagnosticNodes(result.Diagnostics));
        return new ScreenNode("case", SubjectStyle.Case, result.Id, $"[{result.Status}]", children)
        {
            Suffix = SaveNote(result),
            Budget = DiagnosticBudget,
        };
    }

    private static ScreenNode VerdictNode(string caseId, EngineReportAssert assert)
    {
        List<ScreenNode> children =
        [
            .. assert.Offending.Select(item => new ScreenNode("offending", SubjectStyle.Fact, null, FirstLineWithPointer(item, "asserts"), ScreenNode.None)),
        ];
        if (!string.IsNullOrEmpty(assert.Source))
            children.Add(new ScreenNode("source", SubjectStyle.Fact, null, "source: " + FirstLineWithPointer(assert.Source, "asserts"), ScreenNode.None)
                { Trailing = true, MarkAt = "source".Length, Name = "source" });
        if (!string.IsNullOrEmpty(assert.Explanation))
            children.Add(new ScreenNode("explanation", SubjectStyle.Fact, null, FirstLineWithPointer(assert.Explanation, "asserts"), ScreenNode.None)
                { Trailing = true, Name = "说明" });
        string? handle = FindingPresentation.AssertHandle(assert.Ordinal);
        return new ScreenNode("verdict", SubjectStyle.Label, FindingPresentation.FirstLinesWithPointer("asserts", assert.Label),
            (handle == null ? "" : handle + " ") + FindingPresentation.FirstLinesWithPointer("asserts", AssertParts(assert)), children)
        {
            Handle = handle,
            Identity = string.Join("\u0001", new[] { assert.Expected, assert.Actual, assert.Source ?? "", assert.Explanation ?? "" }.Concat(assert.Offending)),
            LogPrefix = $"[同一判定] {caseId} ",
            Budget = new Budget("offending", FindingPresentation.OffendingPerAssert, "项", FindingPresentation.InLog,
                $"[违例] {caseId} {FirstLineWithPointer(assert.Label, "asserts")}：", LogOverflow: true),
        };
    }

    internal static void AppendExecutedOnly(List<string> lines, EngineReportCase result)
    {
        lines.Add($"[NO_ASSERTIONS] {result.Id}");
        int degraded = NonEmpty(result.DegradedNotes).Length;
        if (result.Asserts.Count == 0 && degraded > 0)
            lines.Add($"  降级 {degraded} 条，全文在报告 degradedNotes");
    }

    internal sealed record ConclusionFacts(
        int ExitCode, IReadOnlyList<(string Name, int Count)> Sections, IReadOnlyList<string> Caveats,
        string? Rejected, string? CaseRejected, IReadOnlyList<string> Attention, string ModListSuffix);

    internal static string Conclusion(ConclusionFacts facts, List<string> logOnly) =>
        FindingPresentation.FitKeepingTail(ConclusionBody(facts, logOnly), facts.ModListSuffix, logOnly);

    private static string ConclusionBody(ConclusionFacts facts, List<string> logOnly)
    {
        string Pending(string before) => FindingPresentation.ListLine(
            $"待处理 {facts.Attention.Count} 项：", facts.Attention, " ", logOnly, "项", logHead: $"[结果] 待处理 {facts.Attention.Count} 项：",
            maxChars: FindingPresentation.ScreenLineChars - before.Length - facts.ModListSuffix.Length);
        if (facts.ExitCode == 0)
        {
            int adjudicating = SectionCount(facts.Sections, "regressions") ?? 0;
            int? readouts = SectionCount(facts.Sections, "readouts");
            int verdictless = readouts ?? 0;
            string caveatSuffix = facts.Caveats.Count == 0 ? "" : $"；另有 {string.Join("、", facts.Caveats)}";
            return (adjudicating == 0
                ? $"[结果] ALL DONE —— {verdictless} 项（{DescribeSections(facts.Sections)}）{caveatSuffix}"
                : (facts.Caveats.Count == 0
                      ? $"[结果] ALL PASS —— {adjudicating} 项"
                      : $"[结果] ALL PASS（回归用例）—— 通过 {adjudicating} 项{caveatSuffix}")
                  + (readouts != null ? $" · 完成 {verdictless} 项" : ""));
        }

        if (facts.Rejected != null)
            return "[结果] REJECTED —— 批次拒绝：" + facts.Rejected;
        if (facts.CaseRejected != null)
        {
            string rejected = "[结果] FAIL —— 用例拒绝：" + facts.CaseRejected;
            return rejected + (facts.Attention.Count > 0 ? "；" + Pending(rejected + "；") : "");
        }

        if (facts.Attention.Count > 0)
            return "[结果] FAIL —— " + Pending("[结果] FAIL —— ");
        return $"[结果] FAIL —— 退出码 {facts.ExitCode}";
    }

    private static void AppendBatchFaults(List<string> lines, List<string> logOnly, EngineReport report)
    {
        foreach (EngineReportEngineFault fault in report.EngineFaults)
        {
            lines.Add($"[{RimRunner.RimRunnerOutcomes.EngineFault}/窗外] {fault.Where}"
                + (string.IsNullOrEmpty(fault.CaseId) ? "" : $"（当时用例 {fault.CaseId}）"));
            lines.Add("  " + FirstLineWithPointer(fault.Text, "engineFaults"));
        }

        string head = $"[{RimRunner.RimRunnerOutcomes.SentinelFault}/窗外] ";
        FindingPresentation.Facts(report.BatchSentinels, "batchSentinels", head.TrimEnd(), lines, logOnly, indent: head);
    }

    private static string FirstLineWithPointer(string text, string field) => FindingPresentation.FirstLineWithPointer(text, field);

    internal static void AppendMachineLoad(
        List<string> lines, List<string> logOnly, IReadOnlyList<EngineReportCase> results, int? performanceCores)
    {
        List<EngineReportCase> measured = [.. results.Where(c => c.OtherBusyCores != null)];
        if (measured.Count == 0)
            return;

        foreach (EngineReportCase result in measured)
            logOnly.Add($"[同机负载] {result.Id} 本实例以外的忙核数 {result.OtherBusyCores:F2} · 窗 {Elapsed(result.OtherBusyWindowMs)}");

        EngineReportCase peak = measured.MaxBy(c => c.OtherBusyCores!.Value)!;
        double low = measured.Min(c => c.OtherBusyCores!.Value);
        string range = measured.Count == 1
            ? $"{peak.OtherBusyCores:F2}"
            : $"{low:F2}–{peak.OtherBusyCores:F2}（最高 {peak.Id}）";
        lines.Add($"[同机负载] 用例窗内本实例以外的忙核数 {range}"
            + (performanceCores is int cores ? $" · 本机 P 核 {cores}" : ""));
    }

    private static void AppendElapsed(
        List<string> lines, List<string> logOnly, EngineReport report, IReadOnlyList<EngineReportCase> results,
        TimeSpan? wallClock)
    {
        var parts = new List<string>();
        long engineMs = report.DurationMs;
        long reloadMs = report.ReloadMs;
        long checkpointMs = results.Sum(c => c.CheckpointMs);
        long scanMs = report.ContractScanMs;
        var detail = new List<string> { "用例 " + Elapsed(Math.Max(engineMs - checkpointMs, 0)) };
        if (reloadMs > 0)
            detail.Add("载档" + (report.ReloadCount > 0 ? $"×{report.ReloadCount}" : "") + " " + Elapsed(reloadMs));
        if (checkpointMs > 0) detail.Add("存档 " + Elapsed(checkpointMs));
        if (scanMs > 0) detail.Add("契约扫描 " + Elapsed(scanMs));
        parts.Add("引擎 " + Elapsed(engineMs + reloadMs + scanMs)
            + "（" + string.Join(" · ", detail) + "）");

        if (wallClock is TimeSpan wall)
        {
            parts.Add("端到端 " + Elapsed((long)wall.TotalMilliseconds));
            lines.Add("[用时] 端到端 " + Elapsed((long)wall.TotalMilliseconds));
        }

        logOnly.Add("[用时] " + string.Join(" · ", parts));
    }

    internal static string? InputsLine(List<EngineReportInput> inputs)
    {
        var groups = inputs.Where(input => input.Source != RimRunner.RimRunnerWireKeys.Report.InputOwnTree
                                           && input.Source != RimRunner.RimRunnerWireKeys.Report.InputSpecified)
            .GroupBy(input => input.Source).ToList();
        return groups.Count == 0
            ? null
            : "[输入] " + string.Join(" · ", groups.Select(group => group.Key + "："
                + string.Join("、", group.Select(input => input.Path
                    + (input.Cases.Count == 0 ? "" : "（" + string.Join(",", input.Cases) + "）")))));
    }

    internal static List<string> SpecifiedReaders(List<EngineReportInput> inputs) =>
    [
        .. inputs.Where(input => input.Source == RimRunner.RimRunnerWireKeys.Report.InputSpecified)
            .SelectMany(input => input.Cases).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
    ];

    internal static string? BorrowedLine(Dictionary<string, List<string>> borrowed)
    {
        var layers = borrowed.Select(pair => (Layer: pair.Key, Items: new List<string>(pair.Value))).ToList();
        layers.RemoveAll(layer => layer.Items.Count == 0);
        return layers.Count == 0
            ? null
            : "[借读] " + string.Join(" · ", layers.Select(layer => layer.Layer + "：" + string.Join("、", layer.Items)));
    }

    internal static string Capabilities(IReadOnlyList<string>? names)
    {
        if (names == null) return "未记录";
        if (names.Count == 0) return "无登记";
        var groups = new List<(string Name, List<string> Args, int Count)>();
        foreach (string entry in names)
        {
            int colon = entry.IndexOf(':');
            string name = colon < 0 ? entry : entry[..colon];
            int index = groups.FindIndex(group => group.Name == name);
            if (index < 0)
            {
                groups.Add((name, [], 0));
                index = groups.Count - 1;
            }

            (string Name, List<string> Args, int Count) = groups[index];
            if (colon >= 0) Args.Add(entry[(colon + 1)..]);
            groups[index] = (Name, Args, Count + 1);
        }

        return string.Join(" · ", groups.Select(group =>
            group.Args.Count is > 0 and <= 4 ? $"{group.Name}({string.Join("、", group.Args)})"
            : group.Count > 1 ? $"{group.Name}×{group.Count}"
            : group.Name));
    }

    internal static string Elapsed(long ms)
    {
        if (ms < 1000) return ms + "ms";
        if (ms < 60_000) return (ms / 1000.0).ToString("0.0") + "s";
        long totalSeconds = ms / 1000;
        long hours = totalSeconds / 3600;
        long minutes = totalSeconds % 3600 / 60;
        long seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours}h{minutes:00}m{seconds:00}s" : $"{minutes}m{seconds:00}s";
    }

    private static void AppendHeader(List<string> lines, EngineReport report, bool identityChecked)
    {
        List<(string Name, int Count)> sections = SectionsOf(report);
        if (identityChecked)
        {
            string[] empty = [.. sections.Where(section => section.Count == 0).Select(section => section.Name)];
            if (empty.Length > 0 && report.Rejections.Count == 0 && report.CaseRejections.Count == 0)
                lines.Add("[本轮] 选中而执行 0 条：" + string.Join(" + ", empty));
            return;
        }

        lines.Add("[本轮] " + report.Command);
        lines.Add("       段 " + DescribeSections(sections) + " · runId " + report.RunId
                  + (report.TextureTier == null ? "" : " · 贴图 " + report.TextureTier));
    }

    internal static List<(string Name, int Count)> SectionsOf(EngineReport report)
    {
        var sections = new List<(string, int)>();
        Note("regressions", report.Regressions);
        Note("readouts", report.Readouts);
        return sections;

        void Note(string name, List<EngineReportCase>? section)
        {
            if (section != null)
                sections.Add((name, section.Count));
        }
    }

    private static int? SectionCount(IReadOnlyList<(string Name, int Count)> sections, string name) =>
        sections.Where(section => section.Name == name).Select(section => (int?)section.Count).FirstOrDefault();

    internal static string DescribeSections(IReadOnlyList<(string Name, int Count)> sections) =>
        sections.Count == 0
            ? "（无）"
            : string.Join(" + ", sections.Select(section => $"{section.Name}({section.Count})"));

    private const int DefaultBreakerPumpThreshold = RimRunner.RimRunnerEngineArgs.BreakerDefaultPumps;
    private const int DefaultBreakerErrorThreshold = RimRunner.RimRunnerEngineArgs.BreakerDefaultErrors;

    private static void AppendBreakerNotes(List<string> lines, EngineReport report)
    {
        bool customThreshold = report.BreakerPumpThreshold != DefaultBreakerPumpThreshold
            || report.BreakerErrorThreshold != DefaultBreakerErrorThreshold;
        string? thresholdSuffix = customThreshold
            ? $"阈 {report.BreakerPumpThreshold} 泵、{report.BreakerErrorThreshold} 条"
            : null;

        if (report.BreakerTrips > 0)
        {
            lines.Add("[熔断] 本轮触发 " + report.BreakerTrips + " 次"
                + (thresholdSuffix == null ? "" : "，" + thresholdSuffix));
        }
        else if (thresholdSuffix != null)
        {
            lines.Add("[熔断] 本轮" + thresholdSuffix);
        }
    }

    private static (List<Artifact> Screenshots, List<Artifact> ReadoutTexts) ArtifactsOf(EngineReport report) =>
    (
        [
            .. report.AllCases.SelectMany(c => c.Screenshots
                .Select(shot => string.IsNullOrEmpty(shot.Error)
                    ? new Artifact(c.Id, FirstLineWithPointer(shot.Caption, "screenshots"), shot.Path,
                        string.IsNullOrEmpty(shot.Note) ? "" : $"（{FirstLineWithPointer(shot.Note, "screenshots")}）")
                    : new Artifact(c.Id, FirstLineWithPointer(shot.Caption, "screenshots"), null,
                        "没拍成：" + FirstLineWithPointer(shot.Error ?? "", "screenshots"))))
        ],
        [
            .. report.AllCases.SelectMany(c => c.ReadoutTexts
                .Select(text => new Artifact(c.Id, FirstLineWithPointer(text.Title, "readoutTexts"), text.Path, $"（{text.Lines} 行）")))
        ]
    );

    internal static void AppendArtifacts(List<string> lines, List<string> logOnly,
        IReadOnlyList<Artifact> screenshots, IReadOnlyList<Artifact> readoutTexts)
    {
        AppendArtifactSection(lines, logOnly, "截图", "张", screenshots, entry => entry.Path == null);
        AppendArtifactSection(lines, logOnly, "读数正文", "份", readoutTexts, _ => false);
    }

    private static void AppendDegraded(List<string> degraded, EngineReport report)
    {
        bool seenBaseline = report.SeenLogsFile != null;
        string[] skipLines =
        [
            .. report.AllCases.SelectMany(c =>
                (seenBaseline ? c.DegradedNotesFresh : c.DegradedNotes).Where(note => !string.IsNullOrEmpty(note))
                .Select(note => $"  [{c.Id}] {FirstLineWithPointer(note, "degradedNotes")}"))
        ];
        if (skipLines.Length > 0)
        {
            int degradedAll = report.AllCases.Sum(c => NonEmpty(c.DegradedNotes).Length);
            degraded.Add(seenBaseline
                ? $"[本次降级·新] 本名单下首次出现的降级，以下断言未参与本轮结论；"
                    + $"其余 {degradedAll - skipLines.Length} 条复现，全列见报告 degradedNotes"
                : "[本次降级] 无跨轮基线，以下断言未参与本轮结论：");
            degraded.AddRange(skipLines);
        }
    }

    private static void AppendArtifactSection(
        List<string> lines, List<string> logOnly, string label, string unit,
        IReadOnlyList<Artifact> entries, Func<Artifact, bool> failed)
    {
        if (entries.Count == 0)
            return;

        string[] dirs =
        [
            .. entries.Where(e => e.Path != null)
                .Select(e => System.IO.Path.GetDirectoryName(e.Path!) ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
        string? sharedDir = dirs.Length == 1 && dirs[0].Length > 0 ? dirs[0] : null;
        string Line(Artifact e) =>
            $"  [{e.Id}] {e.Text} → "
            + (e.Path == null ? "" : sharedDir != null ? System.IO.Path.GetFileName(e.Path) : e.Path)
            + e.Suffix;

        string head = $"[{label}] {entries.Count} {unit}" + (sharedDir == null ? "" : $"，在 {sharedDir}{System.IO.Path.DirectorySeparatorChar}");
        if (entries.Count <= FindingPresentation.ArtifactsListed)
        {
            lines.Add(head + "：");
            lines.AddRange(entries.Select(Line));
            return;
        }

        var shown = entries.Where(failed).ToArray();
        lines.Add(head + "；" + FindingPresentation.Omitted(entries.Count - shown.Length, unit, FindingPresentation.InLog)
            + (shown.Length == 0 ? "" : $"；没拍成的 {shown.Length} {unit}："));
        lines.AddRange(shown.Select(Line));
        logOnly.Add(head + "：");
        logOnly.AddRange(entries.Except(shown).Select(Line));
    }

    internal static void AppendRejections(
        List<string> lines, string prefix, IReadOnlyList<EngineReportRejection> rejections)
    {
        foreach (EngineReportRejection rejection in rejections)
        {
            lines.Add($"{prefix}{RejectionGateLabel(rejection.Gate)}]"
                + (string.IsNullOrEmpty(rejection.Subject) ? "" : " " + rejection.Subject));
            foreach (string line in rejection.Text.Split('\n'))
                lines.Add("  " + line.TrimEnd('\r'));
        }
    }

    private static void AppendWarnings(
        Group warnings, List<string> logOnly,
        IReadOnlyList<EngineReportFinding> open, IReadOnlyList<EngineReportFinding> acked, bool seenBaseline)
    {
        bool markFresh = seenBaseline && open.All(warning => warning.Fresh != null);
        logOnly.AddRange(warnings.AddNodes("带键警告", FindingPresentation.KeyedNodes(open, "[带键警告] ", markFresh), ""));
        if (open.Count > 0 && !markFresh)
            warnings.Section("带键警告").Add("[带键警告] 无跨轮基线，未分首次出现与复现");

        if (acked.Count > 0)
        {
            int batchAcked = acked.Count(w => w.Subject == null);
            logOnly.Add($"[带键警告] 已认领 {acked.Count} 条（"
                + $"{acked.Where(w => w.Subject != null).Select(w => w.Subject).Distinct(StringComparer.Ordinal).Count()} 条用例"
                + (batchAcked > 0 ? $"，其中名单认领 {batchAcked} 条" : "") + "）");
        }
    }

    private static void AppendGapErrors(Group errors, List<string> logOnly, EngineReport report)
    {
        int total = report.GapErrorCount;
        if (total == 0) return;
        int recorded = report.GapErrors.Count;
        errors.Section("窗外错误级日志", $"{total} 条窗外错误级日志").Add($"[窗外错误级日志] 共 {total} 条"
            + (total > recorded ? "，" + FindingPresentation.Omitted(total - recorded, "条", null) : "") + "；" + StackSearchHint);
        logOnly.AddRange(errors.AddNodes("窗外错误级日志",
            FindingPresentation.FactNodes(report.GapErrors, "fact", "gapErrors", "[窗外错误级日志]"), "[窗外错误级日志] "));
    }

    private const string ReloadErrorsPrefix = "[载档错误级日志] ";

    private static string ReloadErrorsHead => "[载档错误级日志] 不计入退出码；" + StackSearchHint;

    private static List<ScreenNode> ReloadErrorNodes(string[] present) => FindingPresentation.FactNodes(
        present.Select(text => new EngineReportFinding { Key = null, Subject = null, Text = text }), "fact", "reloadErrors", "[载档错误级日志]");

    internal static List<string> ReloadErrorLines(IEnumerable<string> errors, List<string> logOnly)
    {
        string[] present = NonEmpty(errors);
        if (present.Length == 0) return [];
        (List<string> lines, List<string> log) = ScreenTree.Render(ReloadErrorNodes(present), ReloadErrorsPrefix, "  ");
        logOnly.AddRange(log);
        return [ReloadErrorsHead, .. lines];
    }

    private const string StackSearchHint = "调用栈见本轮的 RimRunner 日志（reports/rimrunner-log/）";

    private static readonly string[] ReferenceLossWarningPrefixes =
    [
        "Trying to save reference to a discarded thing",
        "Tried to remove used tale",
        "Tried to discard "
    ];

    private const string ReferenceLossTag = "（vanilla 存档或回收时丢失一条引用）";

    private static string ReferenceLossTagOf(string warning) =>
        ReferenceLossWarningPrefixes.Any(prefix => warning.Contains(prefix, StringComparison.Ordinal))
            ? ReferenceLossTag
            : "";

    private const string ExceptionSummaryPrefix = "exception: ";

    private static string ExceptionStackPointer(string errorSummary) =>
        errorSummary.StartsWith(ExceptionSummaryPrefix, StringComparison.Ordinal)
            ? "（全栈见报告 exception 字段）"
            : "";

    private static void AppendSection(List<string> lines, string header, IEnumerable<string> items)
    {
        string[] present = [.. items.Where(s => !string.IsNullOrEmpty(s))];
        if (present.Length == 0) return;
        foreach (string item in present)
            lines.Add(header + " " + item);
    }

    private static string[] NonEmpty(IEnumerable<string> items) =>
        [.. items.Where(s => !string.IsNullOrEmpty(s))];

    private static void AppendLogAcks(List<string> lines, IReadOnlyList<EngineReportCase> results, bool seenBaseline)
    {
        var acks = results.SelectMany(result => result.LogAcks.Select(ack => (result.Id, Ack: ack))).ToList();
        foreach (var group in acks.Where(entry => entry.Ack.Hits > 0).GroupBy(entry => AckHead(entry.Ack)))
        {
            string who = string.Join("、", group.Select(entry => $"{entry.Id}×{entry.Ack.Hits}"));
            lines.Add($"[许可认领] {group.Key} —— 命中 {group.Sum(entry => entry.Ack.Hits)}：{who}");
            HashSet<string> fresh = seenBaseline ? [.. group.SelectMany(entry => entry.Ack.LinesFresh)] : [];
            foreach (IGrouping<string, string> line in group.SelectMany(entry => entry.Ack.Lines)
                         .Where(text => !string.IsNullOrEmpty(text)).GroupBy(text => text, StringComparer.Ordinal))
                lines.Add((fresh.Contains(line.Key) ? "    [新] " : "    ") + line.Key + (line.Count() > 1 ? $" ×{line.Count()}" : ""));
        }

        FindingPresentation.FactsInFull(acks.Where(entry => entry.Ack.Hits == 0)
            .Select(entry => new EngineReportFinding
            {
                Key = null, Subject = entry.Id,
                Text = AckHead(entry.Ack) + MissSuffix(entry.Ack.Misses, entry.Ack.Tolerance, entry.Ack.Unreachable)
            }),
            lines, "[许可未命中] ");
    }

    private static string MissSuffix(int? misses, int tolerance, string? unreachable) =>
        unreachable != null ? " · 本轮够不到：" + unreachable
        : misses is int count ? $" · 连续未命中 {count} 轮 / Tolerance {tolerance}"
        : "";

    private static string AckHead(EngineReportLogAck ack) =>
        (ack.Level == "warning" ? "警告级日志" : "错误级日志")
        + $" \"{ack.Pattern}\""
        + (ack.StackAnchors.Count > 0 ? " · 栈锚 " + string.Join(" + ", ack.StackAnchors.Select(s => $"\"{s}\"")) : "")
        + " · " + ack.Reason;

    private static void AppendMissedAcks(List<string> lines, IReadOnlyList<EngineReportAck> acks) =>
        FindingPresentation.FactsInFull(acks.Where(ack => ack.Hits == 0).Select(ack => new EngineReportFinding
        {
            Key = null,
            Subject = ack.Subject ?? "名单",
            Text = (ack.Kind switch
            {
                EngineReportAck.StartupKind => $"启动错误级日志 \"{ack.Target}\"",
                EngineReportAck.KnownFailingKind => $"已知失败体检 {ack.Target}",
                _ => ack.Origin == null ? $"键 {ack.Target}"
                    : ack.Anchor == null ? $"键 {ack.Target}[{ack.Origin}]"
                    : $"键 {ack.Target}[{ack.Origin} / {ack.Anchor}]"
            }) + " · " + ack.Reason + MissSuffix(ack.Misses, ack.Tolerance, ack.Unreachable),
        }), lines, "[许可未命中] ");

    private static void AppendByCase(Group group, string label, List<string> logOnly, string header, string field,
        IEnumerable<(string Id, string Text)> items) =>
        logOnly.AddRange(group.AddNodes(label, FindingPresentation.FactNodes(
            items.Select(item => new EngineReportFinding { Key = null, Subject = item.Id, Text = item.Text }), "fact", field, header), header + " "));

    private static void AppendFlakyHistory(
        List<string> lines, List<string> logOnly, IReadOnlyList<EngineReportCase> results)
    {
        EngineReportCase[] flaky =
        [
            .. results
                .Where(c => IsFlaky(c) && !string.IsNullOrEmpty(c.OutcomeTail30))
        ];
        if (flaky.Length == 0) return;

        EngineReportCase[] regressions = [.. flaky.Where(c => ShapeOf(c) == FlakyShape.FirstFailure)];
        EngineReportCase[] unseen = [.. flaky.Where(c => ShapeOf(c) == FlakyShape.NoHistory)];
        EngineReportCase[] recurring = [.. flaky.Where(c => ShapeOf(c) == FlakyShape.Recurring)];

        if (recurring.Length > 0)
        {
            lines.Add("[偶发诊断] 近 10 次里出现过未通过；下列各行列出最近 30 次");
            int width = recurring.Max(c => c.Id.Length);
            for (int i = 0; i < recurring.Length; i++)
            {
                EngineReportCase result = recurring[i];
                string tail = result.OutcomeTail30!;
                string row = "  [" + result.Id + "]"
                    + new string(' ', width - result.Id.Length) + " "
                    + tail[..^1] + "<" + tail[^1] + ">";
                (i < FindingPresentation.ScreenSubjects ? lines : logOnly).Add(row);
            }
            if (recurring.Length > FindingPresentation.ScreenSubjects)
                lines.Add("  " + FindingPresentation.Omitted(recurring.Length - FindingPresentation.ScreenSubjects, "条", FindingPresentation.InLog));
        }

        if (regressions.Length > 0)
            lines.Add(FindingPresentation.ListLine($"[本轮首次未通过] {regressions.Length} 条：",
                [.. regressions.Select(c =>
                {
                    int prior = PriorWindow(c).Length;
                    return prior < 9 ? $"{c.Id}（此前 {prior} 次全通过）" : c.Id;
                })], " ", logOnly, "条"));

        if (unseen.Length > 0)
            lines.Add(FindingPresentation.ListLine($"[无历史未通过] {unseen.Length} 条：", [.. unseen.Select(c => c.Id)], " ", logOnly, "条"));
    }

    internal static string SaveNote(EngineReportCase result)
    {
        if (result.Status != "FAIL" || !result.SaveChangedSincePass) return "";
        return result.OnMainMap
            ? "（本条上次通过之后基线档已更换；本条在基线档主图上执行）"
            : "（本条上次通过之后基线档已更换）";
    }

    private enum FlakyShape { Recurring, FirstFailure, NoHistory }

    private static FlakyShape ShapeOf(EngineReportCase result)
    {
        string tail = Samples(result);
        if (IsPassGlyph(tail[^1])) return FlakyShape.Recurring;
        string prior = PriorWindow(result);
        if (prior.Length == 0) return FlakyShape.NoHistory;
        return prior.All(IsPassGlyph) ? FlakyShape.FirstFailure : FlakyShape.Recurring;
    }

    private static bool IsPassGlyph(char glyph) => glyph == RimRunner.RimRunnerOutcomes.GlyphPass;

    private static string PriorWindow(EngineReportCase result)
    {
        string prior = Samples(result)[..^1];
        return prior.Length > 9 ? prior[^9..] : prior;
    }

    private static bool HasNoHistory(EngineReportCase result) =>
        result.OutcomeTail30 != null && Samples(result).Length == 1;

    private static string Samples(EngineReportCase result) =>
        result.OutcomeTail30!.Replace(RimRunner.RimRunnerLedgerFold.SaveGlyph.ToString(), "");

    private static readonly string[] ResidueKeys =
    [
        RimRunner.RimRunnerWarningKey.Residue, RimRunner.RimRunnerWarningKey.StrayWindow,
        RimRunner.RimRunnerWarningKey.StorytellerResidue
    ];

    public static DiagnosticCounts CountDiagnostics(EngineReport report)
    {
        IReadOnlyList<EngineReportCase> results = report.AllCases;
        return new DiagnosticCounts(
            results.Sum(c => c.Screenshots.Count),
            results.Sum(c => c.DegradedNotes.Count(s => !string.IsNullOrEmpty(s))),
            results.Count(c => IsFlaky(c) && !HasNoHistory(c)),
            report.Warnings.Concat(report.WarningsAcked)
                .Concat(results.SelectMany(c => c.Warnings.Concat(c.WarningsAcked)))
                .Count(w => ResidueKeys.Contains(w.Key)),
            report.ExcludedCases.Count(s => !string.IsNullOrEmpty(s)) + InapplicableIds(report).Length,
            results.Sum(c => c.ExpectedLogs.Count(s => !string.IsNullOrEmpty(s))),
            report.ForeignStartupErrors.Count(s => !string.IsNullOrEmpty(s)),
            (report.UnattributedStartupErrors ?? []).Count(s => !string.IsNullOrEmpty(s)),
            report.ClaimedStartupErrors.Count(s => !string.IsNullOrEmpty(s)),
            results.Sum(c => c.ForeignErrors.Count(s => !string.IsNullOrEmpty(s))),
            results.Sum(c => c.Diagnostics.Count),
            results.Sum(c => c.NewWarnings.Count(s => !string.IsNullOrEmpty(s))),
            results.Sum(c => c.LogAcks.Sum(ack => ack.Hits)));
    }

    private const string TargetSpecLabelSuffix = "/靶规格";

    internal static IEnumerable<string> DiagnosticLines(IReadOnlyList<EngineReportDiagnostic> diagnostics) =>
        ScreenTree.Render(DiagnosticNodes(diagnostics), "  ", "  ", DiagnosticBudget).Screen;

    private static List<ScreenNode> DiagnosticNodes(IReadOnlyList<EngineReportDiagnostic> diagnostics)
    {
        var entries = new List<DiagnosticEntry>();
        var foldAt = new Dictionary<(string Label, string Spec), int>();
        foreach (EngineReportDiagnostic d in diagnostics)
        {
            if (d.Kind == "trace" && d.Label.EndsWith(TargetSpecLabelSuffix, StringComparison.Ordinal))
            {
                int space = d.Value.IndexOf(' ');
                string spec = space < 0 ? d.Value : d.Value[(space + 1)..];
                if (foldAt.TryGetValue((d.Label, spec), out int at))
                {
                    entries[at].Count++;
                    continue;
                }
                foldAt[(d.Label, spec)] = entries.Count;
                entries.Add(new DiagnosticEntry(d) { FoldedValue = spec });
                continue;
            }
            (string, string) same = (d.Kind + "\u0001" + d.Label, d.Value);
            if (foldAt.TryGetValue(same, out int repeat))
            {
                entries[repeat].Count++;
                continue;
            }
            foldAt[same] = entries.Count;
            entries.Add(new DiagnosticEntry(d) { FoldedValue = d.Value });
        }

        return
        [
            .. entries.Select(e =>
            {
                string body = e.Count > 1
                    ? FindingPresentation.FirstLinesWithPointer("diagnostics", e.Source.Label, $" ×{e.Count} = ", e.FoldedValue ?? "")
                    : FindingPresentation.FirstLinesWithPointer("diagnostics", e.Source.Label, " = ", e.Source.Value);
                return new ScreenNode("diagnostic", SubjectStyle.Fact, null, $"[诊断/{e.Source.Kind}] {body}（tick {e.Source.GameTick}）", ScreenNode.None)
                    { Identity = e.Source.Kind == "trace" ? "trace" : null, ExtraIndent = 1 };
            }),
        ];
    }

    private static readonly Budget DiagnosticBudget = new("diagnostic", FindingPresentation.DiagnosticsPerCase, "条",
        FindingPresentation.InReport("diagnostics"), "", LogOverflow: false, Order: node => node.Identity == "trace" ? 1 : 0, Lead: "[诊断] ");

    private sealed class DiagnosticEntry(EngineReportDiagnostic source)
    {
        public EngineReportDiagnostic Source { get; } = source;
        public string? FoldedValue { get; init; }
        public int Count { get; set; } = 1;
    }

    private static string[] InapplicableIds(EngineReport report) =>
    [
        .. report.InapplicableCases.Keys
            .Where(id => !string.IsNullOrEmpty(id))
            .OrderBy(id => id, StringComparer.Ordinal)
    ];

    private static string DescribeInapplicable(EngineReport report)
    {
        var groups = new List<(string Reason, List<string> Ids)>();
        foreach (string id in InapplicableIds(report))
        {
            string reason = report.InapplicableCases[id].Trim();
            if (reason.Length == 0)
                reason = "未登记理由";

            int at = groups.FindIndex(group => string.Equals(group.Reason, reason, StringComparison.Ordinal));
            if (at < 0)
            {
                groups.Add((reason, []));
                at = groups.Count - 1;
            }

            groups[at].Ids.Add(id);
        }

        return string.Join(" · ", groups.Select(group =>
            "不适用 " + string.Join(" ", group.Ids) + "（" + group.Reason + "）"));
    }

    private static bool IsFlaky(EngineReportCase result)
    {
        string[] parts = (result.PassRate10 ?? "").Split('/');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out int pass)
            && int.TryParse(parts[1], out int total)
            && pass < total;
    }

    private static string RejectionGateLabel(string gate) => ReportValidator.RejectionGateLabel(gate);

    internal sealed class Group(string tag)
    {
        private readonly List<(string Label, string? Caveat, string? Content, List<string> Lines, List<(int Start, int Count, string? Family)> Blocks)> sections = [];

        public string Tag => tag;

        public IEnumerable<(string Label, IReadOnlyList<string> Lines)> Present =>
            sections.Where(section => section.Lines.Count > 0).Select(section => (section.Label, (IReadOnlyList<string>)section.Lines));

        public List<string> Section(string label, string? caveat = null, IEnumerable<string>? content = null)
        {
            int at = sections.FindIndex(section => section.Label == label);
            if (at >= 0) return sections[at].Lines;
            var lines = new List<string>();
            sections.Add((label, caveat, content == null ? null : string.Join("\n", content), lines, []));
            return lines;
        }

        public void AddBlock(string label, IReadOnlyList<string> lines, string? family = null)
        {
            if (lines.Count == 0) return;
            List<string> into = Section(label);
            sections[sections.FindIndex(section => section.Label == label)].Blocks.Add((into.Count, lines.Count, family));
            into.AddRange(lines);
        }

        public List<string> AddNodes(string label, IReadOnlyList<ScreenNode> nodes, string prefix)
        {
            List<string> logOnly = [];
            foreach (MergedNode node in ScreenTree.Merge(nodes))
            {
                (List<string> lines, List<string> log) = ScreenTree.Render([node], prefix, "  ");
                AddBlock(label, lines, node.Node.Family);
                logOnly.AddRange(log);
            }

            return logOnly;
        }

        public IEnumerable<(string Label, IReadOnlyList<(IReadOnlyList<string> Lines, string? Family)> Blocks)> PresentBlocks =>
            sections.Where(section => section.Lines.Count > 0).Select(section => (section.Label, BlocksOf(section.Lines, section.Blocks)));

        private static IReadOnlyList<(IReadOnlyList<string> Lines, string? Family)> BlocksOf(List<string> lines,
            List<(int Start, int Count, string? Family)> blocks)
        {
            List<(IReadOnlyList<string>, string?)> result = [];
            int next = 0;
            for (int i = 0; i < lines.Count;)
            {
                if (next < blocks.Count && blocks[next].Start == i)
                {
                    result.Add((lines.GetRange(i, blocks[next].Count), blocks[next].Family));
                    i += blocks[next++].Count;
                    continue;
                }

                result.Add(([lines[i++]], null));
            }

            return result;
        }

        public IEnumerable<Caveat> Caveats() =>
            sections.Where(section => section.Lines.Count > 0).Select(section => new Caveat(
                section.Caveat ?? section.Label, section.Label + "\n" + (section.Content ?? string.Join("\n", section.Lines))));

        public void EmitTo(List<ReportLine> output)
        {
            var present = sections.Where(section => section.Lines.Count > 0).ToList();
            if (present.Count == 0) return;
            output.Add(ReportLine.Both(tag + string.Join(" · ", present.Select(section => section.Label))));
            foreach (string line in present.SelectMany(section => section.Lines))
                output.Add(new ReportLine("  " + line, tag + line.TrimStart()));
        }
    }
}

public readonly record struct ReportLine(string? Screen, string Log)
{
    public static ReportLine Both(string text) => new(text, text);
    public static ReportLine LogOnly(string text) => new(null, text);
}

public readonly record struct DiagnosticCounts(
    int Screenshots, int Degraded, int Flaky, int Residue, int Excluded,
    int ExpectedLog, int ForeignStartup, int UnattributedStartup, int ClaimedStartup, int ForeignRuntime, int Diagnostic, int Warning,
    int LogAckHit);
