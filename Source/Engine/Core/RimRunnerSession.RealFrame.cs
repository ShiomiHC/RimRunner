using System.Diagnostics;
using RimWorld.Planet;

namespace RimRunner;

public static partial class RimRunnerSession
{
    private static void BeginCurrentRealFrameCase(
        RimRunnerTestCase testCase, IRimRunnerRealFrameCase realFrameCase)
    {
        if (testCase.Level == RimRunnerLevel.World && !testCase.DeclaresLiveStoryteller)
        {
            RimRunnerScenarioManager.SweepBeforeCase(testCase.Id);
        }

        RimRunnerContext context = testCase.NewContext();
        context.Map = testCase.Level == RimRunnerLevel.World ? RimRunnerCurrentMapProbe.EngineCurrentMap() : null;
        context.SetCheckpointState(InRun.CurrentCheckpointState);
        context.BeginCase(InRun.CurrentEntry!, testCase.DeclaresObservesRuntime);
        if (testCase.Level != RimRunnerLevel.World)
        {
            AppendCurrentException(RimRunnerLog.Prefix
                + " 契约闸违例：真实帧用例须 Level=World，实际申报 " + testCase.Level);
        }

        RealFrameExecution execution = new()
        {
            Context = context,
            Timer = Stopwatch.StartNew(),
            LogCapture = RimRunnerLogMonitor.Snapshot(owner: "真实帧用例 " + testCase.Id, ackCase: testCase),
            LiveStoryteller = testCase.DeclaresLiveStoryteller,
            StartGameTick = Find.TickManager.TicksGame,
            WallClockTimeoutSeconds = realFrameCase.WallClockTimeoutSeconds,
            DeadlineRealtime = RimRunnerRealtime.Seconds + realFrameCase.WallClockTimeoutSeconds
        };
        RimRunnerSustainedErrorBreaker.Arm(execution.LogCapture);
        RimRunnerStubProbe.Open(testCase.Id);
        RimRunnerMachineLoadProbe.Open(testCase);
        InRun.RealFrameExecution = execution;
        PublishDiagnosticSnapshot();

        try
        {
            if (testCase.Level == RimRunnerLevel.World && !testCase.DeclaresLiveStoryteller)
            {
                RimRunnerScenarioManager.EnsureAll(context);
            }

            PrepareRealFrameAutomationState(context, testCase.DeclaresLiveStoryteller);
            execution.LiveAtStart = RimRunnerStorytellerQuiesce.LiveInEffect;
            RimRunnerMapSiteLedger.Record(testCase);
            if (testCase.OnIsolatedMap)
            {
                RimRunnerIsolatedMap.Enter(context, testCase.IsolatedMapSize, testCase.IsolatedMapGround,
                    lastInSlot: true, compareWorld: false);
                RimRunnerIsolatedMap.ShowForRealFrame(context.Map!);
                RimRunnerFrozenMapSpawnProbe.Arm(testCase);
            }

            context.BeginPhaseBaseline();
            context.ArmForeignDamage(true);
            execution.StartGameTick = Find.TickManager.TicksGame;
            execution.DeadlineRealtime = RimRunnerRealtime.Seconds + execution.WallClockTimeoutSeconds;
            execution.Stepper = new RimRunnerStepper(testCase.InvokeSteps(context),
                new RimRunnerCaseStepHost(context, testCase.Id, realFrame: true), "用例 " + testCase.Id);
            execution.Stepper.Resume();
            NoteRealFrameProgress(execution);
            if (execution.Stepper.Done)
            {
                MarkRealFramePending(execution);
            }
        }
        catch (Exception exception)
        {
            AppendCurrentException(exception);
            MarkRealFramePending(execution);
        }

        if (execution.PendingFinalize)
        {
            FinalizeRealFrame(execution);
        }
    }

