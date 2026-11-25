using System.Text;
using System.Text.Json;

namespace RimRunner;

public static class RimRunnerReportWriter
{
    internal const string LatestFileName = "latest.json";

    public static void Write(RimRunnerReport report, RimRunnerCaseSelector selector)
    {
        if (selector == null)
        {
            throw new ArgumentNullException(nameof(selector));
        }

        WriteCore(report, !RimRunnerSaveProbe.Armed, selector);
    }

    internal static string TimestampNow()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    internal static void WriteTimestampedOnly(RimRunnerReport report)
    {
        WriteCore(report, false, null);
    }

    private static void WriteCore(
        RimRunnerReport report, bool writeLatest, RimRunnerCaseSelector? selector)
    {
        report.timestamp = TimestampNow();
        report.modList = RimRunnerPassRate.ResolveModListName();
        report.textureTier = RimRunnerTextureTier.Current;
        report.ledgerFile = RimRunnerPassRate.LedgerFileName();
        report.seenLogsFile = RimRunnerSeenLogLedger.FileName();
        report.borrowed = RimRunnerBorrow.Snapshot();
        report.inputs = RimRunnerInput.Snapshot();
        report.inputsUnread = RimRunnerInput.UnreadSpecified();
        report.assemblyMvids = RimRunnerDefScope.OurAssemblyMvids();
        report.productCapabilities = RimRunnerProduct.TryGetCapabilities()?.Names.ToList();
        report.customSave = RimRunnerPassRate.ResolveCustomSave();
        report.contentFingerprint =
            GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.ContentFingerprint, out string fingerprint)
            && !string.IsNullOrEmpty(fingerprint)
                ? fingerprint
                : null;
        SealSamples(report);
        string json = RimRunnerJson.Write(writer => AppendReport(writer, report, selector), true);

