namespace RimRunnerCli;

internal static class ShardGroupView
{
    internal sealed record Slice(string Tag, int ExitCode, ReportValidationResult? Validation);

    internal sealed record FailedSlice(string Tag, string Reason, int ExitCode = ExitCodes.Incomplete);

    internal sealed record Facts(
        int Cells, int Done, int Unclaimed, int ClaimedNotDone, int? Planned,
        IReadOnlyList<string> Skipped, IReadOnlyList<FailedSlice> Failed,
        IReadOnlyList<string> LedgerFaults, string? ModList, TimeSpan? Wall);

    internal static Facts FactsOf(ShardGroupManifest manifest)
    {
        List<ShardGroupManifest.ClaimEntry> claims = manifest.Claims
            ?? throw new InvalidOperationException("组清单无认领记录：组未走到收尾");
        return new Facts(
            Cells: manifest.QueueCells,
            Done: claims.Count(claim => claim.DoneAt != null),
            Unclaimed: manifest.Unclaimed ?? 0,
            ClaimedNotDone: claims.Count(claim => claim.DoneAt == null),
            Planned: manifest.Planned,
            Skipped: [.. manifest.Undispatched.Where(entry => !entry.Failed && entry.Unplanned != true).Select(entry => entry.Reason)],
            Failed:
            [
                .. manifest.Undispatched.Where(entry => entry.Failed).Select(entry => new FailedSlice(
                    TagOf(entry.Tag, entry.Number),
                    entry.Reason,
                    entry.ExitCode is int code && code != 0 ? code : ExitCodes.Incomplete)),
            ],
            LedgerFaults: manifest.LedgerFaults ?? [],
            ModList: manifest.ModList,
            Wall: manifest.Seconds is double seconds ? TimeSpan.FromSeconds(seconds) : null);
    }

    internal static List<Slice> SlicesOf(ShardGroupManifest manifest,
        Func<ShardGroupManifest.ShardEntry, ReportValidationResult?> validation) =>
    [
        .. manifest.Shards.Select(shard => new Slice(
            TagOf(shard.Tag, shard.Number), shard.ExitCode ?? ExitCodes.Incomplete, validation(shard)))
    ];

    private static string TagOf(string? stored, int number) => stored ?? ShardChildSpec.Tag(number);

    private static bool IsRejected(Slice slice) => slice.Validation?.Report is EngineReport { Rejections.Count: > 0 };

    private sealed record Pending(string Text, int Grade, string? CaseId = null, bool Unreadable = false);

    private sealed record Verdict(List<Pending> Items, int ExitCode, bool AllRejected,
        List<(string Tag, EngineReport Report)> Reports, List<Merged> Cases, List<EngineReportRejection> CaseRejections);

    internal static int FailedPrepareExitCode(int exitCode) =>
        exitCode == ExitCodes.Rejected ? ExitCodes.Incomplete : ExitPolicy.Worst(ExitCodes.Incomplete, exitCode);

