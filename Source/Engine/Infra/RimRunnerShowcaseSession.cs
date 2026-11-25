namespace RimRunner;

internal static class RimRunnerShowcaseSession
{
    internal enum Phase
    {
        None,
        Staging,
        TearingDown,
        Staged,
        Stepping,
        Aborted
    }

    private static string? stageAfterReload;
    private static string? pendingStageId;
    private static bool pendingTeardown;
    private static string? pendingStep;
    private static string? steppingName;
    private static long lastStepPublishTick;
    private static volatile string? statusJson;
    private static int publishedRevision = -1;
    private static int publishedDrives;
    private static int lastStepErrorBaseline;
    private static int? settledStepErrors;
    private static bool firstReadySeen;

    internal static IReadOnlyList<string> StartupOwnErrors { get; private set; } = [];
    internal static IReadOnlyList<string> StartupForeignErrors { get; private set; } = [];
    internal static IReadOnlyList<string> StartupUnattributedErrors { get; private set; } = [];
    internal static IReadOnlyList<string> ColdLoadErrors { get; private set; } = [];
    internal static IReadOnlyList<string> ReloadErrors { get; private set; } = [];

    internal static Phase CurrentPhase { get; private set; } = Phase.None;
    internal static RimRunnerScene? CurrentShowcase { get; private set; }
    internal static bool Reloading { get; private set; }

    internal static string? SteppingName =>
        RimRunnerSceneHost.Chain is { Active: true } chain ? chain.StepName : steppingName;
    internal static int SteppingTicks => RimRunnerSceneHost.Chain is { Active: true } chain ? chain.WaitedTicks : 0;
    internal static int SteppingWithinTicks =>
        RimRunnerSceneHost.Chain is { Active: true } chain ? chain.WithinTicks : 0;
    internal static int SteppingDwellRemaining =>
        RimRunnerSceneHost.Chain is { Active: true } chain ? chain.DwellRemaining : 0;

    internal static int OperationErrorCount { get => RimRunnerLogMonitor.TotalErrors - field; private set; }
    internal static int LastStepOperationErrorCount =>
        settledStepErrors ?? Math.Max(0, RimRunnerLogMonitor.TotalErrors - lastStepErrorBaseline);

    internal static string? StatusJson => statusJson;

    internal static void OnMapReady()
    {
        Reloading = false;
        RimRunnerStepClock.End();
        steppingName = null;
        pendingStep = null;
        RimRunnerSceneHost.OnMapReady();
        if (!firstReadySeen)
        {
            firstReadySeen = true;
            OperationErrorCount = RimRunnerLogMonitor.TotalErrors;
            StartupOwnErrors = RimRunnerStartupErrors.Blocking;
            StartupForeignErrors = RimRunnerStartupErrors.Foreign;
            StartupUnattributedErrors = RimRunnerStartupErrors.Unattributed;
            ColdLoadErrors = RimRunnerSession.TakePendingReloadErrors();
        }
        else
        {
            OperationErrorCount = RimRunnerLogMonitor.TotalErrors;
            ReloadErrors = RimRunnerSession.TakePendingReloadErrors();
        }

        ResetLastStepErrors();

        RimRunnerSceneEnvironment.OnMapReady(RimRunnerCurrentMapProbe.EngineCurrentMap()!);

        string? next = stageAfterReload;
        stageAfterReload = null;
        if (next != null)
        {
            StageNow(next);
            return;
        }

        if (CurrentPhase == Phase.TearingDown)
        {
            CurrentPhase = Phase.None;
        }

        Publish();
    }

    internal static bool RequestStage(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? refusal)
    {
        refusal = BusyRefusal();
        if (refusal == null && RimRunnerSceneRegistry.Faults.Count > 0)
        {
            refusal = "场面名册有登记期错误，拒绝立场：" + string.Join("；", RimRunnerSceneRegistry.Faults);
        }

        RimRunnerScene? showcase = RimRunnerSceneRegistry.FindShowcase(id);
        if (refusal == null && showcase == null)
        {
            refusal = RimRunnerSceneRegistry.Find(id) != null
                ? RimRunnerSceneRegistry.NotShowcaseRefusal(id)
                : RimRunnerSceneRegistry.UnknownIdRefusal(id);
        }

        if (refusal != null)
        {
            return false;
        }

        RimRunnerSceneHost.ClearRecord();
        ResetLastStepErrors();
        pendingStep = null;
        steppingName = null;
        RimRunnerGameComponent.MarkWorldTouched();
        CurrentShowcase = showcase;
        CurrentPhase = Phase.Staging;
        pendingStageId = id;
        Publish();
        return true;
    }

