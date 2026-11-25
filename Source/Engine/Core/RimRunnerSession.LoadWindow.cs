namespace RimRunner;

public static partial class RimRunnerSession
{
    internal enum LoadWindowSource
    {
        Checkpoint,
        Baseline,
        CleanSave,
        ColdStart,
        ShowcaseReload,
        FilmReload
    }

    private static LoadWindow? loadWindow;

    private static readonly List<string> pendingReloadErrors = [];
    private static readonly List<RimRunnerWarningEntry> pendingReloadWarnings = [];
    private static readonly HashSet<string> pendingLoadScopes = new(StringComparer.Ordinal);

    private sealed class LoadWindow
    {
        internal LoadWindowSource Source;
        internal required RimRunnerLogCapture Capture;
        internal List<RimRunnerLogLine>? LoadSegment;
    }

    internal static string? OpenLoadWindowLabel =>
        loadWindow == null ? null : "载档窗（" + DescribeSource(loadWindow.Source) + "）";

    internal static void OpenLoadWindow(LoadWindowSource source)
    {
        if (loadWindow != null)
        {
            AbandonLoadWindow();
        }

        try
        {
            loadWindow = new LoadWindow
            {
                Source = source,
                Capture = RimRunnerLogMonitor.Snapshot(includeWarnings: true, owner: DescribeSource(source))
            };
        }
        catch (InvalidOperationException exception)
        {
            pendingReloadErrors.Add(DescribeSource(source) + "：载档窗未能打开，本次载档期间的错误级日志未记入："
                                    + exception.Message);
        }
    }

    internal static void SplitLoadWindow()
    {
        if (loadWindow == null || loadWindow.LoadSegment != null)
        {
            return;
        }

        loadWindow.LoadSegment = RimRunnerLogMonitor.DiffSince(loadWindow.Capture);
        loadWindow.Capture = RimRunnerLogMonitor.Snapshot(includeWarnings: true,
            owner: DescribeSource(loadWindow.Source) + " · 读档后");
    }

    internal static void AbandonLoadWindow()
    {
        if (!TryCloseLoadWindow(out LoadWindow? closed, out List<RimRunnerLogLine>? settle))
        {
            return;
        }

        AppendReloadErrors(pendingReloadErrors, closed, settle, DescribeSource(closed.Source) + "（未完成）", finished: false);
    }

    internal static void CloseLoadWindow(string command, string? runId)
    {
        if (!TryCloseLoadWindow(out LoadWindow? closed, out List<RimRunnerLogLine>? settle))
        {
            return;
        }

        if (!HasContinuation(command, runId))
        {
            AppendReloadErrors(pendingReloadErrors, closed, settle, DescribeSource(closed.Source));
            return;
        }

        if (closed.Source == LoadWindowSource.Checkpoint && InRun.BatchIndex >= 0
                                                        && InRun.BatchIndex < InRun.ActiveBatch.Count)
        {
            AttributeLoadWindowToBatch(closed.LoadSegment!, settle);
            return;
        }

        AppendReloadErrors(InRun.Report.reloadErrors, closed, settle, DescribeSource(closed.Source));
    }

    private static bool TryCloseLoadWindow(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LoadWindow? closed,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out List<RimRunnerLogLine>? settle)
    {
        closed = loadWindow;
        loadWindow = null;
        settle = null;
        if (closed == null)
        {
            return false;
        }

        List<RimRunnerLogLine> rest = RimRunnerLogMonitor.DiffSince(closed.Capture);
        if (closed.LoadSegment == null)
        {
            closed.LoadSegment = rest;
            settle = [];
        }
        else
        {
            settle = rest;
        }

        return true;
    }

    private static void AttributeLoadWindowToBatch(
        List<RimRunnerLogLine> loadSegment, List<RimRunnerLogLine> settle)
    {
        int first = InRun.BatchIndex;
        for (int i = first; i < InRun.ActiveBatch.Count; i++)
        {
            CheckpointBatchMember member = InRun.ActiveBatch[i];
            RimRunnerHandoffFingerprint.Observe(member.HandoffReadings, member.CheckpointState,
                "checkpoint 读档（含读档后补推的 1 tick）", false, member.Entry.pendingFingerprints);
            SortFreshLogs(member.Entry,
                TakeProbeLines(member, loadSegment, RimRunnerLoadWindowProbe.LoadSegment),
                treatWarningsAsErrors: true);
            SortFreshLogs(member.Entry,
                TakeProbeLines(member, settle, RimRunnerLoadWindowProbe.TickSegment));
        }

        int batchSize = InRun.ActiveBatch.Count - first;
        for (int i = InRun.ActiveBatch.Count - 1; i >= first; i--)
        {
            CheckpointBatchMember member = InRun.ActiveBatch[i];
            if (!EntryHasError(member.Entry))
            {
                continue;
            }

            member.Entry.pendingFingerprints.Add(RimRunnerFindingEntry.Of(RimRunnerFindingKey.CheckpointLoadWindow, null,
                "错误级日志出现在 checkpoint 载档窗（读档本体或读档后首 tick）。phase " + member.NextPhase + " 起未执行。同批 "
                + batchSize + " 个成员共用这次载档窗"));
            InRun.CurrentPhaseStartTick = -1;
            RunStrandedCarriedCleanups(member.Entry, member.CarriedCleanups);
            FinishEntry(member.Entry, member.CheckpointState);
            InRun.ActiveBatch.RemoveAt(i);
        }
    }