    private static Verdict Judge(IReadOnlyList<Slice> slices, Facts facts)
    {
        bool allRejected = slices.Count > 0 && slices.All(IsRejected);
        var items = new List<Pending>();
        if (facts.Unclaimed > 0) items.Add(new($"分片组:未认领×{facts.Unclaimed}", ExitCodes.Incomplete));
        if (facts.ClaimedNotDone > 0) items.Add(new($"分片组:已认领未完成×{facts.ClaimedNotDone}", ExitCodes.Incomplete));
        items.AddRange(facts.Failed.Select(failed => new Pending(
            $"{failed.Tag}:{failed.Reason}",
            FailedPrepareExitCode(failed.ExitCode))));

        List<(string Tag, EngineReport Report)> reports = [];
        foreach (Slice slice in slices)
        {
            List<Pending> own = [.. SliceItems(slice, allRejected)];
            items.AddRange(own);
            EngineReport? report = slice.Validation?.Report;
            if (report != null) reports.Add((slice.Tag, report));
            int Effective(int code) => code != ExitCodes.Rejected ? code : allRejected ? 0 : IsRejected(slice) ? 1 : ExitCodes.Incomplete;
            int code = ExitPolicy.Worst([Effective(slice.ExitCode), Effective(slice.Validation?.ExitCode ?? 0)]);
            int named = ExitPolicy.Worst([0, .. own.Select(item => item.Grade),
                .. report == null ? [] : ReportVerdict.CaseItems(report).Select(item => item.Grade), report?.CaseRejections.Count > 0 ? 1 : 0]);
            if (ExitPolicy.Worst(code, named) != named) items.Add(new($"{slice.Tag}:退出码 {code}", code));
        }

        List<Merged> cases = MergeCases(reports);
        items.AddRange(cases.Where(merged => ReportVerdict.IsBad(merged.Case))
            .Concat(cases.Where(merged => merged.Regression && ReportVerdict.IsExecutedOnly(merged.Case)))
            .Select(merged => ReportVerdict.CaseItem(merged.Case, merged.Regression)!)
            .Select(item => new Pending(item.Text, item.Grade, item.CaseId)));
        List<EngineReportRejection> caseRejections = [.. reports.SelectMany(entry => entry.Report.CaseRejections)
            .DistinctBy(rejection => (rejection.Gate, rejection.Subject, rejection.Text))];

        int exitCode = ExitPolicy.Worst([allRejected ? ExitCodes.Rejected : 0, caseRejections.Count > 0 ? 1 : 0,
            .. items.Select(item => item.Grade)]);
        return new Verdict(items, exitCode, allRejected, reports, cases, caseRejections);
    }

    private static IEnumerable<Pending> SliceItems(Slice slice, bool allRejected)
    {
        if (slice.Validation == null)
        {
            yield return new($"{slice.Tag}:无报告", ExitCodes.Incomplete, Unreadable: true);
            yield break;
        }

        if (slice.Validation.OutputFaults.Count > 0)
            yield return new($"{slice.Tag}:{ReportVerdict.OutputFaults}", ExitCodes.Incomplete);
        if (slice.Validation.Report is not EngineReport report)
        {
            yield return new($"{slice.Tag}:报告无效", ExitCodes.Incomplete, Unreadable: true);
            yield break;
        }

        if (report.Rejections.Count > 0 && !allRejected)
            yield return new($"{slice.Tag}:REJECTED", 1);
        foreach (ReportVerdict.Item item in ReportVerdict.BatchItems(report))
            yield return new($"{slice.Tag}:{item.Text}", item.Grade);
    }