    private static void PrepareRealFrameAutomationState(RimRunnerContext context, bool liveStoryteller)
    {
        if (liveStoryteller)
        {
            bool previousStorytellerEnabled = DebugSettings.enableStoryteller;
            DebugSettings.enableStoryteller = true;
            context.Defer(() => DebugSettings.enableStoryteller = previousStorytellerEnabled,
                "恢复真实帧用例前的叙事者开关");
        }
        else
        {
            RimRunnerStorytellerQuiesce.Ensure(context.Current!.id);
        }

        Faction playerFaction = Faction.OfPlayer;
        if (playerFaction != null && !playerFaction.HasName)
        {
            playerFaction.Name = "RimRunnerTestFaction";
            context.Defer(() => playerFaction.Name = null,
                "恢复真实帧用例前未命名的玩家派系");
        }

        if (playerFaction == null || Find.WorldObjects == null)
        {
            return;
        }

        foreach (Settlement settlement in Find.WorldObjects.Settlements
                     .Where(candidate => candidate.Faction == playerFaction && candidate.HasMap))
        {
            if (settlement.namedByPlayer)
            {
                continue;
            }

            string previousName = settlement.Name;
            if (settlement.Name.NullOrEmpty())
            {
                settlement.Name = "RimRunnerTestSettlement";
            }

            settlement.namedByPlayer = true;
            context.Defer(() =>
                {
                    settlement.Name = previousName;
                    settlement.namedByPlayer = false;
                },
                "恢复真实帧用例前未命名的玩家据点");
        }
    }

    internal static void TickActiveBody()
    {
        RealFrameExecution? execution = continuation?.RealFrameExecution;
        RimRunnerStepper? stepper = execution?.Stepper;
        if (stepper == null || execution!.PendingFinalize || !stepper.WaitingOnTicks)
        {
            return;
        }

        RimRunnerTimeDriver.EnterVanillaTick();
        try
        {
            if (DebugSettings.fastEcology)
            {
                throw new InvalidOperationException(
                    RimRunnerLog.Prefix + " 按原版速度推 tick 期间禁止 DebugSettings.fastEcology");
            }

            stepper.Tick();
        }
        catch (Exception exception)
        {
            AppendCurrentException(exception);
            MarkRealFramePending(execution);
        }
        finally
        {
            RimRunnerTimeDriver.ExitVanillaTick();
        }
    }

    internal static void PumpActiveBody()
    {
        if (continuation?.SuspendedPhase != null)
        {
            AdvanceSuspendedPhase();
            return;
        }

        RealFrameExecution? execution = continuation?.RealFrameExecution;
        if (execution == null)
        {
            return;
        }

        if (!execution.PendingFinalize)
        {
            if (RimRunnerSustainedErrorBreaker.Tripped)
            {
                MarkRealFramePending(execution);
            }
            else if (RimRunnerRealtime.Seconds >= execution.DeadlineRealtime)
            {
                AppendCurrentException(RimRunnerLog.Prefix + " 真实帧用例墙钟超时："
                                                           + execution.WallClockTimeoutSeconds + "s，已推进 "
                                                           + (Find.TickManager.TicksGame - execution.StartGameTick) + " tick");
                MarkRealFramePending(execution);
            }
            else if (execution.Stepper != null)
            {
                try
                {
                    execution.Stepper.Frame();
                    NoteRealFrameProgress(execution);
                    if (execution.Stepper.Done)
                    {
                        MarkRealFramePending(execution);
                    }
                }
                catch (Exception exception)
                {
                    AppendCurrentException(exception);
                    MarkRealFramePending(execution);
                }
            }
        }

        if (execution.PendingFinalize)
        {
            FinalizeRealFrame(execution);
        }
    }

    private static void FinalizeRealFrame(RealFrameExecution execution)
    {
        if (execution.LiveStoryteller)
        {
            AddLiveFault(execution.Context.Current!, execution.LiveAtStart);
        }

        EndRealFrameExecution(execution);
        FinishCurrentCase();
        RequestBaselineLoad(completeAfterLoad: InRun.RemainingCaseIds.Count == 0);
    }