    internal static bool RequestTeardown([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? refusal)
    {
        refusal = BusyRefusal();
        if (refusal != null)
        {
            return false;
        }

        RimRunnerSceneHost.ClearRecord();
        ResetLastStepErrors();
        pendingStep = null;
        steppingName = null;
        CurrentShowcase = null;
        CurrentPhase = Phase.TearingDown;
        pendingTeardown = true;
        Publish();
        return true;
    }

    internal static bool RequestStep(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? refusal)
    {
        refusal = StepBusyRefusal() ?? RimRunnerSceneHost.StepRefusal(name);
        if (refusal != null)
        {
            return false;
        }

        RimRunnerSceneHost.BeginStepRecord(name);
        lastStepErrorBaseline = RimRunnerLogMonitor.TotalErrors;
        settledStepErrors = null;
        pendingStep = name;
        steppingName = name;
        CurrentPhase = Phase.Stepping;
        Publish();
        return true;
    }

    internal static void Pump()
    {
        SettleStepping();

        if (publishedRevision != RimRunnerSceneHost.Revision || publishedDrives != RimRunnerDrive.Count)
        {
            Publish();
        }

        if (Reloading)
        {
            return;
        }

        if (pendingTeardown || pendingStageId != null)
        {
            ReloadErrors = [];
        }

        if (pendingTeardown)
        {
            pendingTeardown = false;
            RimRunnerSceneHost.Cleanup();
            BeginReload(null);
            return;
        }

        if (pendingStageId != null)
        {
            string id = pendingStageId;
            pendingStageId = null;
            RimRunnerSceneHost.Cleanup();
            if (RimRunnerSceneHost.WorldDirty)
            {
                BeginReload(id);
            }
            else
            {
                StageNow(id);
            }

            return;
        }

        if (pendingStep != null)
        {
            string name = pendingStep;
            pendingStep = null;
            if (StepBusyRefusal(name) == null && RimRunnerSceneHost.StepRefusal(name) == null
                && RimRunnerSceneHost.RunStep(name) == null
                && RimRunnerSceneHost.Chain is { Active: true })
            {
                if (RimRunnerStepClock.Begin() is string clockRefusal)
                {
                    RimRunnerSceneHost.AbortRunningChain("：" + clockRefusal);
                }
                else
                {
                    lastStepPublishTick = Find.TickManager?.TicksGame ?? 0;
                }
            }

            SettleStepping();
        }
    }

    internal static string? BusyRefusal()
    {
        return RimRunnerRunCoordinator.IsDriving ? "live 驱动在途" : SessionBusyRefusal();
    }

    private static string? SessionBusyRefusal()
    {
        return Reloading || CurrentPhase is Phase.Staging or Phase.TearingDown
            ? "上一次立场或拆场尚未结束"
            : null;
    }

    internal static string? DriveRefusal()
    {
        return SessionBusyRefusal()
               ?? (CurrentPhase == Phase.Stepping || pendingStep != null ? "展台步骤进行中：" + SteppingName : null);
    }

    internal static string? StepBusyRefusal(string? executing = null)
    {
        return BusyRefusal()
               ?? (CurrentPhase == Phase.Stepping
                   && !string.Equals(executing, steppingName, StringComparison.Ordinal)
                       ? "上一步仍在推进：" + SteppingName
                       : null);
    }

    private static void SettleStepping()
    {
        if (CurrentPhase == Phase.Stepping && pendingStep != null)
        {
            return;
        }

        bool stepping = CurrentPhase == Phase.Stepping;
        if (!stepping && !RimRunnerStepClock.Taken)
        {
            return;
        }

        if (RimRunnerSceneHost.Chain is { Active: true } forced && RimRunnerDriveTime.ForcePauseRefusal() is { } pauseLine
            && !RimRunnerDriveTime.ForcePauseSelfResolving())
        {
            forced.ResolveForcePaused(pauseLine);
        }

        if (RimRunnerSceneHost.Chain is { Active: true })
        {
            long tick = Find.TickManager?.TicksGame ?? 0;
            if (tick - lastStepPublishTick >= RimRunnerLiveServer.BurstHeartbeatTicks)
            {
                lastStepPublishTick = tick;
                Publish();
            }

            return;
        }

        RimRunnerStepClock.End();
        steppingName = null;
        if (stepping)
        {
            settledStepErrors = Math.Max(0, RimRunnerLogMonitor.TotalErrors - lastStepErrorBaseline);
            CurrentPhase = RimRunnerSceneHost.AbortLine == null ? Phase.Staged : Phase.Aborted;
            Publish();
        }
    }

    private static void StageNow(string id)
    {
        CurrentShowcase = RimRunnerSceneRegistry.FindShowcase(id);
        CurrentPhase = Phase.Staging;
        bool staged = RimRunnerSceneHost.Stage(id);
        CurrentPhase = staged ? Phase.Staged : Phase.Aborted;
        OperationErrorCount = RimRunnerLogMonitor.TotalErrors;
        ResetLastStepErrors();
        Publish();
    }

    private static void ResetLastStepErrors()
    {
        settledStepErrors = null;
        lastStepErrorBaseline = RimRunnerLogMonitor.TotalErrors;
    }

    private static void BeginReload(string? stageAfter)
    {
        string? saveName = RimRunnerAutoTestSave.ResolveNameOrNull();
        if (saveName == null)
        {
            SetAborted("无法解析基线档名，拆场回载无从进行");
            return;
        }

        if (!RimRunnerGameComponent.RequestAutoRunAfterLoad(
                null, null, out string runId, RimRunnerEngineArgs.ShowcaseCommand))
        {
            SetAborted("运行闸拒绝回载");
            return;
        }

        Reloading = true;
        stageAfterReload = stageAfter;
        Publish();
        RimRunnerSession.OpenLoadWindow(RimRunnerSession.LoadWindowSource.ShowcaseReload);
        RimRunnerGameComponent.QueueLoadGameAfterCurrentUpdate(saveName, exception =>
        {
            RimRunnerGameComponent.ClearAutoRunRequest();
            ReloadErrors = RimRunnerSession.TakePendingReloadErrors();
            RimRunnerRunCoordinator.Complete(RimRunnerEngineArgs.ShowcaseCommand, runId);
            Reloading = false;
            stageAfterReload = null;
            RimRunnerEngineFault.Raise("展台回载基线档抛出异常：" + exception);
            SetAborted("回载基线档抛出异常：" + exception.GetType().Name + ": " + exception.Message);
        });
    }

    private static void SetAborted(string line)
    {
        RimRunnerSceneHost.Abort(line);
        CurrentPhase = Phase.Aborted;
        Publish();
    }

    private static void Publish()
    {
        publishedDrives = RimRunnerDrive.Count;
        statusJson = RimRunnerLiveServer.BuildShowcaseStatusJson();
        publishedRevision = RimRunnerSceneHost.Revision;
    }
}