    internal static (IReadOnlyList<ReportLine> Lines, int ExitCode) Format(IReadOnlyList<Slice> slices, Facts facts, bool archived = false)
    {
        string LogPointer(string tag) => archived ? "" : "；" + LinesOf(tag);
        var lines = new List<string>();
        var logOnly = new List<string>();
        Verdict verdict = Judge(slices, facts);
        List<(string Tag, EngineReport Report)> reports = verdict.Reports;
        List<Merged> cases = verdict.Cases;

        lines.Add(FactsLine(facts, slices.Count));
        foreach (FailedSlice failed in facts.Failed)
            lines.Add($"[FAIL] {failed.Tag} {failed.Reason}，未发片{LogPointer(failed.Tag)}");

        List<EngineReportRejection> rejections = [];
        List<(string Tag, string Text)> rejectionLines = [];
        foreach (Slice slice in slices)
        {
            if (slice.Validation == null)
            {
                lines.Add(UnreadableLine(slice, LogPointer(slice.Tag)));
                continue;
            }

            if (slice.Validation.OutputFaults.Count > 0)
                foreach (ReportLine line in ReportPresentation.OutputFaultLines(slice.Validation.OutputFaults, prefix: slice.Tag + " "))
                    (line.Screen == null ? logOnly : lines).Add(line.Log);

            if (slice.Validation.Report is not EngineReport report)
            {
                lines.Add(UnreadableLine(slice, LogPointer(slice.Tag)));
                continue;
            }

            if (report.Rejections.Count > 0)
            {
                rejections.AddRange(report.Rejections);
                rejectionLines.Add((slice.Tag, ReportValidator.DescribeRejections(report.Rejections)));
            }

            if (report.EngineFaults.Count > 0)
                lines.Add($"[{RimRunner.RimRunnerOutcomes.EngineFault}/窗外] {slice.Tag} {report.EngineFaults.Count} 条{LogPointer(slice.Tag)}");
            if (report.BatchSentinels.Count > 0)
                lines.Add($"[{RimRunner.RimRunnerOutcomes.SentinelFault}/窗外] {slice.Tag} {report.BatchSentinels.Count} 条{LogPointer(slice.Tag)}");
        }

        foreach (var group in rejectionLines.GroupBy(entry => entry.Text, StringComparer.Ordinal))
        {
            string[] tags = [.. group.Select(entry => entry.Tag)];
            string pointer = archived ? "" : "；" + LinesOf(tags);
            string owners = slices.Count == 1 ? tags[0] : Owners(tags, slices.Count);
            lines.Add($"[拒绝] {owners} 批次拒绝：{group.Key}{pointer}");
        }

        List<(string Tag, ReportPresentation.Composition Parts)> parts =
            [.. reports.Select(entry => (entry.Tag, ReportPresentation.ComposeSlice(entry.Report)))];

        List<Merged> bad = [.. cases.Where(merged => ReportVerdict.IsBad(merged.Case))];
        ReportPresentation.AppendCases(lines, logOnly, [.. bad.Select(merged => (merged.Case, (string?)merged.Distribution))], otherLedgers: null);
        foreach (Merged merged in cases.Where(merged => merged.Regression && ReportVerdict.IsExecutedOnly(merged.Case)))
            ReportPresentation.AppendExecutedOnly(lines, merged.Case);

        lines.AddRange(facts.LedgerFaults.Select(fault => "[台账故障] " + fault));

        List<string> caveats =
        [
            .. MergeCaveats(parts, slices.Count),
            .. facts.LedgerFaults.Count > 0 ? new[] { $"{facts.LedgerFaults.Count} 处台账故障" } : [],
        ];
        string conclusion = ReportPresentation.Conclusion(new ReportPresentation.ConclusionFacts(
            verdict.ExitCode,
            Sections(cases, reports),
            caveats,
            verdict.AllRejected && verdict.ExitCode == ExitCodes.Rejected ? ReportValidator.DescribeRejections(rejections) : null,
            verdict.CaseRejections.Count > 0 ? ReportValidator.DescribeRejections(verdict.CaseRejections) : null,
            [.. verdict.Items.Select(item => item.Text)],
            " · 名单 " + (string.IsNullOrEmpty(facts.ModList) ? "无" : facts.ModList)), logOnly);

        var tail = new List<string>();
        ReportPresentation.AppendArtifacts(tail, logOnly,
            [.. parts.SelectMany(part => Owned(part.Tag, part.Parts.Screenshots))],
            [.. parts.SelectMany(part => Owned(part.Tag, part.Parts.ReadoutTexts))]);

        List<ReportLine> output = [.. lines.Select(ReportLine.Both)];
        MergeGroup(parts, part => part.Errors, slices.Count).EmitTo(output);
        ReportPresentation.Group warnings = MergeGroup(parts, part => part.Warnings, slices.Count);
        List<EngineReportFinding> noHits = verdict.Items.Any(item => item.Unreadable) ? [] : GroupNoHits(reports);
        FindingPresentation.Keyed(noHits, "[带键警告] ", warnings.Section("带键警告"), logOnly);
        logOnly.AddRange(noHits.Select(finding => $"[带键警告] [{finding.Key}] {finding.Subject}：{finding.Text}"));
        warnings.EmitTo(output);
        MergeGroup(parts, part => part.Scope, slices.Count).EmitTo(output);
        output.AddRange(logOnly.Select(ReportLine.LogOnly));
        output.AddRange(tail.Select(ReportLine.Both));
        output.Add(ReportLine.Both(conclusion));
        return (output, verdict.ExitCode);

        IEnumerable<ReportPresentation.Artifact> Owned(string tag, IEnumerable<ReportPresentation.Artifact> artifacts) =>
            slices.Count == 1 ? artifacts : artifacts.Select(artifact => artifact with { Text = $"{tag}：{artifact.Text}" });
    }