        bool single = SingleCaseIdOrNull(selector) != null;
        RimRunnerOutput.Report(report.runId ?? "",
            RimRunnerOutputPath.Join(ArchiveDirFor(selector), ArchiveFileName(report, selector)),
            writeLatest ? RimRunnerOutputPath.Join(RimRunnerPaths.ReportsPath, LatestFileName) : null,
            single ? (SingleKeepRecent, SingleKeepFailed) : (BatchKeepRecent, BatchKeepFailed),
            Encoding.UTF8.GetBytes(json));
        RimRunnerEngineLog.Info("报告已交出：pass=" + report.pass + " noAssertions=" + report.noAssertions
                                + " fail=" + report.fail + " unclaimedFault=" + report.unclaimedFault
                                + " sentinelFault=" + report.sentinelFault
                                + " engineFault=" + report.engineFault
                                + (report.engineFaults.Count > 0 ? " engineFaults=" + report.engineFaults.Count : "")
                                + (report.rejections.Count > 0 ? " rejections=" + report.rejections.Count : "")
                                + (report.caseRejections.Count > 0 ? " caseRejections=" + report.caseRejections.Count : ""));
    }

    private static void AppendReport(
        Utf8JsonWriter writer, RimRunnerReport report, RimRunnerCaseSelector? selector)
    {
        writer.WriteStartObject();
        writer.WriteString("timestamp", report.timestamp);
        writer.WriteNumber(RimRunnerWireKeys.Report.Schema, RimRunnerWireKeys.Report.SchemaVersion);
        writer.WriteString("command", report.command);
        writer.WriteString("modList", report.modList);
        writer.WriteString("textureTier", report.textureTier);
        writer.WriteString("ledgerFile", report.ledgerFile);
        writer.WriteString("seenLogsFile", report.seenLogsFile);
        AppendBorrowed(writer, RimRunnerWireKeys.Report.Borrowed, report.borrowed);
        AppendInputs(writer, report.inputs);
        AppendStringArray(writer, RimRunnerWireKeys.Report.InputsUnread, report.inputsUnread);
        writer.WriteString("runId", report.runId);
        writer.WriteNumber("pass", report.pass);
        writer.WriteNumber("noAssertions", report.noAssertions);
        writer.WriteNumber("fail", report.fail);
        writer.WriteNumber("unclaimedFault", report.unclaimedFault);
        writer.WriteNumber("sentinelFault", report.sentinelFault);
        writer.WriteNumber("engineFault", report.engineFault);
        writer.WriteNumber("done", report.done);
        writer.WriteNumber("incomplete", report.incomplete);
        writer.WriteNumber("durationMs", report.durationMs);
        writer.WriteNumber("reloadMs", report.reloadMs);
        writer.WriteNumber("reloadCount", report.reloadCount);
        AppendStringArray(writer, RimRunnerWireKeys.Report.AmbientWindows, report.ambientWindows);
        AppendStringArray(writer, RimRunnerWireKeys.Report.AmbientWindowsFresh, report.ambientWindowsFresh);
        writer.WriteString(RimRunnerWireKeys.Report.CustomSave, report.customSave);
        AppendStringArray(writer, RimRunnerWireKeys.Report.CustomSaveNotes, report.customSaveNotes);
        writer.WriteNumber("danglingProbeArms", report.danglingProbeArms);
        writer.WriteNumber("faultReloads", report.faultReloads);
        writer.WriteNumber("mapReloads", report.mapReloads);
        writer.WriteNumber("breakerObservations", report.breakerObservations);
        writer.WriteNumber("breakerTrips", report.breakerTrips);
        writer.WriteNumber("breakerPumpThreshold", report.breakerPumpThreshold);
        writer.WriteNumber("breakerErrorThreshold", report.breakerErrorThreshold);
        AppendStringArray(writer, "excludedCases", report.excludedCases);
        if (report.envChecksClean == null)
        {
            writer.WriteNull(RimRunnerWireKeys.Report.EnvChecksClean);
        }
        else
        {
            writer.WriteBoolean(RimRunnerWireKeys.Report.EnvChecksClean, report.envChecksClean.Value);
        }

        AppendStringMap(writer, RimRunnerWireKeys.Report.InapplicableCases, report.inapplicableCases);
        AppendStringArray(writer, RimRunnerWireKeys.Report.GroupSkippedCases, report.groupSkippedCases);
        AppendStringMap(writer, RimRunnerWireKeys.Report.AbsentCases, report.absentCases);
        AppendStringArray(writer, "checkpointBatches", report.checkpointBatches);
        AppendStringArray(writer, "checkpointRetries", report.checkpointRetries);
        AppendStringArray(writer, "footprints", report.footprints);
        AppendStringArray(writer, "mainMap", report.mainMap);
        AppendStringArray(writer, "forcedMainMap", report.forcedMainMap);
        if (report.componentFirstFills == null)
        {
            writer.WriteNull(RimRunnerWireKeys.Report.ComponentFirstFills);
        }
        else
        {
            AppendStringMap(writer, RimRunnerWireKeys.Report.ComponentFirstFills, report.componentFirstFills);
        }
        writer.WriteString("experiments", report.experiments);
        writer.WriteString("compatOff", report.compatOff);
        AppendStringArray(writer, "foreignStartupErrors", report.foreignStartupErrors);
        AppendStringArray(writer, "claimedStartupErrors", report.claimedStartupErrors);
        AppendStringArray(writer, RimRunnerWireKeys.Report.UnattributedStartupErrors, report.unattributedStartupErrors);
        AppendRejections(writer, RimRunnerWireKeys.Report.Rejections, report.rejections);
        AppendRejections(writer, RimRunnerWireKeys.Report.CaseRejections, report.caseRejections);
        AppendRejections(writer, RimRunnerWireKeys.Report.OutOfBatchFaults, report.outOfBatchFaults);
        AppendWarnings(writer, RimRunnerWireKeys.Report.Warnings, report.warnings);
        AppendWarnings(writer, RimRunnerWireKeys.Report.WarningsAcked, report.warningsAcked);
        AppendStringArray(writer, RimRunnerWireKeys.Report.LedgerFaults, report.ledgerFaults);
        AppendEngineFaults(writer, "engineFaults", report.engineFaults);
        AppendFindings(writer, "batchSentinels", report.batchSentinels);
        AppendFindings(writer, "outputFaults", report.outputFaults);
        AppendStringArray(writer, "reloadErrors", report.reloadErrors);
        AppendFindings(writer, "gapErrors", report.gapErrors);
        writer.WriteNumber("gapErrorCount", report.gapErrorCount);
        AppendFindings(writer, "logCapHits", report.logCapHits);
        writer.WriteNumber("logCountPeak", report.logCountPeak);
        AppendAcks(writer, "acks", report.acks);
        writer.WriteBoolean("contractGatesReady", report.contractGatesReady);
        writer.WriteNumber("contractScanMs", report.contractScanMs);
        AppendLongMap(writer, RimRunnerWireKeys.Report.MemoryStart, report.memoryStart);
        AppendLongMap(writer, RimRunnerWireKeys.Report.MemoryEnd, report.memoryEnd);
        writer.WriteString(RimRunnerWireKeys.Report.ContentFingerprint, report.contentFingerprint);
        AppendStringMap(writer, RimRunnerWireKeys.Report.AssemblyMvids, report.assemblyMvids);
        if (report.productCapabilities == null)
        {
            writer.WriteNull(RimRunnerWireKeys.Report.ProductCapabilities);
        }
        else
        {
            AppendStringArray(writer, RimRunnerWireKeys.Report.ProductCapabilities, report.productCapabilities);
        }

        AppendShard(writer, report.shard);
        AppendReportSections(writer, report, selector);
        writer.WriteEndObject();
    }

    private static void SealSamples(RimRunnerReport report)
    {
        RimRunnerSampleWriter.Seal(report, (entry, exception) =>
        {
            RimRunnerFindingEntry fault = RimRunnerFindingEntry.Of(null, entry.id, "读数库封批失败：" + exception.Message);
            RimRunnerEngineLog.Warn(fault.Line + "\n" + exception);
            report.outputFaults.Add(fault);
        });
    }

    private const int BatchKeepRecent = RimRunnerArchiveName.BatchKeepRecent;
    private const int BatchKeepFailed = RimRunnerArchiveName.BatchKeepFailed;
    private const int SingleKeepRecent = RimRunnerArchiveName.SingleKeepRecent;
    private const int SingleKeepFailed = RimRunnerArchiveName.SingleKeepFailed;

    private static bool IsAttention(RimRunnerReport report)
    {
        return RimRunnerVerdict.IsAttention(
            report.regressions.Select(entry => entry.status).ToArray(),
            report.readouts
                .Select(entry => entry.status).ToArray(),
            report.engineFaults.Count > 0 || report.batchSentinels.Count > 0);
    }

    private static string ArchiveDirFor(RimRunnerCaseSelector? selector)
    {
        string? singleId = SingleCaseIdOrNull(selector);
        return singleId != null
            ? RimRunnerOutputPath.Join(RimRunnerPaths.ReportsPath, RimRunnerDataLayout.SingleSubDirName, singleId)
            : RimRunnerPaths.ReportsPath;
    }

    private static string? SingleCaseIdOrNull(RimRunnerCaseSelector? selector)
    {
        return selector != null && selector.CaseIds.Length == 1 ? selector.CaseIds[0] : null;
    }

    private static string ArchiveFileName(
        RimRunnerReport report, RimRunnerCaseSelector? selector)
    {
        string mark = report.rejections.Count > 0
            ? RimRunnerArchiveName.RejectedMark
            : report.caseRejections.Count > 0 || IsAttention(report)
                ? RimRunnerArchiveName.FailedMark
                : RimRunnerArchiveName.PassedMark;
        return RimRunnerArchiveName.Format(
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"),
            mark,
            report.fail, report.unclaimedFault, report.sentinelFault, report.engineFault + report.engineFaults.Count,
            report.noAssertions, report.incomplete,
            selector == null ? "err" : selector.ArchiveTag(),
            report.runId);
    }

    private static void AppendShard(Utf8JsonWriter writer, RimRunnerShardInfo? shard)
    {
        if (shard == null)
        {
            writer.WriteNull(RimRunnerWireKeys.Report.Shard);
            return;
        }

        writer.WriteStartObject(RimRunnerWireKeys.Report.Shard);
        writer.WriteString(RimRunnerWireKeys.Report.ShardGroup, shard.group);
        writer.WriteNumber(RimRunnerWireKeys.Report.ShardIndex, shard.index);
        writer.WriteNumber(RimRunnerWireKeys.Report.ShardCount, shard.count);
        writer.WriteString(RimRunnerWireKeys.Report.ShardCase, shard.caseId);
        writer.WriteNumber(RimRunnerWireKeys.Report.ShardCells, shard.cells);
        writer.WriteNumber(RimRunnerWireKeys.Report.ShardWeight, shard.weight);
        writer.WriteString(RimRunnerWireKeys.Report.ShardFingerprint, shard.fingerprint);
        writer.WriteEndObject();
    }

    private static void AppendReportSections(
        Utf8JsonWriter writer, RimRunnerReport report, RimRunnerCaseSelector? selector)
    {
        MaybeAppendSection(writer, "regressions", report.regressions, selector);
        if (report.readouts.Count > 0)
        {
            AppendResults(writer, "readouts", report.readouts);
        }
    }

    private static void MaybeAppendSection(
        Utf8JsonWriter writer, string name, List<RimRunnerResultEntry> entries,
        RimRunnerCaseSelector? selector)
    {
        bool selected = selector is { IsNamed: false };
        if (entries.Count == 0 && !selected)
        {
            return;
        }

        AppendResults(writer, name, entries);
    }

    private static void AppendResults(
        Utf8JsonWriter writer, string name, List<RimRunnerResultEntry> results)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerResultEntry result in results)
        {
            writer.WriteStartObject();
            writer.WriteString("id", result.id);
            writer.WriteString("status", result.status);
            writer.WriteNumber("durationMs", result.durationMs);
            writer.WriteNumber("checkpointMs", result.checkpointMs);
            writer.WriteString("frameSpeed", result.frameSpeed);
            writer.WriteNumber("ticksAdvanced", result.ticksAdvanced);
            if (result.otherBusyCores is { } otherBusyCores)
            {
                writer.WriteNumber("otherBusyCores", otherBusyCores);
            }
            else
            {
                writer.WriteNull("otherBusyCores");
            }

            writer.WriteNumber("otherBusyWindowMs", result.otherBusyWindowMs);
            writer.WriteString("passRate10", result.passRate10);
            writer.WriteString("outcomeTail30", result.outcomeTail30);
            writer.WriteBoolean("saveChangedSincePass", result.saveChangedSincePass);
            writer.WriteBoolean("onMainMap", result.onMainMap);
            writer.WriteBoolean("onParallelSite", result.onParallelSite);
            writer.WriteString("errorSummary", result.errorSummary);
            AppendAsserts(writer, "asserts", result.asserts);
            AppendDiagnostics(writer, "diagnostics", result.diagnostics);
            AppendArtifacts(writer, "artifacts", result.artifacts);
            AppendScreenshots(writer, "screenshots", result.screenshots);
            AppendReadoutTexts(writer, "readoutTexts", result.readoutTexts);
            writer.WriteString("exception", result.exception);
            AppendErrors(writer, "newErrors", result.newErrors);
            AppendFindings(writer, "sentinelFaults", result.sentinelFaults);
            AppendStringArray(writer, "engineFaults", result.engineFaults);
            AppendWarnings(writer, RimRunnerWireKeys.Report.Warnings, result.warnings);
            AppendWarnings(writer, RimRunnerWireKeys.Report.WarningsAcked, result.warningsAcked);
            AppendStringArray(writer, "expectedLogs", result.expectedLogs);
            AppendStringArray(writer, "expectedLogsFresh", result.expectedLogsFresh);
            AppendLogAcks(writer, "logAcks", result.logAcks);
            AppendStringArray(writer, "foreignErrors", result.foreignErrors);
            AppendStringArray(writer, "newWarnings", result.newWarnings);
            AppendStringArray(writer, "cleanupErrors", result.cleanupErrors);
            AppendStringArray(writer, "degradedNotes", result.degradedNotes);
            AppendStringArray(writer, "degradedNotesFresh", result.degradedNotesFresh);
            AppendFindings(writer, "failureFingerprints", result.failureFingerprints);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendDiagnostics(
        Utf8JsonWriter writer, string name, List<RimRunnerDiagnosticEntry> diagnostics)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerDiagnosticEntry entry in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", entry.kind);
            writer.WriteString("label", entry.label);
            writer.WriteString("value", entry.value);
            writer.WriteNumber("gameTick", entry.gameTick);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendScreenshots(
        Utf8JsonWriter writer, string name, List<RimRunnerScreenshotEntry> screenshots)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerScreenshotEntry screenshot in screenshots)
        {
            writer.WriteStartObject();
            writer.WriteString("caption", screenshot.caption);
            writer.WriteString("path", screenshot.path);
            writer.WriteString("artifactId", screenshot.artifactId);
            writer.WriteString("error", screenshot.error);
            writer.WriteString("note", screenshot.note);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendReadoutTexts(
        Utf8JsonWriter writer, string name, List<RimRunnerReadoutTextEntry> texts)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerReadoutTextEntry text in texts)
        {
            writer.WriteStartObject();
            writer.WriteString("title", text.title);
            writer.WriteString("path", text.path);
            writer.WriteNumber("lines", text.lines);
            writer.WriteNumber("bytes", text.bytes);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendArtifacts(
        Utf8JsonWriter writer, string name, List<RimRunnerArtifactEntry> artifacts)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerArtifactEntry artifact in artifacts)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", artifact.kind);
            writer.WriteString("artifactId", artifact.artifactId);
            writer.WriteString("state", artifact.state);
            writer.WriteString("label", artifact.label);
            writer.WriteString("fileName", artifact.fileName);
            writer.WriteString("path", artifact.path);
            writer.WriteString("error", artifact.error);
            writer.WriteString("runId", artifact.runId);
            writer.WriteString("command", artifact.command);
            writer.WriteString("caseId", artifact.caseId);
            writer.WriteString("targetId", artifact.targetId);
            writer.WriteNumber("gameTick", artifact.gameTick);
            writer.WriteNumber("frame", artifact.frame);
            writer.WriteNumber("completedFrame", artifact.completedFrame);
            writer.WriteNumber("cleanupFrame", artifact.cleanupFrame);
            writer.WriteNumber("width", artifact.width);
            writer.WriteNumber("height", artifact.height);
            writer.WriteNumber("screenWidth", artifact.screenWidth);
            writer.WriteNumber("screenHeight", artifact.screenHeight);
            writer.WriteNumber("uiScale", artifact.uiScale);
            writer.WriteNumber("mapId", artifact.mapId);
            writer.WriteString("cameraPosition", artifact.cameraPosition);
            writer.WriteString("cameraZoom", artifact.cameraZoom);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendAsserts(
        Utf8JsonWriter writer, string name, IReadOnlyList<RimRunnerAssertEntry> asserts)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerAssertEntry assertEntry in asserts)
        {
            writer.WriteStartObject();
            writer.WriteNumber("ordinal", assertEntry.ordinal);
            writer.WriteString("label", assertEntry.label);
            writer.WriteString("expected", assertEntry.expected);
            writer.WriteString("actual", assertEntry.actual);
            writer.WriteBoolean("pass", assertEntry.pass);
            writer.WriteString("source", assertEntry.source);
            AppendStringArray(writer, "offending", assertEntry.offending);
            writer.WriteString("explanation", assertEntry.explanation);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendLongMap(
        Utf8JsonWriter writer, string name, SortedDictionary<string, long>? values)
    {
        writer.WriteStartObject(name);
        if (values != null)
        {
            foreach (KeyValuePair<string, long> pair in values)
            {
                writer.WriteNumber(pair.Key, pair.Value);
            }
        }

        writer.WriteEndObject();
    }

    private static void AppendStringMap(
        Utf8JsonWriter writer, string name, SortedDictionary<string, string> values)
    {
        writer.WriteStartObject(name);
        if (values != null)
        {
            foreach (KeyValuePair<string, string> pair in values)
            {
                writer.WriteString(pair.Key, pair.Value);
            }
        }

        writer.WriteEndObject();
    }

    private static void AppendWarnings(
        Utf8JsonWriter writer, string name, List<RimRunnerWarningEntry> values)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerWarningEntry warning in values)
        {
            writer.WriteStartObject();
            writer.WriteString("subject", warning.subject);
            writer.WriteString("key", warning.key);
            writer.WriteString("text", warning.text);
            writer.WriteString("reason", warning.reason);
            if (warning.fresh is bool fresh)
            {
                writer.WriteBoolean("fresh", fresh);
            }
            else
            {
                writer.WriteNull("fresh");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendErrors(Utf8JsonWriter writer, string name, List<RimRunnerErrorEntry> values)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerErrorEntry error in values)
        {
            writer.WriteStartObject();
            writer.WriteString("text", error.text);
            writer.WriteString("stack", error.stack);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendFindings(Utf8JsonWriter writer, string name, List<RimRunnerFindingEntry> values)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerFindingEntry finding in values)
        {
            writer.WriteStartObject();
            writer.WriteString("key", finding.key);
            writer.WriteString("subject", finding.subject);
            writer.WriteString("text", finding.text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendEngineFaults(
        Utf8JsonWriter writer, string name, List<RimRunnerEngineFaultEntry> values)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerEngineFaultEntry fault in values)
        {
            writer.WriteStartObject();
            writer.WriteString("at", fault.at);
            writer.WriteString("caseId", fault.caseId);
            writer.WriteString("where", fault.where);
            writer.WriteString("text", fault.text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendRejections(
        Utf8JsonWriter writer, string name, List<RimRunnerRejectionEntry> values)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerRejectionEntry rejection in values)
        {
            writer.WriteStartObject();
            writer.WriteString("gate", rejection.gate);
            writer.WriteString("subject", rejection.subject);
            writer.WriteString("summary", rejection.summary);
            writer.WriteString("text", rejection.text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendLogAcks(
        Utf8JsonWriter writer, string name, List<RimRunnerLogAckEntry> acks)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerLogAckEntry ack in acks)
        {
            writer.WriteStartObject();
            writer.WriteString("level", ack.level);
            writer.WriteString("pattern", ack.pattern);
            writer.WriteString("reason", ack.reason);
            AppendStringArray(writer, "stackAnchors", ack.stackAnchors);
            writer.WriteNumber("hits", ack.hits);
            AppendStringArray(writer, "lines", ack.lines);
            AppendStringArray(writer, "linesFresh", ack.linesFresh);
            AppendMisses(writer, ack.tolerance, ack.misses, ack.unreachable);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendAcks(Utf8JsonWriter writer, string name, List<RimRunnerAckEntry> acks)
    {
        writer.WriteStartArray(name);
        foreach (RimRunnerAckEntry ack in acks)
        {
            writer.WriteStartObject();
            writer.WriteString("subject", ack.subject);
            writer.WriteString("kind", ack.kind);
            writer.WriteString("target", ack.target);
            writer.WriteString("origin", ack.origin);
            writer.WriteString("anchor", ack.anchor);
            writer.WriteString("reason", ack.reason);
            writer.WriteNumber("hits", ack.hits);
            AppendMisses(writer, ack.tolerance, ack.misses, ack.unreachable);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendMisses(Utf8JsonWriter writer, int tolerance, int? misses, string? unreachable)
    {
        writer.WriteNumber("tolerance", tolerance);
        if (misses is int value)
        {
            writer.WriteNumber("misses", value);
        }
        else
        {
            writer.WriteNull("misses");
        }

        writer.WriteString("unreachable", unreachable);
    }

    private static void AppendBorrowed(
        Utf8JsonWriter writer, string name, List<KeyValuePair<string, List<string>>> layers)
    {
        writer.WriteStartObject(name);
        foreach (KeyValuePair<string, List<string>> layer in layers)
        {
            AppendStringArray(writer, layer.Key, layer.Value);
        }

        writer.WriteEndObject();
    }

    private static void AppendInputs(Utf8JsonWriter writer, List<RimRunnerInputRead> inputs)
    {
        writer.WriteStartArray(RimRunnerWireKeys.Report.Inputs);
        foreach (RimRunnerInputRead input in inputs)
        {
            writer.WriteStartObject();
            writer.WriteString(RimRunnerWireKeys.Report.InputPath, input.path);
            writer.WriteString(RimRunnerWireKeys.Report.InputSource, input.source);
            writer.WriteString(RimRunnerWireKeys.Report.InputHash, input.hash);
            AppendStringArray(writer, RimRunnerWireKeys.Report.InputCases, input.cases);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendStringArray(Utf8JsonWriter writer, string name, List<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