    internal static void AddLiveFault(RimRunnerResultEntry entry, bool liveAtStart)
    {
        if (RimRunnerStorytellerQuiesce.LiveInEffect)
        {
            return;
        }

        string state = RimRunnerStorytellerQuiesce.DescribeLiveState();
        if (liveAtStart)
        {
            entry.sentinelFaults.Add(RimRunnerFindingEntry.Of(null, null, "用例 " + entry.id + " 声明 LiveStoryteller，开始执行时叙事者在照发态，收尾时已不在照发态（"
                + state + "）"));
        }
        else
        {
            entry.engineFaults.Add(RimRunnerLog.Prefix + " 用例 " + entry.id + " 声明 LiveStoryteller，开始执行前叙事者照发未生效（" + state + "）");
        }
    }

    private static void NoteRealFrameProgress(RealFrameExecution execution)
    {
        RimRunnerStepper stepper = execution.Stepper!;
        InRun.CurrentEntry!.ticksAdvanced = Find.TickManager.TicksGame - execution.StartGameTick;
        if (stepper.TicksSpeed is { } speed && !execution.Speeds.Contains(speed))
        {
            execution.Speeds.Add(speed);
        }

        PublishDiagnosticSnapshot();
    }

    private static void MarkRealFramePending(RealFrameExecution execution)
    {
        execution.PendingFinalize = true;
        foreach (Exception fault in execution.Stepper?.Dispose() ?? [])
        {
            AppendCurrentException(fault);
        }

        execution.Stepper = null;
        AppendUnyieldedOps();
    }

    private static void AppendUnyieldedOps()
    {
        foreach (string name in RimRunnerOp.TakeUnyielded())
        {
            AppendCurrentException(RimRunnerLog.Prefix + " 调原语 " + name + " 没有 yield：原语在 RunSteps 里 yield return 才执行");
        }
    }

    private static void EndRealFrameExecution(RealFrameExecution execution)
    {
        RimRunnerCurrentMapProbe.Disarm();
        RimRunnerFrozenMapSpawnProbe.Disarm();
        try
        {
            execution.Context.EndCase();
        }
        catch (Exception cleanupException)
        {
            InRun.CurrentEntry!.cleanupErrors.Add("真实帧用例收尾时 Context.EndCase 失败：" + cleanupException);
        }

        CloseCaseWindow(InRun.CurrentEntry!, execution.LogCapture);
        execution.Timer.Stop();
        InRun.CurrentEntry!.durationMs += execution.Timer.ElapsedMilliseconds;
        InRun.DurationMs += execution.Timer.ElapsedMilliseconds;
        InRun.CurrentEntry.ticksAdvanced = Find.TickManager.TicksGame - execution.StartGameTick;
        InRun.CurrentEntry.frameSpeed = execution.Speeds.Count == 0 ? null : string.Join("+", execution.Speeds);
        InRun.RealFrameExecution = null;
    }

    private static void AbortActiveRealFrameExecution(string reason)
    {
        RealFrameExecution? execution = continuation?.RealFrameExecution;
        if (execution == null)
        {
            return;
        }

        InRun.CurrentEntry!.engineFaults.Add(reason);
        foreach (Exception fault in execution.Stepper?.Dispose() ?? [])
        {
            InRun.CurrentEntry.engineFaults.Add("中止真实帧用例时释放正文失败：" + fault);
        }

        execution.Stepper = null;
        RimRunnerOp.TakeUnyielded();
        EndRealFrameExecution(execution);
        FinishCurrentCase();
    }

    private sealed class RealFrameExecution
    {
        internal required RimRunnerContext Context;
        internal bool LiveStoryteller;
        internal bool LiveAtStart;
        internal RimRunnerStepper? Stepper;
        internal int StartGameTick;
        internal float WallClockTimeoutSeconds;
        internal float DeadlineRealtime;
        internal bool PendingFinalize;
        internal readonly List<RimRunnerFrameSpeed> Speeds = [];
        internal required Stopwatch Timer;
        internal required RimRunnerLogCapture LogCapture;
    }
}