    private static string UnreadableLine(Slice slice, string pointer = "") => slice.Validation == null
        ? $"[FAIL] {slice.Tag} 无报告，退出码 {slice.ExitCode}{pointer}"
        : $"[FAIL] {slice.Tag} {slice.Validation.Message}{pointer}";

    private static ReportPresentation.Group MergeGroup(List<(string Tag, ReportPresentation.Composition Parts)> parts,
        Func<ReportPresentation.Composition, ReportPresentation.Group> pick, int sliceCount)
    {
        var merged = new ReportPresentation.Group(parts.Count > 0 ? pick(parts[0].Parts).Tag : "");
        var blocks = parts.SelectMany(part => pick(part.Parts).PresentBlocks
            .SelectMany(section => section.Blocks.Select(block => (part.Tag, section.Label, block.Lines, block.Family))));
        foreach (var bySection in blocks.GroupBy(block => block.Label, StringComparer.Ordinal))
        {
            List<string> into = merged.Section(bySection.Key);
            foreach (var family in bySection.GroupBy(block => block.Family ?? "\u0000" + string.Join("\n", block.Lines), StringComparer.Ordinal))
            {
                foreach (var head in family.GroupBy(block => block.Lines[0], StringComparer.Ordinal))
                    into.Add(Owned(head.Select(block => block.Tag), head.Key));

                string[] familyTags = [.. family.Select(block => block.Tag).Distinct(StringComparer.Ordinal)];
                foreach (var child in family.SelectMany(block => block.Lines.Skip(1).Select(line => (block.Tag, Line: line)))
                             .GroupBy(entry => entry.Line, StringComparer.Ordinal))
                {
                    string[] tags = [.. child.Select(entry => entry.Tag).Distinct(StringComparer.Ordinal)];
                    if (tags.Length == familyTags.Length)
                    {
                        into.Add(child.Key);
                        continue;
                    }

                    int indent = child.Key.Length - child.Key.TrimStart(' ').Length;
                    into.Add(child.Key[..indent] + Owned(tags, child.Key[indent..]));
                }
            }
        }

        return merged;

        string Owned(IEnumerable<string> tags, string line)
        {
            string owners = Owners([.. tags.Distinct(StringComparer.Ordinal)], sliceCount);
            return (owners.Length == 0 ? "" : owners + "：") + line;
        }
    }

    private static string Owners(string[] tags, int sliceCount) =>
        sliceCount == 1 ? "" : tags.Length == sliceCount ? "各片" : string.Join("、", tags);

    internal static (IReadOnlyList<ReportLine> Lines, bool Attention) FormatCase(
        IReadOnlyList<Slice> slices, Facts facts, string caseId)
    {
        Verdict verdict = Judge(slices, facts);
        List<(string Tag, EngineReport Report)> reports = verdict.Reports;
        Slice[] unreadable = [.. slices.Where(slice => slice.Validation?.Report == null)];
        string[] others = [.. verdict.Items.Where(item => item.CaseId == null && !item.Unreadable).Select(item => item.Text)];
        Merged? merged = verdict.Cases.FirstOrDefault(entry => entry.Case.Id == caseId);
        List<EngineReportRejection> caseRejections = [.. verdict.CaseRejections.Where(rejection => rejection.Subject == caseId)];

        List<string> lines = [FactsLine(facts, slices.Count)];
        List<string> logOnly = [];
        if (verdict.AllRejected)
        {
            lines.Add($"[拒绝] 各片 批次拒绝：{ReportValidator.DescribeRejections([.. reports.SelectMany(entry => entry.Report.Rejections)])}");
        }
        else if (caseRejections.Count > 0)
        {
            ReportPresentation.AppendRejections(lines, "[用例拒绝/", caseRejections);
        }
        else if (merged == null)
        {
            if (reports.Count > 0)
                lines.Add($"[FAIL] 已读的 {reports.Count} 片报告里没有用例 {caseId}");
        }
        else if (ReportVerdict.IsExecutedOnly(merged.Case))
        {
            ReportPresentation.AppendExecutedOnly(lines, merged.Case);
            if (merged.Distribution != null)
                lines.Add("  " + merged.Distribution);
        }
        else
        {
            ReportPresentation.AppendCase(lines, logOnly, merged.Case, otherLedgers: null, merged.Distribution);
        }

        if (unreadable.Length > 0)
        {
            lines.AddRange(unreadable.Select(slice => UnreadableLine(slice)));
            lines.Add($"[FAIL] 用例 {caseId} 在 {string.Join("、", unreadable.Select(slice => slice.Tag))} 上的结果未知");
        }
        if (others.Length > 0)
            lines.Add($"[FAIL] 分片组另有待处理：{string.Join("、", others)}");

        bool attention = merged == null || verdict.AllRejected || caseRejections.Count > 0 || unreadable.Length > 0 || others.Length > 0
            || ReportVerdict.IsBad(merged.Case) || ReportVerdict.IsExecutedOnly(merged.Case);
        return ([.. lines.Select(ReportLine.Both), .. logOnly.Select(ReportLine.LogOnly)], attention);
    }

