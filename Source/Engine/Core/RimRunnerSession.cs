using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace RimRunner;

public static partial class RimRunnerSession
{
    private const string CheckpointSaveName = "RimRunnerCheckpoint";
    private static RunContinuation? continuation;

    private static RunContinuation InRun => continuation!;

    internal static RimRunnerRunWorldTally? WorldTally => continuation?.WorldTally;

    internal static string? ActiveRunId => continuation?.RunId;
    internal static string? CurrentBatchIdentity =>
        continuation is { } run ? run.RunId + ":" + run.BatchStartSerial : null;
    internal static bool RealFrameActive => continuation?.RealFrameExecution != null;

    internal static bool BodySuspendedOutside =>
        (continuation?.RealFrameExecution != null || continuation?.SuspendedPhase != null) && !RimRunnerStepper.AnyStepping;

    internal static void RunCases(RimRunnerCaseSelector selector, string? runId = null, bool exclusive = false,
        RimRunnerShardRequest? shard = null)
    {
        if (selector == null)
        {
            throw new ArgumentNullException(nameof(selector));
        }

        string command = selector.ToCanonicalLabel();
        string resolvedRunId = ResolveRunId(runId);
        if (!RimRunnerRunCoordinator.TryStart(
                command, resolvedRunId, out RimRunnerRunSnapshot activeRun))
        {
            RimRunnerEngineLog.Warn("拒绝重入运行：requested=" + command
                                    + "/" + resolvedRunId + " active=" + activeRun.State + "/"
                                    + activeRun.Command + "/" + activeRun.RunId);
            return;
        }

        RimRunnerDanglingRefProbe.ResetArmCount();
        RimRunnerTaleDanglingProbe.Reset();
        RimRunnerSustainedErrorBreaker.ResetForRun();
        RimRunnerWindowWatch.ResetForRun();
        RimRunnerSeenLogLedger.ResetForRun();
        RimRunnerBorrow.ResetForRun();
        RimRunnerRunClaims.Bundle runClaims = RimRunnerRunClaims.Take(resolvedRunId);
        RimRunnerInput.BeginRun(runClaims.Inputs);
        RimRunnerRingLedger.ReloadAll();
        RimRunnerFootprintProbe.ResetRun();
        RimRunnerMapSiteLedger.ResetRun();
        RimRunnerCurrentMapProbe.ResetRun();
        RimRunnerLiveStorytellerScope.ResetRun();
        RimRunnerFrozenMapSpawnProbe.ResetRun();
        RimRunnerEnvChecks.ResetForRun();
        RimRunnerScenarioManager.ResetForRun();
        RimRunnerBattleLogDiscardProbe.ResetForRun();
        RimRunnerContext.ResetResiduePawnsForRun();
        bool worldTouchedAtStart = !RimRunnerGameComponent.WorldPristineSinceLoad;
        RimRunnerGameComponent.MarkWorldTouched();
        List<RimRunnerTestCase> resolvedCases = OrderForExecution(
            RimRunnerRegistry.FirstPerId(selector.ResolveCases()));
        ApplicabilitySplit applicability = SplitByApplicability(resolvedCases);
        long contractScanMs = RimRunnerContractGates.EnsureRan() ? RimRunnerContractGates.ScanMs : 0;
        IReadOnlyList<RimRunnerRunClaims.Ack> startupAcks = runClaims.StartupAcks;
        Dictionary<string, string> batchAcks = RimRunnerWarnings.BatchAcks(runClaims.KeyAcks);
        List<RimRunnerChainAck> chainAcks = RimRunnerWarnings.ChainAcks(runClaims.KeyAcks);
        RimRunnerFindingResolution findings = RimRunnerWarnings.Resolve(
            RimRunnerRegistry.Findings.Concat(RimRunnerContractGates.FindingsInRegistryOrder())
                .Concat(StartupAckFindings(startupAcks))
                .Concat(RimRunnerWarnings.BatchAckDeclarationFindings(runClaims.KeyAcks))
                .Concat(RimRunnerEnvChecks.KnownFailingFindings(runClaims.KnownFailing))
                .Concat(RimRunnerComponentState.UseModListExemptions(runClaims.ComponentExemptions))
                .Concat(runClaims.CliWarnings.Select(warning => RimRunnerFinding.Warning(null, warning.Key, warning.Value))),
            batchAcks, ilScanIncomplete: RimRunnerContractGates.IlScanIncomplete);
        List<RimRunnerRejectionEntry> caseRejections = [];
        List<RimRunnerTestCase> runnable = [];
        foreach (RimRunnerTestCase testCase in applicability.Runnable)
        {
            string? refusal = RimRunnerTextureTier.FloorRefusal(testCase);
            if (findings.CaseFacts.TryGetValue(testCase.Id, out List<string>? facts))
                caseRejections.Add(RejectionEntry(RimRunnerRejectionGate.Contract, testCase.Id, string.Join("\n", facts)));
            else if (refusal != null)
                caseRejections.Add(RejectionEntry(RimRunnerRejectionGate.Texture, testCase.Id, refusal));
            else
                runnable.Add(testCase);
        }

        List<List<RimRunnerCheckpointPlanner.Member>> checkpointPlan = PlanCheckpointBatches(runnable);
        List<RimRunnerTestCase> orderedCases = ApplyCheckpointPlan(runnable, checkpointPlan);
        continuation = new RunContinuation
        {
            Selector = selector,
            Command = command,
            RunId = resolvedRunId,
            Report = new RimRunnerReport
            {
                command = command,
                runId = resolvedRunId,
                memoryStart = RimRunnerMemoryProbe.Snapshot(),
                contractScanMs = contractScanMs
            },
            RemainingCaseIds = new Queue<string>(orderedCases.Select(testCase => testCase.Id)),
            TotalCaseCount = orderedCases.Count,
            ApplicabilityFaults = applicability.Faults,
            BatchAcks = batchAcks,
            ChainAcks = chainAcks,
            WorldTouchedSinceLoad = worldTouchedAtStart
        };
        for (int batch = 0; batch < checkpointPlan.Count; batch++)
        {
            List<string> ids = checkpointPlan[batch].Select(member => member.Id).ToList();
            InRun.CheckpointBatches.Add(ids);
            foreach (string id in ids)
                InRun.CheckpointBatchOf[id] = batch;
            InRun.Report.checkpointBatches.Add(string.Join(", ", ids));
        }

        InRun.Report.caseRejections.AddRange(caseRejections);
        HashSet<string> runnableIds = new(
            applicability.Runnable.Select(testCase => testCase.Id), StringComparer.Ordinal);
        foreach (string subject in findings.CaseFactOrder.Where(subject => !runnableIds.Contains(subject)))
            InRun.Report.outOfBatchFaults.Add(
                RejectionEntry(RimRunnerRejectionGate.Contract, subject, string.Join("\n", findings.CaseFacts[subject])));
        HashSet<string> resolvedIds = new(
            resolvedCases.Select(testCase => testCase.Id), StringComparer.Ordinal);
        HashSet<string> cliKeys = new(runClaims.CliWarnings.Select(warning => warning.Key).Concat(runClaims.CliChecked),
            StringComparer.Ordinal);
        foreach (RimRunnerWarningEntry entry in findings.Warnings.Where(entry => entry.subject == null))
            entry.scope = cliKeys.Contains(entry.key) ? RimRunnerSeenLogLedger.CliScope(entry.key) : RimRunnerSeenLogLedger.RegistrationScope;
        InRun.Report.warningScopes.Add(RimRunnerSeenLogLedger.RegistrationScope);
        foreach (string key in runClaims.CliChecked)
            InRun.Report.warningScopes.Add(RimRunnerSeenLogLedger.CliScope(key));
        InRun.Report.warnings.AddRange(findings.Warnings.Where(entry =>
            entry.subject == null || resolvedIds.Contains(entry.subject)));
        InRun.Report.warningsAcked.AddRange(findings.Acked.Where(entry =>
            entry.subject == null || resolvedIds.Contains(entry.subject)));

        foreach (RimRunnerTestCase excluded in selector.BatchExcludedCases())
            InRun.Report.excludedCases.Add(excluded.Id);

        foreach (RimRunnerTestCase skipped in selector.GroupSkippedCases())
            InRun.Report.groupSkippedCases.Add(skipped.Id);

        HashSet<string> namedIds = new(selector.CaseIds, StringComparer.Ordinal);
        foreach (KeyValuePair<RimRunnerTestCase, string> inapplicable in applicability.Inapplicable)
        {
            if (namedIds.Contains(inapplicable.Key.Id))
                InRun.Report.caseRejections.Add(RejectionEntry(RimRunnerRejectionGate.Command, inapplicable.Key.Id,
                    "指定的用例在本环境不适用：" + inapplicable.Value));
            else
                InRun.Report.inapplicableCases[inapplicable.Key.Id] = inapplicable.Value;
        }

        foreach (KeyValuePair<string, string> absent in RimRunnerRegistry.AbsentTypes)
            InRun.Report.absentCases[absent.Key] = absent.Value;

        DrainPendingReloadErrors(InRun.Report, InRun.BatchAcks);

        InRun.Report.foreignStartupErrors.AddRange(
            RimRunnerStartupErrors.Foreign);

        if (RimRunnerStartupErrors.QueueCapNote is string queueCap)
        {
            AddBatchWarning(RimRunnerWarningKey.StartupLogEvicted, queueCap);
        }

        List<RimRunnerRunClaims.Ack> liveStartupAcks =
            startupAcks.Where(ack => !string.IsNullOrWhiteSpace(ack.Reason)).ToList();
        RimRunnerStartupErrors.ClaimResult claims =
            RimRunnerStartupErrors.Claim(liveStartupAcks.Select(ack => ack.Target).ToList());
        InRun.Report.claimedStartupErrors.AddRange(claims.Claimed);
        InRun.Report.unattributedStartupErrors.AddRange(claims.Unattributed);
        for (int index = 0; index < liveStartupAcks.Count; index++)
        {
            InRun.Report.acks.Add(new RimRunnerAckEntry
            {
                kind = RimRunnerAckEntry.StartupKind,
                target = liveStartupAcks[index].Target,
                reason = liveStartupAcks[index].Reason.Trim(),
                hits = claims.Hits[index]
            });
        }

        foreach (string startupError in claims.Blocking)
        {
            AddRejection(InRun.Report, RimRunnerRejectionGate.Startup, null, startupError);
        }

        foreach (string engineFault in RimRunnerLogMonitor.StartupEngineFaults)
        {
            AddRejection(InRun.Report, RimRunnerRejectionGate.Engine, null, engineFault);
        }

        foreach (KeyValuePair<string, string> declared in RimRunnerEnvChecks.KnownFailing(runClaims.KnownFailing))
        {
            InRun.KnownFailing[declared.Key] = declared.Value;
        }

        if (resolvedCases.Count == 0)
        {
            AddRejection(InRun.Report, RimRunnerRejectionGate.Command, null,
                "命令未选中任何测试用例" + RimRunnerPackages.AbsentNote(RimRunnerRegistry.AbsentTypes, "用例类")
                + RimRunnerEjectedAssemblies.Note);
        }
        else if (selector.Groups.Length > 0
                 && !resolvedCases.Any(testCase => RimRunnerCaseSelector.GroupFiltered(testCase)
                                                   && !RimRunnerEnvChecks.IsEnvCheck(testCase)))
        {
            AddRejection(InRun.Report, RimRunnerRejectionGate.Command, null,
                "按组 " + string.Join("、", selector.Groups) + " 选中的产品用例为 0 条"
                + RimRunnerPackages.AbsentNote(RimRunnerRegistry.AbsentTypes, "用例类"));
        }
        else if (applicability.Runnable.Count == 0)
        {
            AddRejection(InRun.Report, RimRunnerRejectionGate.Command, null,
                "命令选中的用例在本环境全部不适用（NotApplicableReason），本批不执行");
        }

        if (!exclusive)
        {
            foreach (RimRunnerTestCase testCase in orderedCases.Where(testCase => testCase.DeclaresQuietMachine))
                AddRejection(InRun.Report, RimRunnerRejectionGate.Quiet, testCase.Id,
                    "用例声明 QuietMachine，本次执行未独占机器（run 请求未带 exclusive）");
        }

        if (shard != null)
        {
            List<string> shardFaults = [];
            InRun.Shard = RimRunnerShardEnumeration.Admit(shard, orderedCases, shardFaults,
                InRun.Report.warnings, InRun.Report.warningsAcked);
            foreach (string fault in shardFaults)
                AddRejection(InRun.Report, RimRunnerRejectionGate.Shard, null, fault);
            if (InRun.Shard != null)
            {
                InRun.Report.shard = new RimRunnerShardInfo
                {
                    group = shard.Group,
                    index = shard.Index,
                    count = shard.Count,
                    caseId = InRun.Shard.CaseId,
                    fingerprint = shard.Fingerprint
                };
                InRun.Shard.Info = InRun.Report.shard;
            }
        }

        if (RimRunnerContractGates.ScanFault != null)
        {
            AddRejection(InRun.Report, RimRunnerRejectionGate.Contract, null,
                RimRunnerContractGates.ScanFault);
        }

        foreach (string batchFact in findings.BatchFacts)
        {
            AddRejection(InRun.Report, RimRunnerRejectionGate.Registry, null, batchFact);
        }

        foreach (KeyValuePair<string, string> duplicate in RimRunnerRegistry.DuplicateIdFaults)
        {
            AddRejection(InRun.Report, RimRunnerRejectionGate.Contract, duplicate.Key, duplicate.Value);
        }

        foreach (string packageFault in RimRunnerRegistry.PackageFaults.Concat(RimRunnerEjectedAssemblies.Faults))
        {
            AddRejection(InRun.Report, RimRunnerRejectionGate.Package, null, packageFault);
        }

        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            InRun.Report.warnings.Add(new RimRunnerWarningEntry
            {
                key = RimRunnerWarningKey.RosterUnverified,
                text = "进程不是由 CLI 启动的（未带 -" + RimRunnerEngineArgs.Enable + "），跳过用例名册核对",
                scope = RimRunnerSeenLogLedger.RegistrationScope,
            });
        }
        else
        {
            RimRunnerRosterCheck.Result roster = RimRunnerRosterCheck.Resolve(
                RimRunnerRegistry.All, RimRunnerRegistry.CaseTypes, RimRunnerRegistry.UninstantiatedTypes);
            foreach (string fault in roster.Faults)
            {
                AddRejection(InRun.Report, RimRunnerRejectionGate.Registry, null, fault);
            }

            foreach (string warning in roster.Warnings)
            {
                InRun.Report.warnings.Add(new RimRunnerWarningEntry
                {
                    key = RimRunnerWarningKey.RosterUnverified,
                    text = warning,
                    scope = RimRunnerSeenLogLedger.RegistrationScope,
                });
            }
        }