    private static List<RimRunnerLogLine> TakeProbeLines(
        CheckpointBatchMember member, List<RimRunnerLogLine> lines, string segment)
    {
        List<RimRunnerLogLine> rest = new(lines.Count);
        foreach (RimRunnerLogLine line in lines)
        {
            if (RimRunnerLoadWindowProbe.TryClaim(member.CaseId, line?.Text, segment))
            {
                member.Entry.expectedLogs.Add(line!.Text);
                continue;
            }

            if (RimRunnerLoadWindowProbe.IsProbeLine(line?.Text))
            {
                continue;
            }

            rest.Add(line!);
        }

        return rest;
    }

    private static void AppendReloadErrors(List<string> sink, LoadWindow closed,
        List<RimRunnerLogLine> settle, string label, bool finished = true)
    {
        string scope = RimRunnerSeenLogLedger.LoadScope(closed.Source.ToString());
        if (finished)
        {
            pendingLoadScopes.Add(scope);
        }

        foreach (RimRunnerLogLine line in closed.LoadSegment!)
        {
            string? text = line?.Text;
            if (text != null && text.StartsWith(RimRunnerLogMonitor.WarningPrefix, StringComparison.Ordinal)
                             && !IsSaveIntegrityWarning(text))
            {
                if (!IsBenignSaveWindowWarning(text))
                {
                    pendingReloadWarnings.Add(ReloadWarning(label, text, scope));
                }

                continue;
            }

            AppendReloadError(sink, label + " · 读档本体", line);
        }

        foreach (RimRunnerLogLine line in settle)
        {
            if (line?.Text != null
                && line.Text.StartsWith(RimRunnerLogMonitor.WarningPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            AppendReloadError(sink, label + " · 读档后", line);
        }
    }

    private static void AppendReloadError(List<string> sink, string label, RimRunnerLogLine? line)
    {
        string? text = line?.Text;
        if (text == null)
        {
            return;
        }

        string? foreignSource = RimRunnerForeignAttribution.Resolve(text, text);
        sink.Add(label + (foreignSource == null ? "" : "（他 mod：" + foreignSource + "）") + "：" + text);
    }

    internal static RimRunnerWarningEntry ReloadWarning(string label, string line, string? scope = null)
    {
        return new RimRunnerWarningEntry
        {
            key = RimRunnerWarningKey.SaveWindowWarning,
            text = label + " · 读档本体：" + DescribeSaveWindowWarning(line),
            scope = scope,
        };
    }

    internal static void SettleReloadWarnings(RimRunnerReport report, IEnumerable<RimRunnerWarningEntry> warnings,
        Dictionary<string, string>? batchAcks, bool customSave)
    {
        foreach (RimRunnerWarningEntry warning in warnings)
        {
            if (customSave)
            {
                report.customSaveNotes.Add(warning.text);
                continue;
            }

            string? reason = null;
            batchAcks?.TryGetValue(warning.key, out reason);
            warning.reason = reason;
            (reason == null ? report.warnings : report.warningsAcked).Add(warning);
        }
    }

    internal static List<string> TakePendingReloadErrors()
    {
        List<string> taken = [.. pendingReloadErrors];
        pendingReloadErrors.Clear();
        pendingReloadWarnings.Clear();
        pendingLoadScopes.Clear();
        return taken;
    }

    private static void DrainPendingReloadErrors(RimRunnerReport report, Dictionary<string, string>? batchAcks)
    {
        report.reloadErrors.AddRange(pendingReloadErrors);
        pendingReloadErrors.Clear();
        SettleReloadWarnings(report, pendingReloadWarnings, batchAcks, RimRunnerPassRate.ResolveCustomSave() != null);
        pendingReloadWarnings.Clear();
        report.warningScopes.UnionWith(pendingLoadScopes);
        pendingLoadScopes.Clear();
    }

    private static string DescribeSource(LoadWindowSource source)
    {
        return source switch
        {
            LoadWindowSource.Checkpoint => "checkpoint 载档",
            LoadWindowSource.Baseline => "回基线载档",
            LoadWindowSource.CleanSave => "clean-save 载档",
            LoadWindowSource.ColdStart => "冷启动载档",
            LoadWindowSource.ShowcaseReload => "拆场回载",
            LoadWindowSource.FilmReload => "拍摄回载",
            _ => source.ToString(),
        };
    }
}