    private static IEnumerable<string> MergeCaveats(List<(string Tag, ReportPresentation.Composition Parts)> parts, int sliceCount)
    {
        var merged = parts
            .SelectMany(entry => entry.Parts.Caveats.Select(caveat => (entry.Tag, Caveat: caveat)))
            .GroupBy(item => item.Caveat);
        foreach (var group in merged)
        {
            string[] tags = [.. group.Select(item => item.Tag)];
            yield return sliceCount == 1 ? group.Key.Text
                : tags.Length == sliceCount ? $"{group.Key.Text}（各片相同）"
                : tags.Length == 1 ? $"{group.Key.Text}（{tags[0]}）"
                : $"{group.Key.Text}（{string.Join("、", tags)} 相同）";
        }
    }

    private sealed record Merged(EngineReportCase Case, bool Regression, string? Distribution);

    private static List<Merged> MergeCases(List<(string Tag, EngineReport Report)> reports)
    {
        var byId = new Dictionary<string, List<(string Tag, EngineReportCase Case, bool Regression)>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach ((string tag, EngineReport report) in reports)
        {
            foreach ((bool regression, List<EngineReportCase>? section) in new[] { (true, report.Regressions), (false, report.Readouts) })
            {
                foreach (EngineReportCase result in section ?? [])
                {
                    if (!byId.TryGetValue(result.Id, out var entries))
                    {
                        entries = [];
                        byId.Add(result.Id, entries);
                        order.Add(result.Id);
                    }

                    entries.Add((tag, result, regression));
                }
            }
        }

        return
        [
            .. order.Select(id =>
            {
                List<(string Tag, EngineReportCase Case, bool Regression)> entries = byId[id];
                EngineReportCase worst = entries.MaxBy(entry => RimRunner.RimRunnerOutcomes.Severity(entry.Case.Status)).Case;
                EngineReportCase merged = worst with
                {
                    EngineFaults = [.. entries.SelectMany(entry => entry.Case.EngineFaults)],
                    SentinelFaults = [.. entries.SelectMany(entry => entry.Case.SentinelFaults)],
                    FailureFingerprints = [.. entries.SelectMany(entry => entry.Case.FailureFingerprints)],
                    Asserts = [.. entries.SelectMany(entry => entry.Case.Asserts)],
                    Diagnostics = [.. entries.SelectMany(entry => entry.Case.Diagnostics)],
                };
                return new Merged(merged, entries[0].Regression, Distribution(entries.Select(entry => (entry.Tag, entry.Case.Status))));
            }).OrderByDescending(merged => merged.Regression)
        ];
    }