        if (InRun.Report.rejections.Count > 0)
        {
            CompleteContinuation();
            return;
        }

        ContinueRun();
    }

    internal static bool HasAnyContinuation => continuation != null;

    internal static bool HasContinuation(string? command, string? runId)
    {
        return continuation != null
               && string.Equals(continuation.Command, command, StringComparison.Ordinal)
               && string.Equals(continuation.RunId, runId, StringComparison.Ordinal);
    }

    internal static void ResumeAfterLoad(string command, string runId)
    {
        if (!HasContinuation(command, runId))
        {
            WriteTerminalError(command, runId, "载档完成，但没有匹配的多阶段续段");
            return;
        }

        if (!RimRunnerRunCoordinator.TryStart(
                command, runId, out RimRunnerRunSnapshot activeRun))
        {
            RimRunnerEngineLog.Warn("拒绝恢复续段：requested=" + command + "/" + runId
                                    + " active=" + activeRun.State + "/" + activeRun.Command + "/" + activeRun.RunId);
            return;
        }

        if (InRun.ReloadTimer.IsRunning)
        {
            InRun.ReloadTimer.Stop();
            InRun.ReloadMs += InRun.ReloadTimer.ElapsedMilliseconds;
        }

        bool completeAfterBaseline = InRun.AwaitingBaselineLoad
                                     && InRun.CompleteAfterBaselineLoad;
        if (InRun.AwaitingBaselineLoad)
        {
            InRun.WorldTouchedSinceLoad = false;
            InRun.MainMapNeedsReload = false;
            if (InRun.MapRestore != null)
            {
                InRun.WorldTally.NoteMissed(InRun.MapRestore.Members, "所在段收尾前回载基线档");
                if (InRun.MapRestore.ScenarioRestores.PendingCount > 0)
                {
                    RimRunnerEngineLog.Mark(RimRunnerObservationKeys.RestoreMissedReload, InRun.MapRestore.BatchLabel);
                    RimRunnerEngineLog.Warn("[engine] " + InRun.MapRestore.BatchLabel + "的装配还原未在回载基线档之前执行，已丢弃 "
                                            + InRun.MapRestore.ScenarioRestores.PendingCount + " 条");
                }
            }

            InRun.MapRestore = null;
        }
        else
        {
            InRun.MapRestore?.NoteCheckpointLoad();
        }

        InRun.AwaitingBaselineLoad = false;
        InRun.CompleteAfterBaselineLoad = false;
        if (completeAfterBaseline)
        {
            CompleteContinuation();
            return;
        }

        ContinueRun();
    }

    private static void ContinueRun()
    {
        try
        {
            while (continuation != null)
            {
                if (continuation.CurrentCaseId == null && !TrySelectNextCase())
                {
                    return;
                }

                RimRunnerTestCase? testCase = RimRunnerRegistry.All.FirstOrDefault(
                    item => string.Equals(item.Id, InRun.CurrentCaseId, StringComparison.Ordinal));
                if (testCase == null)
                {
                    AppendCurrentException(RimRunnerLog.Prefix + " 用例名册中没有要继续执行的用例 "
                                                               + InRun.CurrentCaseId);
                    FinishCurrentCase();
                    AdvanceBatchPointer();
                    continue;
                }

                if (InRun.NextPhase == 0
                    && InRun.ApplicabilityFaults.TryGetValue(testCase.Id, out string? applicabilityFault))
                {
                    AppendCurrentException(applicabilityFault);
                    FinishCurrentCase();
                    AdvanceBatchPointer();
                    continue;
                }

                if (testCase is IRimRunnerRealFrameCase realFrameCase)
                {
                    if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
                    {
                        AppendCurrentException(RimRunnerLog.Prefix + " 真实帧用例仅允许带 -" + RimRunnerEngineArgs.Enable + " 的实例："
                                               + testCase.Id);
                        FinishCurrentCase();
                        AdvanceBatchPointer();
                        continue;
                    }

                    if (testCase.DeclaresLiveStoryteller && InRun.WorldTouchedSinceLoad)
                    {
                        RequestBaselineLoad(completeAfterLoad: false);
                        return;
                    }

                    InRun.WorldTouchedSinceLoad = true;
                    BeginCurrentRealFrameCase(testCase, realFrameCase);
                    return;
                }

                IRimRunnerPhasedCase? phasedCase = testCase as IRimRunnerPhasedCase;
                if (phasedCase != null
                    && (phasedCase.PhaseCount <= 0
                        || InRun.NextPhase >= phasedCase.PhaseCount))
                {
                    AppendCurrentException(RimRunnerLog.Prefix + " " + testCase.Id
                                           + " 的 PhaseCount/nextPhase 非法：" + phasedCase.PhaseCount + "/"
                                           + InRun.NextPhase);
                    FinishCurrentCase();
                    AdvanceBatchPointer();
                    continue;
                }

                if (phasedCase != null
                    && !GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
                {
                    AppendCurrentException(RimRunnerLog.Prefix + " 多阶段用例仅允许带 -" + RimRunnerEngineArgs.Enable + " 的实例："
                                           + testCase.Id);
                    FinishCurrentCase();
                    AdvanceBatchPointer();
                    continue;
                }

                InRun.WorldTouchedSinceLoad = true;
                if (!ExecuteCurrentPhase(testCase))
                {
                    return;
                }

                if (!AdvanceAfterCurrentPhase(testCase))
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            if (continuation != null)
            {
                AddBatchEngineFault(continuation.Report, "运行级异常", exception);
                CompleteContinuation();
            }
        }
    }

    private static bool TrySelectNextCase()
    {
        if (InRun.BatchIndex >= 0)
        {
            if (InRun.BatchIndex < InRun.ActiveBatch.Count)
            {
                CheckpointBatchMember member =
                    InRun.ActiveBatch[InRun.BatchIndex];
                InRun.CurrentCaseId = member.CaseId;
                InRun.CurrentEntry = member.Entry;
                InRun.CurrentCheckpointState = member.CheckpointState;
                InRun.CurrentCarriedCleanups = member.CarriedCleanups;
                InRun.CurrentCarriedSpecTargets = member.CarriedSpecTargets;
                InRun.NextPhase = member.NextPhase;
                InRun.ScenarioPawnRoster = member.ScenarioPawnRoster;
                InRun.CurrentPhaseTicks = member.PhaseTicks;
                PublishDiagnosticSnapshot();
                return true;
            }

            InRun.ActiveBatch.Clear();
            InRun.BatchIndex = -1;
            if (InRun.PendingBatch.Count > 0)
            {
                return FireCheckpointBarrier();
            }
        }

        if (InRun.RemainingCaseIds.Count == 0)
        {
            if (InRun.PendingBatch.Count > 0)
            {
                return FireCheckpointBarrier();
            }

            CompleteContinuation();
            return false;
        }

        if (InRun.PendingBatch.Count > 0
            && CheckpointBatchOf(InRun.RemainingCaseIds.Peek())
            != CheckpointBatchOf(InRun.PendingBatch[0].CaseId))
        {
            return FireCheckpointBarrier();
        }

        if (InRun.MapRestore != null && InRun.PendingBatch.Count == 0)
        {
            RimRunnerEngineLog.Warn("主图占用段未在收尾时关闭，回载基线档：成员 "
                                    + string.Join("、", InRun.MapRestore.Members));
            DropScenarioRestores(InRun.MapRestore, "未在收尾时关闭");
            InRun.WorldTally.NoteMissed(InRun.MapRestore.Members, "主图占用段未在收尾时关闭");
            InRun.MapRestore = null;
            InRun.MainMapNeedsReload = true;
        }

        if (InRun.MainMapNeedsReload && InRun.PendingBatch.Count == 0)
        {
            InRun.MainMapNeedsReload = false;
            InRun.MapReloads++;
            RequestBaselineLoad(completeAfterLoad: false);
            return false;
        }

        InRun.CurrentCaseId = InRun.RemainingCaseIds.Dequeue();
        BeginCheckpointBatchIfNeeded(InRun.CurrentCaseId);
        InRun.NextPhase = 0;
        InRun.ScenarioPawnRoster = null;
        InRun.CurrentPhaseTicks = [];
        InRun.CurrentEntry = new RimRunnerResultEntry
        {
            id = InRun.CurrentCaseId
        };
        if (InRun.RetryCarryover.TryGetValue(InRun.CurrentCaseId, out RimRunnerResultEntry? abandoned))
        {
            InRun.RetryCarryover.Remove(InRun.CurrentCaseId);
            CarryOverAbandoned(abandoned, InRun.CurrentEntry);
        }
        InRun.CurrentCheckpointState =
            new Dictionary<string, string>(StringComparer.Ordinal);
        InRun.CurrentCarriedCleanups = [];
        InRun.CurrentCarriedSpecTargets = [];
        PublishDiagnosticSnapshot();
        return true;
    }

    private static void CarryOverAbandoned(RimRunnerResultEntry abandoned, RimRunnerResultEntry retry)
    {
        retry.foreignErrors.AddRange(abandoned.foreignErrors);
        retry.newWarnings.AddRange(abandoned.newWarnings);
        retry.pendingResidue.AddRange(abandoned.pendingResidue);
        retry.durationMs += abandoned.durationMs;
    }

    private static int CheckpointBatchOf(string caseId)
    {
        return InRun.CheckpointBatchOf.TryGetValue(caseId, out int batch) ? batch : -1;
    }

    private static void BeginCheckpointBatchIfNeeded(string caseId)
    {
        int batch = CheckpointBatchOf(caseId);
        if (batch < 0 || batch == InRun.CurrentBatchNumber)
        {
            return;
        }

        InRun.CurrentBatchNumber = batch;
        InRun.BatchStartSerial++;
    }

    internal static IReadOnlyList<string> CurrentCheckpointBatchMembers =>
        continuation?.CurrentCaseId is { } caseId ? CheckpointBatchMembersOf(caseId).ToList() : [];

    internal static int CheckpointBatchCount => continuation?.CheckpointBatches.Count ?? 0;

    private static IEnumerable<string> CheckpointBatchMembersOf(string caseId)
    {
        int batch = CheckpointBatchOf(caseId);
        return batch < 0 ? Enumerable.Empty<string>() : InRun.CheckpointBatches[batch];
    }

    private static bool IsMultiPhaseCase(string caseId)
    {
        return RimRunnerRegistry.All.FirstOrDefault(item =>
                string.Equals(item.Id, caseId, StringComparison.Ordinal))
            is IRimRunnerPhasedCase;
    }

    internal static bool IsHeldByCheckpointBatch(string thingId)
    {
        return continuation != null && thingId != null
                                    && HoldingMembers().Any(member => HoldsRegistered(member, thingId));
    }

    private static IEnumerable<CheckpointBatchMember> HoldingMembers()
    {
        foreach (CheckpointBatchMember member in InRun.PendingBatch)
        {
            yield return member;
        }

        for (int i = InRun.BatchIndex + 1; i < InRun.ActiveBatch.Count; i++)
        {
            yield return InRun.ActiveBatch[i];
        }
    }

    private static void ObserveHandoffsAfterCurrentPhase()
    {
        string window = InRun.CurrentCaseId + " phase " + InRun.NextPhase + " 之后";
        foreach (CheckpointBatchMember member in HoldingMembers())
        {
            RimRunnerHandoffFingerprint.Observe(member.HandoffReadings, member.CheckpointState, window, true,
                member.Entry.pendingFingerprints);
        }
    }

    private static bool HoldsRegistered(CheckpointBatchMember member, string thingId)
    {
        if (member.CheckpointState == null)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> pair in member.CheckpointState)
        {
            if ((pair.Key.StartsWith(RimRunnerContext.RegisteredPawnStateKeyPrefix, StringComparison.Ordinal)
                 || pair.Key.StartsWith(RimRunnerContext.RegisteredThingStateKeyPrefix, StringComparison.Ordinal))
                && string.Equals(pair.Value, thingId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FireCheckpointBarrier()
    {
        if (InRun.BatchIndex >= 0)
        {
            InRun.ActiveBatch.RemoveRange(
                0, Math.Min(InRun.BatchIndex + 1, InRun.ActiveBatch.Count));
        }

        for (int index = InRun.PendingBatch.Count - 1; index >= 0; index--)
        {
            InRun.ActiveBatch.Add(InRun.PendingBatch[index]);
        }

        InRun.PendingBatch.Clear();
        InRun.CheckpointSaveTick = Find.TickManager?.TicksGame ?? -1;
        InRun.CheckpointBatchSize = InRun.ActiveBatch.Count;
        InRun.MapRestore?.NoteCheckpointSave();
        RimRunnerMapTicks.NoteCheckpointSave();
        RimRunnerIsolatedMap.NoteCheckpointSave();
        if (!SaveBatchCheckpoint())
        {
            foreach (CheckpointBatchMember member in InRun.ActiveBatch)
            {
                RunStrandedCarriedCleanups(member.Entry, member.CarriedCleanups);
                FinishEntry(member.Entry);
            }

            InRun.ActiveBatch.Clear();
            RequestBaselineLoad(
                completeAfterLoad: InRun.RemainingCaseIds.Count == 0);
            return false;
        }

        InRun.BatchIndex = 0;
        RequestLoad(CheckpointSaveName);
        return false;
    }

    private static bool AdvanceAfterCurrentPhase(RimRunnerTestCase testCase)
    {
        int now = Find.TickManager?.TicksGame ?? -1;
        if (testCase is IRimRunnerPhasedCase && InRun.CurrentSlotStartTick >= 0 && now >= 0)
        {
            InRun.CurrentPhaseTicks?.Add(now - InRun.CurrentSlotStartTick);
        }

        ObserveHandoffsAfterCurrentPhase();

        string? overBudget = InRun.PendingOverBudget;
        string? overBudgetRetry = InRun.PendingOverBudgetRetry;
        InRun.PendingOverBudget = null;
        InRun.PendingOverBudgetRetry = null;
        if (overBudget != null)
        {
            if (!CurrentEntryHasError())
            {
                return RequeueOverBudget(testCase, overBudgetRetry!);
            }

            InRun.CurrentEntry!.sentinelFaults.Add(RimRunnerFindingEntry.Of(null, null, overBudget));
        }

        if (CurrentEntryHasError())
        {
            return FinishFaultedCase();
        }

        if (testCase is IRimRunnerPhasedCase phased
            && HandsOffToNextPhase(InRun.CurrentEntry!, InRun.NextPhase, phased.PhaseCount))
        {
            if (InRun.NextPhase == 0 && IsRed(InRun.CurrentEntry!))
            {
                InRun.CurrentEntry!.redBeforeBarrier = true;
            }

            if (InRun.LastPhaseEndTick >= 0)
            {
                InRun.CurrentCheckpointState![
                        RimRunnerContext.PhaseEndTickStateKey]
                    = InRun.LastPhaseEndTick.ToString(CultureInfo.InvariantCulture);
            }

            if (InRun.LastPhaseEndMapTick >= 0)
            {
                InRun.CurrentCheckpointState![RimRunnerContext.PhaseEndMapTickStateKey]
                    = InRun.LastPhaseEndMapTick.ToString(CultureInfo.InvariantCulture);
            }

            InRun.PendingBatch.Add(new CheckpointBatchMember
            {
                CaseId = InRun.CurrentCaseId!,
                Entry = InRun.CurrentEntry!,
                CheckpointState = InRun.CurrentCheckpointState,
                CarriedCleanups = InRun.CurrentCarriedCleanups,
                CarriedSpecTargets = InRun.CurrentCarriedSpecTargets,
                NextPhase = InRun.NextPhase + 1,
                ScenarioPawnRoster = InRun.ScenarioPawnRoster,
                PhaseTicks = InRun.CurrentPhaseTicks,
                HandoffReadings = RimRunnerHandoffFingerprint.ReadAll(InRun.CurrentCheckpointState)
            });
            DetachCurrentCase();
            return AdvanceBatchPointer();
        }

        if (testCase is IRimRunnerPhasedCase && BatchRedRetryApplies(testCase))
        {
            return RequeueBatchRed(testCase);
        }

        SettleBatchRedRetry(testCase);
        RecordCheckpointTicks(testCase);
        FinishCurrentCase();
        return AdvanceBatchPointer();
    }

    private static bool IsRed(RimRunnerResultEntry entry) =>
        entry.sentinelFaults.Count > 0 || entry.asserts.Any(assertEntry => !assertEntry.pass);

    private static bool BatchRedRetryApplies(RimRunnerTestCase testCase)
    {
        RimRunnerResultEntry entry = InRun.CurrentEntry!;
        return InRun.NextPhase >= 1
               && IsRed(entry)
               && !entry.redBeforeBarrier
               && !entry.trippedCheckpointGate
               && CheckpointBatchMembersOf(testCase.Id).Count() > 1
               && RimRunnerCheckpointPlanner.EffectiveBudget(testCase) == int.MaxValue
               && !InRun.CheckpointRetried.Contains(testCase.Id);
    }

    private static bool RequeueBatchRed(RimRunnerTestCase testCase)
    {
        InRun.CheckpointRetried.Add(testCase.Id);
        List<string> members = CheckpointBatchMembersOf(testCase.Id).ToList();
        string firstRed = FirstRed(InRun.CurrentEntry!);
        int drift = CurrentPhaseClockDrift();
        InRun.BatchRedRetries[testCase.Id] = new BatchRedEvidence
        {
            FirstRed = firstRed,
            ClockDrift = drift,
            Members = members
        };
        return RequeueOverBudget(testCase,
            RimRunnerCheckpointPlanner.BatchRedRetried(testCase.Id, firstRed, drift, members));
    }

    private static void SettleBatchRedRetry(RimRunnerTestCase testCase)
    {
        if (!InRun.BatchRedRetries.TryGetValue(testCase.Id, out BatchRedEvidence? evidence))
        {
            return;
        }

        InRun.BatchRedRetries.Remove(testCase.Id);
        if (!IsRed(InRun.CurrentEntry!) && !InRun.CurrentEntry!.endedByClaimedFault)
        {
            InRun.CurrentEntry.sentinelFaults.Add(RimRunnerFindingEntry.Of(null, null, RimRunnerCheckpointPlanner.BatchRedSoloGreen(testCase.Id,
                evidence.FirstRed, evidence.ClockDrift, CurrentPhaseClockDrift(), testCase.OnIsolatedMap,
                evidence.Members)));
        }
    }

    private static string FirstRed(RimRunnerResultEntry entry)
    {
        RimRunnerAssertEntry? failed = entry.asserts.FirstOrDefault(assertEntry => !assertEntry.pass);
        string text = failed != null
            ? string.Concat(RimRunnerAssertLine.Parts(failed.label, failed.expected, failed.actual))
            : entry.sentinelFaults.FirstOrDefault()?.Line ?? "";
        return text.Length > 160 ? text[..160] + "…" : text;
    }

    private static int CurrentPhaseClockDrift()
    {
        return InRun.CurrentPhaseStartTick >= 0
               && InRun.CurrentCheckpointState != null
               && InRun.CurrentCheckpointState.TryGetValue(RimRunnerContext.PhaseEndTickStateKey, out string? raw)
            ? InRun.CurrentPhaseStartTick - int.Parse(raw, CultureInfo.InvariantCulture)
            : -1;
    }

    private static bool RequeueOverBudget(RimRunnerTestCase testCase, string retryLine)
    {
        RimRunnerResultEntry abandoned = InRun.CurrentEntry!;
        RunStrandedCarriedCleanups(abandoned, InRun.CurrentCarriedCleanups);
        InRun.RetryCarryover[testCase.Id] = abandoned;

        int batch = InRun.CheckpointBatches.Count;
        InRun.CheckpointBatches.Add([testCase.Id]);
        InRun.CheckpointBatchOf[testCase.Id] = batch;
        InRun.Report.checkpointBatches.Add(testCase.Id);
        InRun.Report.checkpointRetries.Add(retryLine);

        List<string> remaining = InRun.RemainingCaseIds.ToList();
        int insertAt = remaining.FindIndex(id => IsOnIsolatedMap(id)
            || RimRunnerRegistry.All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
                is IRimRunnerRealFrameCase);
        remaining.Insert(insertAt < 0 ? remaining.Count : insertAt, testCase.Id);
        InRun.RemainingCaseIds = new Queue<string>(remaining);

        DetachCurrentCase();
        return AdvanceBatchPointer();
    }

    private static bool IsOnIsolatedMap(string id) => RimRunnerRegistry.All.FirstOrDefault(item =>
        string.Equals(item.Id, id, StringComparison.Ordinal)) is { OnIsolatedMap: true } testCase
        && testCase is not IRimRunnerRealFrameCase;

    private static bool IsolatedWorkAfterCurrent() =>
        InRun.ActiveBatch.Skip(InRun.BatchIndex + 1).Any(member => IsOnIsolatedMap(member.CaseId))
        || InRun.PendingBatch.Any(member => IsOnIsolatedMap(member.CaseId))
        || InRun.RemainingCaseIds.Any(IsOnIsolatedMap);

    private static void RecordCheckpointTicks(RimRunnerTestCase testCase)
    {
        if (testCase is IRimRunnerPhasedCase phased
            && !InRun.CurrentEntry!.endedByClaimedFault
            && InRun.CurrentEntry.asserts.All(assertEntry => assertEntry.pass)
            && InRun.CurrentPhaseTicks != null
            && InRun.CurrentPhaseTicks.Count == phased.PhaseCount)
        {
            RimRunnerCheckpointTickLedger.Record(testCase.Id, InRun.CurrentPhaseTicks);
        }
    }

    internal static bool HandsOffToNextPhase(RimRunnerResultEntry entry, int currentPhase, int phaseCount)
    {
        return !entry.endedByClaimedFault && currentPhase + 1 < phaseCount;
    }

    private static bool FinishFaultedCase()
    {
        FinishCurrentCase();
        if (InRun.PendingBatch.Count > 0)
        {
            InRun.FaultReloads++;
            return FireCheckpointBarrier();
        }

        AdvanceBatchPointer();
        if (InRun.BatchIndex >= 0)
        {
            if (InRun.BatchIndex >= InRun.ActiveBatch.Count)
            {
                return true;
            }

            InRun.FaultReloads++;
            RequestLoad(CheckpointSaveName);
            return false;
        }

        if (InRun.RemainingCaseIds.Count == 0)
        {
            return true;
        }

        InRun.FaultReloads++;
        RequestBaselineLoad(completeAfterLoad: false);
        return false;
    }

    private static bool AdvanceBatchPointer()
    {
        if (InRun.BatchIndex >= 0)
        {
            InRun.BatchIndex++;
        }

        return true;
    }

    private static void ObserveCarriedOverProjectiles(RimRunnerContext context)
    {
        if (InRun.NextPhase <= 0)
        {
            return;
        }

        foreach (Map map in Find.Maps)
        {
            if (map == null)
            {
                continue;
            }

            foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile))
            {
                if (thing is Projectile projectile)
                {
                    context.ObserveProjectile(projectile);
                }
            }
        }
    }

    private static bool ExecuteCurrentPhase(RimRunnerTestCase testCase)
    {
        if (InRun.NextPhase == 0 && testCase.Level == RimRunnerLevel.World)
        {
            RimRunnerScenarioManager.SweepBeforeCase(testCase.Id);
        }

        RimRunnerLogCapture capture = RimRunnerLogMonitor.Snapshot(includeWarnings: true,
            owner: "用例 " + testCase.Id + (testCase is IRimRunnerPhasedCase ? " phase " + InRun.NextPhase : ""),
            ackCase: testCase);
        RimRunnerSustainedErrorBreaker.Arm(capture);
        RimRunnerStubProbe.Open(testCase.Id);
        RimRunnerMachineLoadProbe.Open(testCase);

        RimRunnerContext context;
        bool quiescedAtStart;
        try
        {
            RimRunnerStorytellerQuiesce.Ensure(testCase.Id);
            quiescedAtStart = RimRunnerStorytellerQuiesce.StillInEffect;

            context = testCase.NewContext();
            context.Map = testCase.Level == RimRunnerLevel.World ? RimRunnerCurrentMapProbe.EngineCurrentMap() : null;
            context.CarriesRegisteredToNextPhase = testCase is IRimRunnerPhasedCase carrying
                                                   && InRun.NextPhase + 1 < carrying.PhaseCount;
            context.ContinuesPreviousPhase = InRun.NextPhase > 0;
            context.ShardRun = InRun.Shard != null && InRun.Shard.CaseId == testCase.Id
                ? InRun.Shard
                : null;
            context.ShardableCase = testCase is IRimRunnerShardable ? testCase : null;
            context.SetCheckpointState(InRun.CurrentCheckpointState);
            context.SetCarriedCleanups(InRun.CurrentCarriedCleanups);
            context.SetCarriedSpecTargets(InRun.CurrentCarriedSpecTargets);
            if (InRun.NextPhase > 0)
            {
                context.ResolveRegisteredFromCheckpoint();
            }

            context.BeginCase(InRun.CurrentEntry!, testCase.DeclaresObservesRuntime);
            ObserveCarriedOverProjectiles(context);
        }
        catch
        {
            CloseCaseWindow(InRun.CurrentEntry!, capture);
            throw;
        }

        Stopwatch timer = Stopwatch.StartNew();
        PhaseRun run = new()
        {
            TestCase = testCase,
            Context = context,
            Capture = capture,
            Timer = timer,
            QuiescedAtStart = quiescedAtStart
        };
        int phaseStartMapTick = -1;
        int slotStartTick = Find.TickManager?.TicksGame ?? -1;
        InRun.CurrentSlotStartTick = slotStartTick;
        bool threw = false;
        try
        {
            if (InRun.NextPhase == 0
                && testCase.Level == RimRunnerLevel.World)
            {
                RimRunnerScenarioManager.EnsureAll(context);
                InRun.ScenarioPawnRoster =
                    context.SnapshotScenarioPawnRoster();
                RimRunnerMapSiteLedger.Record(testCase);
                if (testCase.OnIsolatedMap)
                {
                    bool phasedOwner = testCase is IRimRunnerPhasedCase;
                    RimRunnerIsolatedMap.Enter(context, testCase.IsolatedMapSize, testCase.IsolatedMapGround,
                        lastInSlot: !phasedOwner && !InRun.RemainingCaseIds.Any(IsOnIsolatedMap),
                        owner: phasedOwner ? testCase.Id : null);
                }
            }
            else if (InRun.NextPhase > 0)
            {
                bool resumesScenario = testCase.Level == RimRunnerLevel.World;
                if (testCase.OnIsolatedMap && testCase is IRimRunnerPhasedCase resumed)
                {
                    int resumedPhase = InRun.NextPhase;
                    RimRunnerIsolatedMap.Resume(context, testCase.Id,
                        lastInSlot: InRun.NextPhase + 1 >= resumed.PhaseCount && !IsolatedWorkAfterCurrent(),
                        beforeLoadTick: () =>
                        {
                            if (resumesScenario)
                            {
                                RimRunnerScenarioManager.ResumeAll(context);
                            }

                            testCase.InvokeBeforeLoadTick(context, resumedPhase);
                        });
                    slotStartTick = Find.TickManager?.TicksGame ?? -1;
                    InRun.CurrentSlotStartTick = slotStartTick;
                }
                else
                {
                    if (resumesScenario)
                    {
                        RimRunnerScenarioManager.ResumeAll(context);
                    }

                    if (testCase.SitedOnIsolatedMap && RimRunnerCaseBody.WritesBeforeLoadTick(testCase.GetType()))
                    {
                        InRun.CurrentEntry!.degradedNotes.Add("实验开关 " + RimRunnerExperiments.ForceMainMap
                            + " 把本条压回主图，读档补推那一拍已在 vanilla 载档收尾里推过，BeforeLoadTick 未调");
                    }
                }

                context.FingerprintContinuationPawnLeaks(
                    InRun.ScenarioPawnRoster);
            }

            int phaseStartTick = Find.TickManager?.TicksGame ?? -1;
            run.PhaseStartTick = phaseStartTick;
            InRun.CurrentPhaseStartTick = phaseStartTick;
            phaseStartMapTick = context.Map != null ? RimRunnerMapTicks.Of(context.Map) : -1;
            context.BeginPhaseBaseline();
            context.ArmForeignDamage(InRun.NextPhase == 0);
            if (RimRunnerRunWorldTally.ShouldCompare(testCase))
            {
                InRun.WorldTally.NoteEligible(testCase.Id);
            }

            ArmMainMapRestore(context, testCase,
                testCase is not IRimRunnerPhasedCase phased || InRun.NextPhase + 1 >= phased.PhaseCount);
            RimRunnerFootprintProbe.Arm(testCase.Id, context.Map);
            if (testCase.OnIsolatedMap)
            {
                RimRunnerCurrentMapProbe.Arm(testCase);
                RimRunnerFrozenMapSpawnProbe.Arm(testCase);
            }
            if (testCase is IRimRunnerPhasedCase)
            {
                bool overBudgetRetry = false;
                if (InRun.NextPhase > 0 && context.TryReadCheckpointState(
                        RimRunnerContext.PhaseEndTickStateKey, out string? previousEnd))
                {
                    int drift = phaseStartTick - int.Parse(previousEnd, CultureInfo.InvariantCulture);
                    int mapDrift = phaseStartMapTick >= 0 && context.TryReadCheckpointState(
                        RimRunnerContext.PhaseEndMapTickStateKey, out string? previousMapEnd)
                        ? phaseStartMapTick - int.Parse(previousMapEnd, CultureInfo.InvariantCulture)
                        : drift;
                    int clockBudget = RimRunnerCheckpointPlanner.DeclaredClockBudget(testCase) ?? int.MaxValue;
                    int tickBudget = RimRunnerCheckpointPlanner.DeclaredTickBudget(testCase) ?? int.MaxValue;
                    List<string> batchMembers = CheckpointBatchMembersOf(testCase.Id).ToList();
                    string? overBudget = RimRunnerCheckpointPlanner.OverBudget(testCase.Id,
                        mapDrift, tickBudget, drift, clockBudget, batchMembers);
                    if (overBudget != null && InRun.CheckpointRetried.Add(testCase.Id))
                    {
                        overBudgetRetry = true;
                        InRun.PendingOverBudget = overBudget;
                        InRun.PendingOverBudgetRetry = RimRunnerCheckpointPlanner.OverBudgetRetried(testCase.Id,
                            mapDrift, tickBudget, drift, clockBudget, batchMembers);
                        InRun.CurrentEntry!.endedByClaimedFault = true;
                    }
                    else
                    {
                        InRun.CurrentEntry!.trippedCheckpointGate |= overBudget != null;
                        RimRunnerFault.Require(overBudget == null, overBudget);
                    }
                }

                if (!overBudgetRetry)
                {
                    testCase.InvokePhase(context, InRun.NextPhase);
                    string? overDeclared = RimRunnerCheckpointPlanner.OverDeclared(testCase.Id, InRun.NextPhase,
                        (Find.TickManager?.TicksGame ?? slotStartTick) - slotStartTick,
                        RimRunnerCheckpointPlanner.DeclaredTicks(testCase));
                    InRun.CurrentEntry!.trippedCheckpointGate |= overDeclared != null;
                    RimRunnerFault.Require(overDeclared == null, overDeclared);
                }
            }
            else
            {
                if (testCase.WritesSteps)
                {
                    run.Stepper = new RimRunnerStepper(testCase.InvokeSteps(context),
                        new RimRunnerCaseStepHost(context, testCase.Id, realFrame: false), "用例 " + testCase.Id);
                    run.Stepper.Resume();
                }
                else
                {
                    testCase.InvokeRun(context);
                }
            }
        }
        catch (Exception exception)
        {
            threw = true;
            AppendCurrentException(exception);
        }

        if (run.Stepper is { Done: false } && !threw)
        {
            SuspendPhase(run);
            return false;
        }

        return CompletePhase(run);
    }

    private static bool CompletePhase(PhaseRun run)
    {
        RimRunnerTestCase testCase = run.TestCase;
        RimRunnerContext context = run.Context;
        RimRunnerLogCapture capture = run.Capture;
        Stopwatch timer = run.Timer;
        if (run.Stepper != null)
        {
            foreach (Exception fault in run.Stepper.Dispose())
            {
                AppendCurrentException(fault);
            }

            run.Stepper = null;
        }

        AppendUnyieldedOps();

        AddQuiesceFault(context.Current!, run.QuiescedAtStart);

        RimRunnerFootprintProbe.Disarm();
        RimRunnerCurrentMapProbe.Disarm();
        RimRunnerFrozenMapSpawnProbe.Disarm();
        RimRunnerCameraFixture.EndPhase();
        RecordPhaseTicks(run.PhaseStartTick);
        InRun.LastPhaseEndTick = Find.TickManager?.TicksGame ?? -1;
        InRun.LastPhaseEndMapTick = context.Map != null ? RimRunnerMapTicks.Of(context.Map) : -1;
        if (testCase is IRimRunnerPhasedCase)
        {
            context.SnapshotRegistered();
        }

        List<RimRunnerScreenshotRequest> requests =
            context is RimRunnerReadoutContext readoutContext ? [.. readoutContext.ScreenshotRequests] : [];
        if (requests.Count == 0 && CurrentEntryHasError()
            && testCase.Kind == RimRunnerKind.Readout
            && RimRunnerContractGates.CallsScreenshot(testCase.Id)
            && !RimRunnerTextureTier.IsPlaceholder)
        {
            requests.Add(new RimRunnerScreenshotRequest
            {
                Caption = "用例执行出错，此为全屏现场图（定位用）",
                Crop = RimRunnerCrop.Full.Spec,
                Label = "failure-phase-" + InRun.NextPhase
            });
        }

        if (requests.Count > 0)
        {
            InRun.PendingCase = new PendingCaseFinalization
            {
                Context = context,
                Timer = timer,
                LogCapture = capture,
                Requests = requests
            };
            if (!AdvanceScreenshotQueue())
            {
                PublishDiagnosticSnapshot();
                return false;
            }

            RimRunnerScreenshotDriver.ReleaseFocus();
            InRun.PendingCase = null;
        }

        FinalizePhase(context, timer, capture);
        return true;
    }

    private const int SuspendedPhaseLimitFrames = 1800;

    private static void SuspendPhase(PhaseRun run)
    {
        run.SuspendTick = Find.TickManager.TicksGame;
        InRun.SuspendedPhase = run;
        PublishDiagnosticSnapshot();
    }

    private static void AdvanceSuspendedPhase()
    {
        PhaseRun run = InRun.SuspendedPhase!;
        try
        {
            bool stop = SuspendedPhaseStops(run);
            if (!stop)
            {
                try
                {
                    run.Stepper!.Frame();
                }
                catch (Exception exception)
                {
                    AppendCurrentException(exception);
                    stop = true;
                }
            }

            if (!stop && !run.Stepper!.Done)
            {
                run.SuspendTick = Find.TickManager.TicksGame;
                PublishDiagnosticSnapshot();
                return;
            }

            InRun.SuspendedPhase = null;
            if (!CompletePhase(run))
            {
                return;
            }

            if (AdvanceAfterCurrentPhase(run.TestCase))
            {
                ContinueRun();
            }
        }
        catch (Exception exception)
        {
            InRun.SuspendedPhase = null;
            if (continuation != null)
            {
                AddBatchEngineFault(continuation.Report, "运行级异常", exception);
                CompleteContinuation();
            }
        }
    }

    private static bool SuspendedPhaseStops(PhaseRun run)
    {
        int moved = Find.TickManager.TicksGame - run.SuspendTick;
        if (moved != 0)
        {
            InRun.CurrentEntry!.engineFaults.Add(RimRunnerLog.Prefix + " 用例 " + run.TestCase.Id + " 让出帧期间游戏推进 "
                                                 + moved + " tick（游戏速度 " + Find.TickManager.CurTimeSpeed + "）");
            return true;
        }

        if (RimRunnerSustainedErrorBreaker.Tripped)
        {
            return true;
        }

        if (run.Stepper!.Frames >= SuspendedPhaseLimitFrames)
        {
            AppendCurrentException(RimRunnerLog.Prefix + " 用例 " + run.TestCase.Id + " 让出帧超过 " + SuspendedPhaseLimitFrames
                                   + " 帧（未声明 IRimRunnerRealFrameCase 的用例每个 phase 的上限）");
            return true;
        }

        return false;
    }

    private static void AbortSuspendedPhase(string reason)
    {
        PhaseRun? run = continuation?.SuspendedPhase;
        if (run == null)
        {
            return;
        }

        InRun.SuspendedPhase = null;
        RimRunnerResultEntry entry = InRun.CurrentEntry!;
        entry.engineFaults.Add(reason);
        foreach (Exception fault in run.Stepper?.Dispose() ?? [])
        {
            entry.engineFaults.Add("中止让出帧的正文时释放失败：" + fault);
        }

        RimRunnerOp.TakeUnyielded();
        RimRunnerFootprintProbe.Disarm();
        RimRunnerCurrentMapProbe.Disarm();
        RimRunnerFrozenMapSpawnProbe.Disarm();
        RimRunnerCameraFixture.EndPhase();
        try
        {
            run.Context.EndCase();
        }
        catch (Exception cleanupException)
        {
            entry.cleanupErrors.Add("中止让出帧的正文时 Context.EndCase 失败：" + cleanupException);
        }
        finally
        {
            NoteMainMapRestore(run.Context);
            CloseCaseWindow(entry, run.Capture);
            run.Timer.Stop();
            entry.durationMs += run.Timer.ElapsedMilliseconds;
            InRun.DurationMs += run.Timer.ElapsedMilliseconds;
            CloseBatchMainMapRestore(run.Context);
        }

        FinishCurrentCase();
    }

    private static bool AdvanceScreenshotQueue()
    {
        PendingCaseFinalization pending = InRun.PendingCase!;
        while (pending.Index < pending.Requests.Count)
        {
            RimRunnerScreenshotRequest request = pending.Requests[pending.Index];
            RimRunnerScreenshotArtifact? artifact = null;
            string? error = FocusGoneError(request.Crop);
            try
            {
                if (error == null && RimRunnerScreenshotDriver.TryQueueFocused(
                        request.Label,
                        InRun.CurrentCaseId,
                        request,
                        out artifact,
                        out error,
                        out _))
                {
                    pending.ArtifactId = artifact.Id;
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = "截图准备或排队失败："
                        + exception.GetType().Name + ": " + exception.Message;
            }

            InRun.CurrentEntry!.artifacts.Add(new RimRunnerArtifactEntry
            {
                kind = "screenshot",
                artifactId = artifact?.Id,
                state = "error",
                label = request.Label,
                error = error,
                runId = InRun.RunId,
                command = InRun.Command,
                caseId = InRun.CurrentCaseId,
                targetId = request.Crop.Things.Length > 0
                    ? string.Join(",", request.Crop.Things.Select(thing => thing.ThingID))
                    : null,
                gameTick = Find.TickManager?.TicksGame ?? -1,
                frame = RimRunnerFrame.Current,
                completedFrame = RimRunnerFrame.Current,
                cleanupFrame = RimRunnerFrame.Current
            });
            InRun.CurrentEntry.screenshots.Add(new RimRunnerScreenshotEntry
            {
                caption = request.Caption,
                artifactId = artifact?.Id,
                error = error
            });
            pending.Index++;
        }

        return true;
    }

    internal static string? FocusGoneError(RimRunnerCropSpec crop)
    {
        foreach (Thing focus in crop.Things)
        {
            if (focus.Spawned)
            {
                continue;
            }

            Thing holder = focus.SpawnedParentOrMe;
            if (holder != null)
            {
                return focus.ThingID + " 在 phase 返回时不在图上、在 " + holder.ThingID + " 里，未出图";
            }

            return focus.Destroyed
                ? focus.ThingID + " 在 phase 返回时已销毁（早于 Defer 清理），未出图"
                : focus.ThingID + " 在 phase 返回时不在任何地图上，未出图";
        }

        return null;
    }

    private static void FinalizePhase(
        RimRunnerContext context,
        Stopwatch timer,
        RimRunnerLogCapture capture)
    {
        try
        {
            context.EndCase();
        }
        catch (Exception cleanupException)
        {
            InRun.CurrentEntry!.cleanupErrors.Add("收尾时 Context.EndCase 失败：" + cleanupException);
        }
        finally
        {
            NoteMainMapRestore(context);
        }

        CloseCaseWindow(InRun.CurrentEntry!, capture);
        timer.Stop();
        InRun.CurrentEntry!.durationMs += timer.ElapsedMilliseconds;
        InRun.DurationMs += timer.ElapsedMilliseconds;
        CloseBatchMainMapRestore(context);
    }

    private static void CloseBatchMainMapRestore(RimRunnerContext context)
    {
        context.CloseBatchMainMapRestore();
        NoteMainMapRestore(context);
    }

    private static void ArmMainMapRestore(RimRunnerContext context, RimRunnerTestCase testCase, bool lastPhase)
    {
        Map? segmentMap = context.Map == null || !RimRunnerIsolatedMap.IsPoolMap(context.Map)
            ? context.Map
            : testCase is IRimRunnerPhasedCase ? RimRunnerIsolatedMap.Frozen : null;
        bool joins = testCase.Level == RimRunnerLevel.World && segmentMap != null;
        if (joins && InRun.MapRestore == null && InRun.NextPhase == 0)
        {
            InRun.MapRestore = new RimRunnerMapRestore(segmentMap!, RimRunnerComponentScope.MapAndGlobal,
                compareWorld: testCase is not IRimRunnerRealFrameCase);
        }
        else if (joins && InRun.MapRestore == null)
        {
            InRun.WorldTally.NoteMissed([testCase.Id], "所在屏障批回载过基线档，本程不在比对段里");
        }

        if (InRun.MapRestore == null)
        {
            return;
        }

        if (joins && !InRun.MapRestore.Members.Contains(testCase.Id))
        {
            InRun.MapRestore.Members.Add(testCase.Id);
        }

        if (joins && context.TakeScenarioRestores() is { } restores)
        {
            InRun.MapRestore.ScenarioRestores.AppendFrom(restores);
        }

        bool lastOfBatch = InRun.BatchIndex < 0 || InRun.BatchIndex == InRun.ActiveBatch.Count - 1;
        if (lastOfBatch && InRun.PendingBatch.Count == 0)
        {
            context.ArmMainMapRestore(InRun.MapRestore, lastPhase);
        }
    }

    private static void NoteMainMapRestore(RimRunnerContext context)
    {
        if (context.MainMapRestoreClosed)
        {
            InRun.MapRestore = null;
        }

        if (context.MainMapUnrestorable)
        {
            InRun.MainMapNeedsReload = true;
        }
    }

    internal static void AddQuiesceFault(RimRunnerResultEntry entry, bool quiescedAtStart)
    {
        if (RimRunnerStorytellerQuiesce.StillInEffect)
        {
            return;
        }

        string state = RimRunnerStorytellerQuiesce.DescribeState();
        if (quiescedAtStart)
        {
            entry.sentinelFaults.Add(RimRunnerFindingEntry.Of(null, null, "用例 " + entry.id
                + " 开始执行时叙事者在停用态，收尾时已不在停用态（" + state + "）"));
        }
        else
        {
            entry.engineFaults.Add(RimRunnerLog.Prefix + " 用例 " + entry.id + " 开始执行前叙事者停用未生效（" + state + "）");
        }
    }

    private static void CloseCaseWindow(RimRunnerResultEntry entry, RimRunnerLogCapture capture)
    {
        RimRunnerSustainedErrorBreaker.Disarm();
        RimRunnerStubProbe.Close();
        RimRunnerMachineLoadProbe.Close(entry);
        SortFreshLogs(entry, RimRunnerLogMonitor.DiffSince(capture));
        string? trip = RimRunnerSustainedErrorBreaker.TakeTrip(out string? firstErrorStack);
        if (trip == null)
        {
            return;
        }

        entry.sentinelFaults.Add(RimRunnerFindingEntry.Of(null, null, trip));
        entry.pendingFingerprints.Add(RimRunnerFindingEntry.Of(RimRunnerFindingKey.SustainedErrorBreaker, null,
            "每帧持续错误级日志：本用例余下推进被熔断"
            + (string.IsNullOrEmpty(firstErrorStack) ? "" : "。首条错误级日志栈：\n" + firstErrorStack)));
    }

    internal static void AdvancePendingCaseFinalization()
    {
        if (continuation?.PendingCase == null)
        {
            return;
        }

        try
        {
            AdvancePendingCaseFinalizationCore();
        }
        catch (Exception exception)
        {
            FailPendingCaseFinalization(exception);
        }
    }

    private static void AdvancePendingCaseFinalizationCore()
    {
        PendingCaseFinalization? pending = continuation?.PendingCase;
        if (pending?.ArtifactId == null)
        {
            return;
        }

        RimRunnerScreenshotArtifact? artifact =
            RimRunnerScreenshotDriver.Get(pending.ArtifactId);
        if (artifact == null || artifact.State == "queued" || artifact.State == "capturing")
        {
            return;
        }

        int cleanupFrame = RimRunnerFrame.Current;
        InRun.CurrentEntry!.artifacts.Add(ToReportArtifact(artifact, cleanupFrame));
        RimRunnerScreenshotRequest completed = pending.Requests[pending.Index];
        InRun.CurrentEntry.screenshots.Add(new RimRunnerScreenshotEntry
        {
            caption = completed.Caption,
            path = artifact.State == "complete" ? artifact.Path : null,
            artifactId = artifact.Id,
            error = artifact.Error,
            note = artifact.FramingNote
        });
        pending.ArtifactId = null;
        pending.Index++;
        if (!AdvanceScreenshotQueue())
        {
            PublishDiagnosticSnapshot();
            return;
        }

        RimRunnerScreenshotDriver.ReleaseFocus();
        FinalizePhase(pending.Context, pending.Timer, pending.LogCapture);
        InRun.PendingCase = null;
        PublishDiagnosticSnapshot();

        RimRunnerTestCase? testCase = RimRunnerRegistry.All.FirstOrDefault(
            item => string.Equals(item.Id, InRun.CurrentCaseId, StringComparison.Ordinal));
        if (testCase == null)
        {
            AppendCurrentException(RimRunnerLog.Prefix + " 截图归结时用例名册中没有用例 "
                                                       + InRun.CurrentCaseId);
            FinishCurrentCase();
            ContinueRun();
            return;
        }

        if (AdvanceAfterCurrentPhase(testCase))
        {
            ContinueRun();
        }
    }

    private static void FailPendingCaseFinalization(Exception exception)
    {
        PendingCaseFinalization? pending = continuation?.PendingCase;
        if (pending == null || InRun.CurrentEntry == null)
        {
            return;
        }

        AppendCurrentException(RimRunnerLog.Prefix + " 截图结果待归结时发生异常：" + exception);
        ReleasePendingCase(pending, InRun.CurrentEntry, "截图结果待归结时发生异常：" + exception.GetType().Name + ": " + exception.Message);
        if (FinishFaultedCase())
        {
            ContinueRun();
        }
    }

    private static void AbortPendingCase(string reason)
    {
        PendingCaseFinalization? pending = continuation?.PendingCase;
        if (pending == null || InRun.CurrentEntry == null)
        {
            return;
        }

        InRun.CurrentEntry.engineFaults.Add(reason);
        ReleasePendingCase(pending, InRun.CurrentEntry, reason);
        FinishCurrentCase();
    }

    private static void ReleasePendingCase(PendingCaseFinalization pending, RimRunnerResultEntry entry, string cancelReason)
    {
        RimRunnerScreenshotArtifact? artifact = pending.ArtifactId == null
            ? null
            : RimRunnerScreenshotDriver.Cancel(pending.ArtifactId, cancelReason);
        int cleanupFrame = RimRunnerFrame.Current;
        if (artifact != null)
        {
            entry.artifacts.Add(
                ToReportArtifact(artifact, cleanupFrame));
        }

        RimRunnerScreenshotDriver.ReleaseFocus();
        try
        {
            pending.Context.EndCase();
        }
        catch (Exception cleanupException)
        {
            entry.cleanupErrors.Add(
                "截图结果待归结时 Context.EndCase 失败：" + cleanupException);
        }
        finally
        {
            NoteMainMapRestore(pending.Context);
            CloseCaseWindow(entry, pending.LogCapture);
            pending.Timer.Stop();
            entry.durationMs += pending.Timer.ElapsedMilliseconds;
            InRun.DurationMs += pending.Timer.ElapsedMilliseconds;
            CloseBatchMainMapRestore(pending.Context);
            InRun.PendingCase = null;
        }
    }

    private static RimRunnerArtifactEntry ToReportArtifact(
        RimRunnerScreenshotArtifact artifact, int cleanupFrame)
    {
        return new RimRunnerArtifactEntry
        {
            kind = "screenshot",
            artifactId = artifact.Id,
            state = artifact.State,
            label = artifact.Label,
            fileName = artifact.FileName,
            path = artifact.Path,
            error = artifact.Error,
            runId = artifact.RunId,
            command = artifact.Command,
            caseId = artifact.CaseId,
            targetId = artifact.TargetId,
            gameTick = artifact.GameTick,
            frame = artifact.Frame,
            completedFrame = artifact.CompletedFrame,
            cleanupFrame = cleanupFrame,
            width = artifact.Width,
            height = artifact.Height,
            screenWidth = artifact.ScreenWidth,
            screenHeight = artifact.ScreenHeight,
            uiScale = artifact.UiScale,
            mapId = artifact.MapId,
            cameraPosition = artifact.CameraPosition,
            cameraZoom = artifact.CameraZoom
        };
    }

    private static void RecordPhaseTicks(int phaseStartTick)
    {
        if (phaseStartTick < 0 || Find.TickManager == null)
        {
            return;
        }

        int phaseEndTick = Find.TickManager.TicksGame;
        if (phaseEndTick <= phaseStartTick)
        {
            return;
        }

        InRun.CurrentEntry!.ticksAdvanced += phaseEndTick - phaseStartTick;
    }

    private static bool SaveBatchCheckpoint()
    {
        Stopwatch timer = Stopwatch.StartNew();
        RimRunnerLogCapture capture =
            RimRunnerLogMonitor.Snapshot(includeWarnings: true, owner: "checkpoint 存档");
        string? failure = null;
        try
        {
            string path = GenFilePaths.FilePathForSavedGame(CheckpointSaveName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            RimRunnerDanglingRefProbe.Arm();
            RimRunnerHandoffCompressionGuard.Arm(InRun.ActiveBatch.Select(member => member.CheckpointState));
            GameDataSaveLoader.SaveGame(CheckpointSaveName);
            if (!File.Exists(path))
            {
                throw new IOException("checkpoint 保存调用返回后文件仍不存在：" + path);
            }
        }
        catch (Exception exception)
        {
            failure = RimRunnerLog.Prefix + " checkpoint 保存失败：" + exception;
        }
        finally
        {
            RimRunnerHandoffCompressionGuard.Disarm();
            RimRunnerDanglingRefProbe.DisarmAndLog();
            List<RimRunnerLogLine> barrierErrors = RimRunnerLogMonitor.DiffSince(capture);
            timer.Stop();
            InRun.ActiveBatch[0].Entry.checkpointMs += timer.ElapsedMilliseconds;
            InRun.DurationMs += timer.ElapsedMilliseconds;
            foreach (CheckpointBatchMember member in InRun.ActiveBatch)
            {
                SortFreshLogs(member.Entry, barrierErrors, treatWarningsAsErrors: true);
                if (failure != null)
                {
                    AppendEntryException(member.Entry, failure);
                }
            }
        }

        return InRun.ActiveBatch.All(member => !EntryHasError(member.Entry));
    }

    private static bool checkpointLoadQueued;

    internal static bool TakeCheckpointLoadFlag()
    {
        bool queued = checkpointLoadQueued;
        checkpointLoadQueued = false;
        return queued;
    }

    private static bool RequestLoad(string saveName)
    {
        string command = InRun.Command;
        string runId = InRun.RunId;
        if (!RimRunnerGameComponent.RequestContinuationAfterLoad(command, runId))
        {
            AddBatchEngineFault(InRun.Report, "无法从 Running 转入续段 Loading");
            CompleteContinuation();
            return false;
        }

        bool checkpoint = string.Equals(saveName, CheckpointSaveName, StringComparison.Ordinal);
        try
        {
            OpenLoadWindow(checkpoint ? LoadWindowSource.Checkpoint : LoadWindowSource.Baseline);
            checkpointLoadQueued = checkpoint;
            RimRunnerGameComponent.QueueLoadGameAfterCurrentUpdate(
                saveName,
                exception =>
                {
                    RimRunnerGameComponent.ClearAutoRunRequest();
                    WriteTerminalError(command, runId, "续段载档调用抛出异常", exception);
                });
            InRun.ReloadTimer.Restart();
            InRun.ReloadCount++;
            return true;
        }
        catch (Exception exception)
        {
            RimRunnerGameComponent.ClearAutoRunRequest();
            WriteTerminalError(command, runId, "续段载档排队抛出异常", exception);
            return false;
        }
    }

    private static void DropScenarioRestores(RimRunnerMapRestore? segment, string when)
    {
        if (segment == null || segment.ScenarioRestores.PendingCount == 0)
        {
            return;
        }

        foreach (string error in segment.ScenarioRestores.Cleanup())
        {
            RimRunnerEngineLog.Warn(segment.BatchLabel + when + "执行装配还原失败：" + error);
        }

        if (segment.ScenarioRestores.EndedByClaimedFault)
        {
            RimRunnerEngineLog.Warn(segment.BatchLabel + when + "执行装配还原时有一条以认领故障中止");
        }
    }

    private static void RequestBaselineLoad(bool completeAfterLoad)
    {
        DropScenarioRestores(InRun.MapRestore, "回载基线档前");
        string? baseline = RimRunnerAutoTestSave.ResolveNameOrNull();
        if (baseline == null)
        {
            AddBatchEngineFault(InRun.Report,
                "须回载基线档，但没有自动实例的基线档");
            CompleteContinuation();
            return;
        }

        InRun.AwaitingBaselineLoad = true;
        InRun.CompleteAfterBaselineLoad = completeAfterLoad;
        RequestLoad(baseline);
    }

    private static bool CurrentEntryHasError()
    {
        return EntryHasError(InRun.CurrentEntry!);
    }

    private static bool EntryHasError(RimRunnerResultEntry entry)
    {
        return entry.exception != null || entry.cleanupErrors.Count > 0
                                       || entry.newErrors.Count > 0;
    }

    private static bool RanToEnd(RimRunnerResultEntry entry)
    {
        return !EntryHasError(entry) && !entry.endedByClaimedFault;
    }

    private static void SortFreshLogs(RimRunnerResultEntry entry, List<RimRunnerLogLine> fresh,
        bool treatWarningsAsErrors = false)
    {
        if (entry == null || fresh == null || fresh.Count == 0)
        {
            return;
        }

        List<RimRunnerAcknowledgeLogAttribute> acks = RimRunnerLogAcks.Effective(LogAcksOf(entry.id));

        List<RimRunnerSentinel.Hit> sentinelHits = [];
        List<RimRunnerSentinel.Hit> sentinelWarningHits = [];
        foreach (RimRunnerLogLine entryLine in fresh)
        {
            if (entryLine.EngineFault)
            {
                entry.engineFaults.Add(entryLine.Text);
                continue;
            }

            if (entryLine.Sentinel != null)
            {
                (entryLine.Sentinel.Key != null ? sentinelWarningHits : sentinelHits).Add(entryLine.Sentinel);
                continue;
            }

            string line = entryLine.Text;

            bool isWarning = line.StartsWith(RimRunnerLogMonitor.WarningPrefix, StringComparison.Ordinal);
            if (isWarning && !(treatWarningsAsErrors && IsSaveIntegrityWarning(line)))
            {
                if (entryLine.ClaimedByAssertion)
                {
                    entry.expectedLogs.Add(line);
                }
                else if (!RimRunnerLogAcks.TryClaim(entry, acks, RimRunnerLogLevel.Warning, line))
                {
                    if (treatWarningsAsErrors && !IsBenignSaveWindowWarning(line))
                    {
                        AddRuntimeWarning(entry, RimRunnerWarningKey.SaveWindowWarning, DescribeSaveWindowWarning(line));
                    }
                    else
                    {
                        entry.newWarnings.Add(line);
                    }
                }

                continue;
            }

            if (entryLine.ClaimedByAssertion)
            {
                entry.expectedLogs.Add(line);
                continue;
            }

            if (RimRunnerLogAcks.TryClaim(entry, acks, RimRunnerLogLevel.Error, line, entryLine.Stack))
            {
                continue;
            }

            string? foreignSource = RimRunnerForeignAttribution.Resolve(line, line);
            if (foreignSource != null)
            {
                entry.foreignErrors.Add(foreignSource + "：" + line);
                continue;
            }

            entry.newErrors.Add(new RimRunnerErrorEntry { text = line, stack = entryLine.Stack });
        }

        entry.sentinelFaults.AddRange(sentinelHits.Select(hit => RimRunnerFindingEntry.Of(null, hit.Subject, hit.Reason)));
        foreach (KeyValuePair<string, string> warning in RimRunnerSentinel.GroupWarnings(sentinelWarningHits))
        {
            AddRuntimeWarning(entry, warning.Key, warning.Value);
        }
    }

    private static readonly string[] SaveIntegrityWarnings =
    [
        "but is not deep-saved",
        "DebugLoadIDsSavingErrorsChecker error",
        " was already deepsaved at ",
        "Trying to save reference to a discarded thing",
        "Deep-saving destroyed thing",
        "Deep-saving discarded thing",
        "Tried to remove used tale",
        "Tried to discard ",
        "Could not resolve reference to ",
        "Trying to load reference of an object of type",
        "RegisterforPostLoadInit",
    ];

    internal static bool IsSaveIntegrityWarning(string? line)
    {
        return line != null
               && SaveIntegrityWarnings.Any(text => line.IndexOf(text, StringComparison.Ordinal) >= 0);
    }

    internal static bool IsBenignSaveWindowWarning(string? line)
    {
        return line != null
               && line.IndexOf("is from version ", StringComparison.Ordinal) >= 0
               && line.IndexOf(", we are running version ", StringComparison.Ordinal) >= 0;
    }

    private static string DescribeSaveWindowWarning(string line)
    {
        return "存读档窗里的警告级日志不属已知存档完整性族：" + line[RimRunnerLogMonitor.WarningPrefix.Length..];
    }

    internal static void AddRuntimeWarning(RimRunnerResultEntry entry, string key, string text, string? origin = null)
    {
        RimRunnerTestCase? testCase = RimRunnerRegistry.All.FirstOrDefault(
            item => string.Equals(item.Id, entry.id, StringComparison.Ordinal));
        string? reason = testCase == null ? null : RimRunnerWarnings.Match(RimRunnerWarnings.Acks(testCase.GetType()), key, origin);

        List<RimRunnerWarningEntry> target = reason == null ? entry.warnings : entry.warningsAcked;
        if (target.Any(existing => existing.key == key && existing.text == text))
        {
            return;
        }

        target.Add(new RimRunnerWarningEntry { subject = entry.id, key = key, text = text, reason = reason, origin = origin });
    }

    private static void AddBatchWarning(string key, string text, string? origin = null, string? anchor = null, string? scope = null)
    {
        RimRunnerReport report = InRun.Report;
        string? reason = anchor != null
            ? InRun.ChainAcks.FirstOrDefault(ack => ack.Matches(key, origin, anchor))?.Reason
            : InRun.BatchAcks.TryGetValue(key, out string? acked) ? acked : null;
        List<RimRunnerWarningEntry> target = reason == null ? report.warnings : report.warningsAcked;
        if (target.Any(existing => existing.subject == null && existing.key == key && existing.text == text))
        {
            return;
        }

        target.Add(new RimRunnerWarningEntry
            { key = key, text = text, reason = reason, origin = origin, anchor = anchor, scope = scope });
    }

    private static void DrainProbeHits(RimRunnerResultEntry entry)
    {
        foreach ((string line, string? origin) in RimRunnerCurrentMapProbe.Drain(entry.id))
            AddRuntimeWarning(entry, RimRunnerWarningKey.CurrentMapRead, line, origin);
        foreach (string line in RimRunnerCurrentMapProbe.DrainBlind(entry.id))
            AddRuntimeWarning(entry, RimRunnerWarningKey.CurrentMapProbeBlind, line);
        foreach ((string line, string? origin) in RimRunnerFrozenMapSpawnProbe.Drain(entry.id))
            AddRuntimeWarning(entry, RimRunnerWarningKey.FrozenMapSpawn, line, origin);
        foreach ((string line, string? origin) in RimRunnerLiveStorytellerScope.Drain(entry.id))
            AddRuntimeWarning(entry, RimRunnerWarningKey.StorytellerBlocked, line, origin);
        foreach ((string line, string? origin) in RimRunnerStubProbe.Drain(entry.id))
            AddRuntimeWarning(entry, RimRunnerWarningKey.UnityStub, line, origin);
    }

    private static IEnumerable<RimRunnerFinding> StartupAckFindings(IReadOnlyList<RimRunnerRunClaims.Ack> acks)
    {
        return acks.Where(ack => string.IsNullOrWhiteSpace(ack.Reason))
            .Select(ack => RimRunnerFinding.Warning(null, RimRunnerWarningKey.EmptyReason,
                "mod 名单的启动错误级日志许可「" + ack.Target + "」理由为空，这条许可不生效"));
    }

    private static List<RimRunnerAckEntry> SettleKeyAcks(RimRunnerReport report)
    {
        List<RimRunnerAckEntry> cells = [];
        List<RimRunnerResultEntry> entries = report.regressions.Concat(report.readouts).ToList();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (RimRunnerResultEntry entry in entries.Where(entry => seen.Add(entry.id)))
        {
            RimRunnerTestCase? testCase = RimRunnerRegistry.All.FirstOrDefault(
                item => string.Equals(item.Id, entry.id, StringComparison.Ordinal));
            if (testCase == null)
            {
                continue;
            }

            foreach ((string key, List<RimRunnerAck> acks) in RimRunnerWarnings.Acks(testCase.GetType()))
            {
                foreach (RimRunnerAck ack in acks)
                {
                    bool Counts(RimRunnerWarningEntry warning) =>
                        warning.key == key && (ack.Origin == null || RimRunnerOrigin.Comparer.Equals(ack.Origin, warning.origin));
                    int hits = report.warningsAcked.Count(item => item.subject == entry.id && Counts(item))
                               + entries.Where(item => item.id == entry.id).Sum(item => item.warningsAcked.Count(Counts));
                    cells.Add(new RimRunnerAckEntry
                    {
                        subject = entry.id, kind = RimRunnerAckEntry.KeyKind, target = key, origin = ack.Origin,
                        reason = ack.Reason.Trim(), hits = hits, tolerance = ack.Tolerance
                    });
                }
            }
        }

        return cells;
    }

    private static readonly Dictionary<Type, List<RimRunnerAcknowledgeLogAttribute>> logAcksByType =
        [];

    internal static List<RimRunnerAcknowledgeLogAttribute>? LogAcksOf(string? caseId)
    {
        Type? caseType = caseId == null
            ? null
            : RimRunnerRegistry.All.FirstOrDefault(
                item => string.Equals(item.Id, caseId, StringComparison.Ordinal))?.GetType();
        if (caseType == null)
        {
            return null;
        }

        if (!logAcksByType.TryGetValue(caseType, out List<RimRunnerAcknowledgeLogAttribute>? acks))
        {
            acks = RimRunnerLogAcks.Declared(caseType);
            logAcksByType[caseType] = acks;
        }

        return acks;
    }

    private static bool EmitsSamplesOf(string? caseId)
    {
        if (caseId == null)
        {
            return false;
        }

        return RimRunnerRegistry.All.FirstOrDefault(
            item => string.Equals(item.Id, caseId, StringComparison.Ordinal))?.DeclaresEmitsSamples
               ?? false;
    }

    internal static string? CurrentCaseId => continuation?.CurrentCaseId;

    private static void FinishCurrentCase()
    {
        RunStrandedCarriedCleanups(InRun.CurrentEntry!, InRun.CurrentCarriedCleanups);
        FinishEntry(InRun.CurrentEntry!, InRun.CurrentCheckpointState);
        DetachCurrentCase();
    }

    private static void RunStrandedCarriedCleanups(
        RimRunnerResultEntry entry, List<RimRunnerContext.CarriedCleanup>? carried)
    {
        entry.cleanupErrors.AddRange(
            RimRunnerContext.RunStrandedCarriedCleanups(carried, out bool endedByClaimedFault));
        entry.endedByClaimedFault |= endedByClaimedFault;
        foreach (string line in RimRunnerFixtureState.Release(entry, null))
        {
            AddRuntimeWarning(entry, RimRunnerWarningKey.FixtureResidue, line);
        }
    }

    private static void DetachCurrentCase()
    {
        InRun.CurrentCaseId = null;
        InRun.CurrentEntry = null;
        InRun.CurrentCheckpointState = null;
        InRun.CurrentCarriedCleanups = null;
        InRun.CurrentCarriedSpecTargets = null;
        InRun.NextPhase = 0;
        PublishDiagnosticSnapshot();
    }

    internal static string? DescribeOpenShardLeases(RimRunnerResultEntry entry)
    {
        List<string> open = [.. entry.shardLeases.Where(lease => !lease.Completed).Select(lease => lease.Key)];
        if (open.Count == 0) return null;
        return "用例执行到头时有 " + open.Count + " 格的租约未调用 Complete：" + string.Concat(open.Select(key => "\n  " + key));
    }

    internal static string? DescribeUntakenWholeCells(RimRunnerResultEntry entry, bool wholeShardable)
    {
        if (!wholeShardable) return null;
        if (!entry.wholeCellsEnumerated)
            return "可分片用例不分片执行到头未调用过 TryTakeShardCell，一格未取";
        if (entry.untakenWholeCells.Count == 0) return null;
        return "不分片时还有 " + entry.untakenWholeCells.Count + " 格未取："
               + string.Concat(entry.untakenWholeCells.Select(key => "\n  " + key));
    }

    private static bool WholeShardable(string? caseId) =>
        caseId != null
        && !(InRun.Shard != null && InRun.Shard.CaseId == caseId)
        && RimRunnerRegistry.All.FirstOrDefault(item => string.Equals(item.Id, caseId, StringComparison.Ordinal))
            is IRimRunnerShardable;

    private static void FinishEntry(RimRunnerResultEntry entry,
        Dictionary<string, string>? checkpointState = null)
    {
        if (RanToEnd(entry))
        {
            List<string> unconsumed = RimRunnerExpectationLedger.Unconsumed();
            if (unconsumed.Count > 0)
            {
                entry.AddAssert(new RimRunnerAssertEntry
                {
                    label = entry.id + "-ExpectationsConsumed",
                    expected = "0",
                    actual = unconsumed.Count.ToString(),
                    pass = false,
                    source = "期望表条目加载后未产出任何断言；只有末参为 FromTable / FromParam 的断言登记消费",
                    offending = unconsumed
                });
            }
        }

        RimRunnerLogAcks.Settle(entry, LogAcksOf(entry.id));
        foreach (string broad in RimRunnerLogAcks.BroadClaims(entry))
        {
            AddRuntimeWarning(entry, RimRunnerWarningKey.AckBroad, broad);
        }

        foreach (string fresh in RimRunnerSeenLogLedger.AnnotateAcks(entry))
        {
            AddRuntimeWarning(entry, RimRunnerWarningKey.AckBroad, fresh);
        }

        if (RanToEnd(entry)
            && EmitsSamplesOf(entry.id) && entry.samples.Count == 0
            && !(InRun.Shard != null && InRun.Shard.CaseId == entry.id && InRun.Shard.Info!.cells == 0))
        {
            entry.sentinelFaults.Add(RimRunnerFindingEntry.Of(null, null, "用例 " + entry.id
                + " 声明 EmitsSamples，本轮零条读数，读数库里该用例本轮无更新"));
        }

        if (RanToEnd(entry) && DescribeOpenShardLeases(entry) is string openLeases)
            AddRuntimeWarning(entry, RimRunnerWarningKey.ShardLeaseOpen, openLeases);

        if (RanToEnd(entry) && DescribeUntakenWholeCells(entry, WholeShardable(entry.id)) is string untaken)
            AddRuntimeWarning(entry, RimRunnerWarningKey.ShardCellsUntaken, untaken);

        List<string> between = [];
        RimRunnerWindowWatch.DrainStray(between);
        foreach (string line in between)
            AddBatchWarning(RimRunnerWarningKey.StrayWindow, line);
        between.Clear();
        RimRunnerStorytellerQuiesce.DrainBetween(between);
        foreach (string line in between)
            AddBatchWarning(RimRunnerWarningKey.StorytellerResidue, line);
        DrainProbeHits(entry);
        RimRunnerWindowWatch.DrainAmbient(InRun.Report.ambientWindows, InRun.Report.ambientWindowsFresh);

        RimRunnerEnvChecks.SettleByDeclaration(entry, InRun.KnownFailing);
        if (RimRunnerEnvChecks.ShouldPark(entry))
        {
            InRun.ParkedEnvChecks.Add(
                new KeyValuePair<RimRunnerResultEntry, Dictionary<string, string>?>(entry, checkpointState));
            return;
        }

        SettleEntry(entry, checkpointState);
    }

    private static void SettleEntry(RimRunnerResultEntry entry, Dictionary<string, string>? checkpointState)
    {
        if (entry.engineFaults.Count > 0)
        {
            entry.status = RimRunnerOutcomes.EngineFault;
            entry.errorSummary = "engineFaults[" + entry.engineFaults.Count + "]: " + FirstLine(entry.engineFaults[0]);
        }
        else if (entry.exception != null || entry.cleanupErrors.Count > 0 || entry.newErrors.Count > 0)
        {
            entry.status = RimRunnerOutcomes.UnclaimedFault;
            entry.errorSummary = entry.exception != null
                ? "exception: " + FirstLine(entry.exception)
                : entry.cleanupErrors.Count > 0
                    ? "cleanupErrors[" + entry.cleanupErrors.Count + "]: "
                      + FirstLine(entry.cleanupErrors[0])
                    : "newErrors[" + entry.newErrors.Count + "]: " + entry.newErrors[0].text;
        }
        else if (entry.sentinelFaults.Count > 0)
        {
            entry.status = RimRunnerOutcomes.SentinelFault;
            entry.errorSummary = "sentinelFaults[" + entry.sentinelFaults.Select(fault => (fault.key, fault.text)).Distinct().Count() + "]: "
                                 + entry.sentinelFaults[0].Line;
        }
        else if (!RimRunnerRegistry.CountsAsRegression(entry.id))
        {
            entry.status = VerdictlessStatus(entry);
        }
        else if (entry.asserts.Any(assertEntry => !assertEntry.pass))
        {
            entry.status = RimRunnerOutcomes.Fail;
        }
        else if (IsMissingConclusion(entry))
        {
            entry.status = RimRunnerOutcomes.NoAssertions;
        }
        else
        {
            entry.status = RimRunnerOutcomes.Pass;
        }

        PublishPendingFingerprints(entry);
        AppendBatchFingerprints(entry, checkpointState);
        IncrementStatus(InRun.Report, entry.status);
        RimRunnerPassRate.RecordAndAnnotate(entry);
        RimRunnerSeenLogLedger.Annotate(entry);
        InRun.Report.AddEntry(entry);
    }

    internal static List<RimRunnerTestCase> OrderForExecution(
        IEnumerable<RimRunnerTestCase> cases)
    {
        return cases == null
            ? []
            : cases.OrderBy(testCase => (int)testCase.Slot).ThenBy(testCase => (int)testCase.Level).ToList();
    }

    internal static List<List<RimRunnerCheckpointPlanner.Member>> PlanCheckpointBatches(
        List<RimRunnerTestCase> runnable)
    {
        List<RimRunnerTestCase> phased = runnable.Where(testCase => testCase is IRimRunnerPhasedCase).ToList();
        return PlanGroup(phased.Where(testCase => !testCase.OnIsolatedMap))
            .Concat(PlanGroup(phased.Where(testCase => testCase.OnIsolatedMap)))
            .ToList();
    }

    private static List<List<RimRunnerCheckpointPlanner.Member>> PlanGroup(IEnumerable<RimRunnerTestCase> group) =>
        RimRunnerCheckpointPlanner.Plan(group.Select(testCase => RimRunnerCheckpointPlanner.Describe(
            testCase, RimRunnerCheckpointTickLedger.Ring(testCase.Id))));

    internal static List<RimRunnerTestCase> ApplyCheckpointPlan(
        List<RimRunnerTestCase> runnable, List<List<RimRunnerCheckpointPlanner.Member>> plan)
    {
        Dictionary<string, RimRunnerTestCase> phased = runnable
            .Where(testCase => testCase is IRimRunnerPhasedCase)
            .ToDictionary(testCase => testCase.Id, StringComparer.Ordinal);
        return runnable
            .Where(testCase => testCase.Slot == RimRunnerExecutionSlot.Normal)
            .Concat(plan.SelectMany(batch => batch).Select(member => phased[member.Id]))
            .Concat(runnable.Where(testCase => testCase.Slot == RimRunnerExecutionSlot.IsolatedMap))
            .Concat(runnable.Where(testCase => testCase.Slot == RimRunnerExecutionSlot.RealFrame))
            .ToList();
    }

    internal sealed class ApplicabilitySplit
    {
        internal readonly List<RimRunnerTestCase> Runnable = [];
        internal readonly List<KeyValuePair<RimRunnerTestCase, string>> Inapplicable = [];
        internal readonly Dictionary<string, string> Faults = new(StringComparer.Ordinal);
    }

    internal static ApplicabilitySplit SplitByApplicability(List<RimRunnerTestCase> cases)
    {
        ApplicabilitySplit split = new();
        foreach (RimRunnerTestCase testCase in cases)
        {
            string? reason;
            try
            {
                reason = testCase.NotApplicableReason();
            }
            catch (Exception exception)
            {
                split.Faults[testCase.Id] = "NotApplicableReason() 抛出异常："
                                            + exception;
                split.Runnable.Add(testCase);
                continue;
            }

            if (reason == null)
            {
                split.Runnable.Add(testCase);
            }
            else if (reason.Trim().Length == 0)
            {
                split.Faults[testCase.Id] = "NotApplicableReason() 返回空串";
                split.Runnable.Add(testCase);
            }
            else
            {
                split.Inapplicable.Add(new KeyValuePair<RimRunnerTestCase, string>(testCase, reason));
            }
        }

        return split;
    }

    internal static void PublishPendingFingerprints(RimRunnerResultEntry entry)
    {
        if (RimRunnerContext.ShouldPublishFingerprints(entry.status))
        {
            entry.failureFingerprints.AddRange(entry.pendingFingerprints);
        }

        foreach (string what in entry.pendingResidue)
        {
            AddRuntimeWarning(entry, RimRunnerWarningKey.Residue, what);
        }

        entry.pendingFingerprints.Clear();
        entry.pendingResidue.Clear();
    }

    private static void AppendBatchFingerprints(RimRunnerResultEntry entry,
        Dictionary<string, string>? checkpointState)
    {
        AppendCheckpointDriftFingerprint(entry, checkpointState);

        if (entry.exception == null
            && entry.asserts.All(assertEntry => assertEntry.pass)
            && entry.newErrors.Count > 0
            && entry.newErrors[0].text.Contains("Tried to destroy non-destroyable thing"))
        {
            entry.failureFingerprints.Add(RimRunnerFindingEntry.Of(RimRunnerFindingKey.DestroyNonDestroyable, null,
                "断言全过，首条错误级日志是对 destroyable=false 的物调 Destroy()"));
        }
    }

    private static void AppendCheckpointDriftFingerprint(
        RimRunnerResultEntry entry, Dictionary<string, string>? checkpointState)
    {
        if (!RimRunnerContext.ShouldPublishFingerprints(entry.status)
            || checkpointState == null
            || InRun.CurrentPhaseStartTick < 0
            || !checkpointState.TryGetValue(
                RimRunnerContext.PhaseEndTickStateKey, out string? raw)
            || !IsMultiPhaseCase(entry.id))
        {
            return;
        }

        int previousPhaseEndTick = int.Parse(raw, CultureInfo.InvariantCulture);
        int drift = InRun.CurrentPhaseStartTick - previousPhaseEndTick;
        if (drift <= 0)
        {
            return;
        }

        bool onIsolatedMap = IsOnIsolatedMap(entry.id);
        RimRunnerTestCase? declared = RimRunnerRegistry.All.FirstOrDefault(item =>
            string.Equals(item.Id, entry.id, StringComparison.Ordinal));
        string pin = !onIsolatedMap
            ? "；被测量之外随时间走的过程（再生愈合、休眠销毁、衰减）在这段时间里同样在走"
            : declared != null && RimRunnerCheckpointPlanner.EffectiveBudget(declared) != int.MaxValue
                ? "；被测量之外按 TicksGame 到期的量在这段时间里同样在走"
                : "";
        entry.failureFingerprints.Add(RimRunnerFindingEntry.Of(RimRunnerFindingKey.CheckpointDrift, null, "多阶段用例：本 phase 起于 tick "
                                      + InRun.CurrentPhaseStartTick + "，比上一 phase 终点（tick "
                                      + previousPhaseEndTick + "）晚 " + drift + " tick（checkpoint 存于 tick "
                                      + InRun.CheckpointSaveTick + "）；同批 " + InRun.CheckpointBatchSize
                                      + " 个成员共同推进游戏时间，另有 1 tick 由读档收尾时 vanilla 补推"
                                      + (onIsolatedMap ? "、1 tick 由引擎领回停放的图时补推" : "") + "。"
                                      + "同批成员（phase 0 序）：" + string.Join("、", CheckpointBatchMembersOf(entry.id))
                                      + (onIsolatedMap
                                          ? "。本图在两 phase 之间冻结，只走领回补推的 1 拍、时钟差 " + drift
                                            + " tick"
                                          : "")
                                      + pin));
    }

    internal static string VerdictlessStatus(RimRunnerResultEntry entry)
    {
        RimRunnerAssertEntry? failed = entry.asserts.FirstOrDefault(assertEntry => !assertEntry.pass);
        if (failed != null)
        {
            entry.errorSummary = failed.label + ": " + failed.actual;
            return RimRunnerOutcomes.Incomplete;
        }

        RimRunnerScreenshotEntry? unshot = entry.screenshots.FirstOrDefault(item => item.error != null);
        if (unshot != null)
        {
            entry.errorSummary = "截图没拍成：" + unshot.caption + "；" + unshot.error;
            return RimRunnerOutcomes.Incomplete;
        }

        if (OutputCount(entry) == 0)
        {
            entry.errorSummary = entry.diagnostics.Count > 0
                ? "零产出：作者没交读数正文、截图、样本或诊断（引擎自落的观测条目不计）"
                : "零产出：读数正文、截图、样本、诊断都没有";
            return RimRunnerOutcomes.Incomplete;
        }

        return RimRunnerOutcomes.Done;
    }

    internal static int OutputCount(RimRunnerResultEntry entry) =>
        entry.readoutTexts.Count + entry.screenshots.Count(item => item.error == null)
        + entry.samples.Count + entry.diagnostics.Count(diagnostic => !diagnostic.engineAuthored);

    internal static bool IsMissingConclusion(RimRunnerResultEntry entry) =>
        entry.asserts.Count + entry.demotedAsserts == 0;

    private static void IncrementStatus(RimRunnerReport report, string status)
    {
        switch (status)
        {
            case RimRunnerOutcomes.Pass: report.pass++; break;
            case RimRunnerOutcomes.NoAssertions: report.noAssertions++; break;
            case RimRunnerOutcomes.Fail: report.fail++; break;
            case RimRunnerOutcomes.UnclaimedFault: report.unclaimedFault++; break;
            case RimRunnerOutcomes.SentinelFault: report.sentinelFault++; break;
            case RimRunnerOutcomes.EngineFault: report.engineFault++; break;
            case RimRunnerOutcomes.Done: report.done++; break;
            case RimRunnerOutcomes.Incomplete: report.incomplete++; break;
            default:
                throw new InvalidOperationException(
                    RimRunnerLog.Prefix + " 未知 status \"" + status
                    + "\"：只有 PASS/NO_ASSERTIONS/FAIL/UNCLAIMED_FAULT/SENTINEL_FAULT/ENGINE_FAULT/DONE/INCOMPLETE");
        }
    }

    private static void AppendCurrentException(string text)
    {
        AppendEntryException(InRun.CurrentEntry!, text);
    }

    private static void AppendCurrentException(Exception exception)
    {
        RecordCaseException(InRun.CurrentEntry!, exception);
    }

    internal static void RecordCaseException(RimRunnerResultEntry entry, Exception exception)
    {
        if (RimRunnerClaimedFault.Unwrap(exception) != null)
        {
            entry.endedByClaimedFault = true;
            return;
        }

        AppendEntryException(entry, exception.ToString());
    }

    private static void AppendEntryException(
        RimRunnerResultEntry entry, string text)
    {
        entry.exception = entry.exception == null ? text : entry.exception + "\n" + text;
    }

    private static void CompleteContinuation()
    {
        RunContinuation? completed = continuation;
        if (completed == null)
        {
            return;
        }

        if (completed.RealFrameExecution != null)
        {
            AbortActiveRealFrameExecution("批次在真实帧用例走完前提前结束");
        }

        if (completed.SuspendedPhase != null)
        {
            AbortSuspendedPhase("批次在用例正文走完前提前结束");
        }

        if (completed.PendingCase != null)
        {
            AbortPendingCase("批次在用例截图归结前提前结束");
        }

        FlushUnfinishedBatch(completed);
        foreach (KeyValuePair<RimRunnerResultEntry, Dictionary<string, string>?> parked in completed.ParkedEnvChecks)
        {
            RimRunnerEnvChecks.SettleByUsage(parked.Key);
            SettleEntry(parked.Key, parked.Value);
        }

        completed.ParkedEnvChecks.Clear();
        completed.Report.envChecksClean = RimRunnerEnvChecks.Clean(completed.Selector, completed.Report);
        DropScenarioRestores(completed.MapRestore, "批结束时未关闭");
        TearDownIsolatedMapsAtRunEnd(completed);
        SweepDanglingRefsAtRunEnd(completed);
        DrainPendingReloadErrors(completed.Report, completed.BatchAcks);
        RimRunnerLoadWindowProbe.Disarm();
        RimRunnerLoadWindowProbe.DisarmAckedLine();
        if (completed.MapRestore != null)
        {
            completed.WorldTally.NoteMissed(completed.MapRestore.Members, "批结束时所在段未关闭");
        }

        foreach ((bool warn, string text) in RimRunnerTraceTier.On ? completed.WorldTally.Lines() : [])
        {
            if (warn)
            {
                RimRunnerEngineLog.Warn(text);
            }
            else
            {
                RimRunnerEngineLog.Info(text);
            }
        }
        completed.Report.durationMs = completed.DurationMs;
        completed.Report.reloadMs = completed.ReloadMs;
        completed.Report.reloadCount = completed.ReloadCount;
        completed.Report.faultReloads = completed.FaultReloads;
        completed.Report.mapReloads = completed.MapReloads;
        completed.Report.danglingProbeArms = RimRunnerDanglingRefProbe.ArmCount;
        completed.Report.footprints.AddRange(RimRunnerFootprintProbe.Lines());
        completed.Report.mainMap.AddRange(RimRunnerMapSiteLedger.MainMapLines());
        completed.Report.forcedMainMap.AddRange(RimRunnerMapSiteLedger.ForcedLines());
        completed.Report.componentFirstFills = RimRunnerSaveProbe.Armed ? RimRunnerSaveProbe.Fills() : null;
        completed.Report.experiments = RimRunnerExperiments.Active;
        completed.Report.compatOff = RimRunnerCompatOff.Active;
        foreach (string line in RimRunnerLiveStorytellerScope.DrainRest())
            AddBatchWarning(RimRunnerWarningKey.StorytellerBlocked, line);
        foreach (string line in RimRunnerStubProbe.DrainRest())
            AddBatchWarning(RimRunnerWarningKey.UnityStub, line);
        foreach ((string line, string origin, string anchor, string scope) in RimRunnerCurrentMapProbe.DrainEnvChains())
            AddBatchWarning(RimRunnerWarningKey.CurrentMapRead, line, origin, anchor, scope);
        completed.Report.breakerObservations = RimRunnerSustainedErrorBreaker.RunObservations;
        completed.Report.breakerTrips = RimRunnerSustainedErrorBreaker.RunTrips;
        completed.Report.breakerPumpThreshold = RimRunnerSustainedErrorBreaker.PumpThreshold;
        completed.Report.breakerErrorThreshold = RimRunnerSustainedErrorBreaker.ErrorThreshold;
        completed.Report.memoryEnd = RimRunnerMemoryProbe.Snapshot();
        try
        {
            DeleteCheckpoint(completed.Report);
            RimRunnerPassRate.Flush();
            RimRunnerSeenLogLedger.Flush();
            RimRunnerCheckpointTickLedger.Flush();
            RimRunnerHolderShapes.Flush();
            completed.Report.contractGatesReady = RimRunnerContractGates.Ready;
            RimRunnerLogMonitor.DrainInto(completed.Report, completed.BatchAcks);
            completed.Report.acks.AddRange(SettleKeyAcks(completed.Report));
            completed.Report.acks.AddRange(completed.BatchAcks.Select(ack => new RimRunnerAckEntry
            {
                kind = RimRunnerAckEntry.KeyKind, target = ack.Key, reason = ack.Value.Trim(),
                hits = completed.Report.warningsAcked.Count(item => item.subject == null && item.key == ack.Key && item.anchor == null)
            }));
            completed.Report.acks.AddRange(completed.ChainAcks.Select(ack => new RimRunnerAckEntry
            {
                kind = RimRunnerAckEntry.KeyKind, target = ack.Key, origin = ack.Origin, anchor = ack.Anchor,
                reason = ack.Reason.Trim(),
                hits = completed.Report.warningsAcked.Count(item => item.subject == null && ack.Matches(item.key, item.origin, item.anchor))
            }));
            completed.Report.acks.AddRange(completed.KnownFailing.Select(ack => new RimRunnerAckEntry
            {
                kind = RimRunnerAckEntry.KnownFailingKind, target = ack.Key, reason = ack.Value,
                hits = completed.Report.regressions.Concat(completed.Report.readouts)
                    .Where(entry => entry.id == ack.Key)
                    .Sum(entry => entry.warnings.Concat(entry.warningsAcked)
                        .Count(warning => warning.key == RimRunnerWarningKey.EnvCheckKnownFailing))
            }));
            AddHealthGateFaults(completed.Report);
            string[] ranToEnd = [.. completed.Report.regressions.Concat(completed.Report.readouts)
                .Where(entry => RanToEnd(entry) && entry.engineFaults.Count == 0).Select(entry => entry.id)];
            RimRunnerAckMisses.Annotate(completed.Report, ranToEnd);
            RimRunnerSeenLogLedger.AnnotateWarnings(completed.Report, ranToEnd);
            RimRunnerReportWriter.Write(completed.Report, completed.Selector);
        }
        finally
        {
            continuation = null;
            PublishDiagnosticSnapshot();
            RimRunnerLiveServer.ClearRunPulse();
            RimRunnerRunCoordinator.Complete(completed.Command, completed.RunId);

            RimRunnerIlScan.ClearCache();
        }
    }

    private static void AddHealthGateFaults(RimRunnerReport report)
    {
        if (!report.contractGatesReady)
        {
            AddBatchEngineFault(report, "[契约闸] 未扫描，Level/Kind 申报未经核对");
        }

        if (report.danglingProbeArms == 0)
        {
            AddBatchEngineFault(report, "[引用探针] 本轮启用 0 次，悬空引用未经检查");
        }
    }

    private static void FlushUnfinishedBatch(RunContinuation completed)
    {
        List<CheckpointBatchMember> unfinished = [];
        for (int index = Math.Max(completed.BatchIndex, 0);
             index < completed.ActiveBatch.Count;
             index++)
        {
            unfinished.Add(completed.ActiveBatch[index]);
        }

        unfinished.AddRange(completed.PendingBatch);
        foreach (CheckpointBatchMember member in unfinished)
        {
            if (member.Entry == completed.CurrentEntry
                || completed.Report.ContainsEntry(member.Entry))
            {
                continue;
            }

            member.Entry.engineFaults.Add("checkpoint 屏障批未执行完，批次即归结，phase "
                                          + member.NextPhase + " 未执行");
            RunStrandedCarriedCleanups(member.Entry, member.CarriedCleanups);
            FinishEntry(member.Entry);
        }

        completed.ActiveBatch.Clear();
        completed.PendingBatch.Clear();
        completed.BatchIndex = -1;
    }

    private static void TearDownIsolatedMapsAtRunEnd(RunContinuation completed)
    {
        if (RimRunnerIsolatedMap.PooledCount > 0 || RimRunnerIsolatedMap.Frozen != null)
        {
            try
            {
                RimRunnerIsolatedMap.TearDownSlot();
            }
            catch (Exception exception)
            {
                AddBatchEngineFault(completed.Report, "独立小图槽尾兜底拆除失败：" + exception);
            }
        }

        try
        {
            RimRunnerParallelSite.TearDownAll();
        }
        catch (Exception exception)
        {
            AddBatchEngineFault(completed.Report, "替补层拆除失败：" + exception);
        }
    }

    private static void SweepDanglingRefsAtRunEnd(RunContinuation completed)
    {
        Stopwatch timer = Stopwatch.StartNew();
        string? failure = null;
        try
        {
            RimRunnerDanglingRefProbe.Arm();
            GameDataSaveLoader.SaveGame(CheckpointSaveName);
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
        }
        finally
        {
            List<string> lines = RimRunnerDanglingRefProbe.Disarm();
            if (failure != null)
            {
                AddBatchEngineFault(completed.Report,
                    "全批执行完后的悬空引用清扫存档失败：" + failure);
            }
            else if (lines.Count > 0)
            {
                completed.Report.batchSentinels.AddRange(lines.Select(line => RimRunnerFindingEntry.Of(null, line,
                    "全批执行完后仍有悬空引用（载入存档时会丢弃这些引用）。检查的是整轮累积状态")));
            }

            timer.Stop();
            completed.DurationMs += timer.ElapsedMilliseconds;
        }
    }

    private static void DeleteCheckpoint(RimRunnerReport report)
    {
        string path = GenFilePaths.FilePathForSavedGame(CheckpointSaveName);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            AddBatchEngineFault(report, "删除 checkpoint 存档失败", exception);
        }
    }

    internal static void WriteTerminalError(
        string? command, string? runId, string message, Exception? exception = null,
        RimRunnerCaseSelector? selector = null)
    {
        string resolvedRunId = ResolveRunId(runId);
        if (HasContinuation(command, resolvedRunId))
        {
            string text = message + (exception == null ? string.Empty : "：" + exception);
            if (InRun.RealFrameExecution != null)
            {
                AbortActiveRealFrameExecution(text);
            }
            else if (InRun.SuspendedPhase != null)
            {
                AbortSuspendedPhase(text);
            }
            else if (InRun.PendingCase != null)
            {
                AbortPendingCase(text);
            }
            else if (InRun.CurrentEntry != null)
            {
                InRun.CurrentEntry.engineFaults.Add(text);
                FinishCurrentCase();
            }
            else
            {
                AddBatchEngineFault(InRun.Report, message, exception);
            }

            CompleteContinuation();
            return;
        }

        RimRunnerReport report = new()
        {
            command = command,
            runId = resolvedRunId,
            contractGatesReady = RimRunnerContractGates.Ready
        };
        AddBatchEngineFault(report, message, exception);
        DrainPendingReloadErrors(report, null);
        RimRunnerLogMonitor.DrainInto(report);
        try
        {
            RimRunnerCaseSelector? resolved = selector
                                             ?? RimRunnerCaseSelector.FromCommandString(command);
            if (resolved != null)
            {
                RimRunnerReportWriter.Write(report, resolved);
            }
            else
            {
                RimRunnerReportWriter.WriteTimestampedOnly(report);
            }
        }
        finally
        {
            RimRunnerRunCoordinator.Complete(command, resolvedRunId);
        }
    }

    internal static bool IsValidRunId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? runId)
    {
        return RimRunnerRunId.IsValid(runId);
    }

    internal static string ResolveRunId(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return RimRunnerRunId.New();
        }

        if (!IsValidRunId(runId))
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " runId 须是 " + RimRunnerRunId.Shape, nameof(runId));
        }

        return runId;
    }

    private static void AddBatchEngineFault(
        RimRunnerReport report, string message, Exception? exception = null)
    {
        string text = message + (exception == null ? string.Empty : "：" + exception);
        RimRunnerEngineLog.Warn(RimRunnerEngineFault.LogMark + text);
        report.engineFaults.Add(new RimRunnerEngineFaultEntry
        {
            at = DateTimeOffset.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            caseId = RimRunnerObservationBuffer.ActiveCaseId,
            where = RimRunnerLogMonitor.CurrentGapLabel(),
            text = text
        });
    }

    private static void AddRejection(
        RimRunnerReport report, string gate, string? subject, string text)
    {
        report.rejections.Add(RejectionEntry(gate, subject, text));
    }

    private static RimRunnerRejectionEntry RejectionEntry(string gate, string? subject, string text)
    {
        return new RimRunnerRejectionEntry
        {
            gate = gate,
            subject = subject,
            summary = FirstLine(text),
            text = text
        };
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline].TrimEnd('\r');
    }

    private sealed class RunContinuation
    {
        internal required RimRunnerCaseSelector Selector;
        internal required string Command;
        internal required string RunId;
        internal required RimRunnerReport Report;
        internal required Queue<string> RemainingCaseIds;
        internal readonly HashSet<string> CheckpointRetried = new(StringComparer.Ordinal);
        internal string? PendingOverBudget;
        internal string? PendingOverBudgetRetry;
        internal readonly Dictionary<string, RimRunnerResultEntry> RetryCarryover = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, BatchRedEvidence> BatchRedRetries = new(StringComparer.Ordinal);
        internal int TotalCaseCount;
        internal Dictionary<string, string> ApplicabilityFaults = new(StringComparer.Ordinal);
        internal Dictionary<string, string> BatchAcks = new(StringComparer.Ordinal);
        internal List<RimRunnerChainAck> ChainAcks = [];
        internal string? CurrentCaseId;
        internal int NextPhase;
        internal RimRunnerResultEntry? CurrentEntry;
        internal Dictionary<string, string>? CurrentCheckpointState = new(StringComparer.Ordinal);
        internal List<RimRunnerContext.CarriedCleanup>? CurrentCarriedCleanups = [];
        internal List<RimRunnerPawns.CarriedSpecTarget>? CurrentCarriedSpecTargets = [];
        internal readonly List<CheckpointBatchMember> PendingBatch = [];
        internal readonly List<CheckpointBatchMember> ActiveBatch = [];
        internal int BatchIndex = -1;
        internal readonly List<List<string>> CheckpointBatches = [];
        internal readonly Dictionary<string, int> CheckpointBatchOf = new(StringComparer.Ordinal);
        internal int CurrentBatchNumber = -1;
        internal int BatchStartSerial;
        internal List<int>? CurrentPhaseTicks;
        internal int CurrentSlotStartTick = -1;
        internal bool AwaitingBaselineLoad;
        internal bool CompleteAfterBaselineLoad;
        internal bool WorldTouchedSinceLoad;
        internal long DurationMs;
        internal readonly Stopwatch ReloadTimer = new();
        internal long ReloadMs;
        internal int ReloadCount;
        internal int FaultReloads;
        internal int MapReloads;
        internal bool MainMapNeedsReload;
        internal RimRunnerMapRestore? MapRestore;
        internal readonly RimRunnerRunWorldTally WorldTally = new();
        internal RealFrameExecution? RealFrameExecution;
        internal PhaseRun? SuspendedPhase;
        internal PendingCaseFinalization? PendingCase;
        internal int LastPhaseEndTick = -1;
        internal int LastPhaseEndMapTick = -1;
        internal int CurrentPhaseStartTick = -1;
        internal int CheckpointSaveTick = -1;
        internal int CheckpointBatchSize;
        internal RimRunnerScenarioPawnRoster? ScenarioPawnRoster;
        internal RimRunnerShardRun? Shard;
        internal Dictionary<string, string> KnownFailing = new(StringComparer.Ordinal);
        internal readonly List<KeyValuePair<RimRunnerResultEntry, Dictionary<string, string>?>> ParkedEnvChecks = [];
    }

    private sealed class CheckpointBatchMember
    {
        internal required string CaseId;
        internal required RimRunnerResultEntry Entry;
        internal Dictionary<string, string>? CheckpointState;
        internal List<RimRunnerContext.CarriedCleanup>? CarriedCleanups;
        internal List<RimRunnerPawns.CarriedSpecTarget>? CarriedSpecTargets;
        internal int NextPhase;
        internal RimRunnerScenarioPawnRoster? ScenarioPawnRoster;
        internal List<int>? PhaseTicks;
        internal required Dictionary<string, RimRunnerHandoffFingerprint.Reading> HandoffReadings;
    }

    private sealed class BatchRedEvidence
    {
        internal required string FirstRed;
        internal int ClockDrift;
        internal required List<string> Members;
    }

    private sealed class PhaseRun
    {
        internal required RimRunnerTestCase TestCase;
        internal required RimRunnerContext Context;
        internal required RimRunnerLogCapture Capture;
        internal required Stopwatch Timer;
        internal bool QuiescedAtStart;
        internal int PhaseStartTick = -1;
        internal RimRunnerStepper? Stepper;
        internal int SuspendTick;
    }

    private sealed class PendingCaseFinalization
    {
        internal required RimRunnerContext Context;
        internal required Stopwatch Timer;
        internal required RimRunnerLogCapture LogCapture;
        internal required List<RimRunnerScreenshotRequest> Requests;
        internal int Index;
        internal string? ArtifactId;
    }
}