    private static List<EngineReportFinding> GroupNoHits(List<(string Tag, EngineReport Report)> reports)
    {
        var cells = new List<(string CaseId, string Identity, int? Misses, bool Unknown, int Tolerance, string Label, string Reason)>();
        foreach ((_, EngineReport report) in reports)
        {
            foreach (EngineReportAck ack in report.Acks.Where(ack => ack.Subject != null))
                cells.Add((ack.Subject!, "key\n" + ack.Target + "\n" + ack.Origin?.ToLowerInvariant(), ack.Misses,
                    ack.Misses == null && ack.Unreachable == null, ack.Tolerance,
                    RimRunner.RimRunnerAckMissText.KeyLabel(ack.Target, ack.Origin), ack.Reason));
            foreach (EngineReportCase result in (report.Regressions ?? []).Concat(report.Readouts ?? []))
                foreach (EngineReportLogAck ack in result.LogAcks)
                    cells.Add((result.Id, "log\n" + ack.Level + "\n" + ack.Pattern + "\n" + string.Join("\n", ack.StackAnchors),
                        ack.Misses, ack.Misses == null && ack.Unreachable == null, ack.Tolerance,
                        RimRunner.RimRunnerAckMissText.LogLabel(ack.Level, ack.Pattern, ack.StackAnchors), ack.Reason));
        }

        return
        [
            .. cells.GroupBy(cell => (cell.CaseId, cell.Identity))
                .Where(group => !group.Any(cell => cell.Unknown) && group.Any(cell => cell.Misses != null))
                .Select(group => (Cell: group.First(), Misses: group.Min(cell => cell.Misses)!.Value))
                .Where(item => item.Misses > item.Cell.Tolerance)
                .Select(item => new EngineReportFinding
                {
                    Subject = item.Cell.CaseId, Key = RimRunner.RimRunnerWarningKey.AckNoHit,
                    Text = RimRunner.RimRunnerAckMissText.NoHit(item.Cell.CaseId, item.Cell.Label, item.Misses, item.Cell.Tolerance,
                        item.Cell.Reason)
                })
        ];
    }

    private static string? Distribution(IEnumerable<(string Tag, string Status)> statuses)
    {
        var groups = statuses.GroupBy(entry => entry.Status, StringComparer.Ordinal)
            .OrderByDescending(group => RimRunner.RimRunnerOutcomes.Severity(group.Key))
            .ToList();
        return groups.Count < 2
            ? null
            : "各片状态：" + string.Join(" · ", groups.Select(group => $"{group.Key}（{string.Join("、", group.Select(entry => entry.Tag))}）"));
    }

    private static List<(string Name, int Count)> Sections(List<Merged> cases, List<(string Tag, EngineReport Report)> reports)
    {
        List<(string Name, int Count)> sections = [];
        if (reports.Any(entry => entry.Report.Regressions != null))
            sections.Add(("regressions", cases.Count(merged => merged.Regression)));
        if (reports.Any(entry => entry.Report.Readouts != null))
            sections.Add(("readouts", cases.Count(merged => !merged.Regression)));
        return sections;
    }

    private static string FactsLine(Facts facts, int dispatched)
    {
        string cells = $"格 {facts.Done}/{facts.Cells} 完成"
            + (facts.Unclaimed > 0 ? $" · 未认领 {facts.Unclaimed}" : "")
            + (facts.ClaimedNotDone > 0 ? $" · 已认领未完成 {facts.ClaimedNotDone}" : "");
        string skipped = facts.Skipped.Count == 0
            ? ""
            : "（未发：" + string.Join("、", facts.Skipped.GroupBy(reason => reason, StringComparer.Ordinal)
                .Select(group => $"{group.Key}×{group.Count()}")) + "）";
        return $"[分片组] {cells} · 发片 {dispatched}" + (facts.Planned is int planned ? $" / 计划 {planned}" : "") + skipped
            + (facts.Wall is TimeSpan wall ? $" · 用时 {ReportPresentation.Elapsed((long)wall.TotalMilliseconds)}" : "");
    }

    internal static string LinesOf(params string[] tags) =>
        $"明细见 RimRunner 日志 {string.Join("、", tags.Select(tag => $"[{tag}]"))} 行";
}
