namespace RimRunner;

public class RimRunnerGameComponent : GameComponent
{
    private const float AutoRunMapReadyDelaySeconds = 0.75f;

    internal const string LoadOnlyCommand = RimRunnerEngineArgs.LoadOnlyCommand;

    private static RimRunnerCaseSelector? autoRunSelector;
    private static bool autoRunExclusive;
    private static RimRunnerShardRequest? autoRunShard;
    private static string? autoRunCommand;
    private static string? autoRunId;
    private static float mapReadyAt = -1f;

    private static Game? gameAtLoadRequest;

    private static int loadedAtTick = -1;

    internal static void MarkWorldTouched() => WorldPristineSinceLoad = true;

    internal static bool WorldPristineSinceLoad { get =>
        !field
        && loadedAtTick >= 0
        && Find.TickManager != null
        && Find.TickManager.TicksGame <= loadedAtTick + 1; private set; } = true;

    private static bool LoadPending =>
        autoRunCommand != null && ReferenceEquals(Current.Game, gameAtLoadRequest);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0290")]
    public RimRunnerGameComponent(Game game)
    {
    }

    internal static bool RequestAutoRunAfterLoad(
        RimRunnerCaseSelector? selector, string? runId, out string resolvedRunId, string? bareCommand = null,
        bool exclusive = false, RimRunnerShardRequest? shard = null)
    {
        string command = selector?.ToCanonicalLabel() ?? bareCommand ?? LoadOnlyCommand;
        resolvedRunId = RimRunnerSession.ResolveRunId(runId);
        if (!RimRunnerRunCoordinator.TryPrepareLoading(
                command, resolvedRunId, out RimRunnerRunSnapshot activeRun))
        {
            RimRunnerEngineLog.Warn("拒绝载档运行：requested=" + command + "/" + resolvedRunId
                                    + " active=" + activeRun.State + "/" + activeRun.Command + "/" + activeRun.RunId);
            return false;
        }

        autoRunSelector = selector;
        autoRunExclusive = exclusive;
        autoRunShard = shard;
        autoRunCommand = command;
        autoRunId = resolvedRunId;
        mapReadyAt = -1f;
        gameAtLoadRequest = Current.Game;
        return true;
    }

    internal static bool RequestBuildSaveAfterSceneLoad(string? runId, out string resolvedRunId)
    {
        string command = RimRunnerEngineArgs.BuildSaveCommand;
        resolvedRunId = RimRunnerSession.ResolveRunId(runId);
        if (!RimRunnerRunCoordinator.TryPrepareLoading(
                command, resolvedRunId, out RimRunnerRunSnapshot activeRun))
        {
            RimRunnerEngineLog.Warn("拒绝建档：requested=" + command + "/" + resolvedRunId
                                    + " active=" + activeRun.State + "/" + activeRun.Command + "/" + activeRun.RunId);
            return false;
        }

        autoRunSelector = null;
        autoRunCommand = command;
        autoRunId = resolvedRunId;
        mapReadyAt = -1f;
        gameAtLoadRequest = Current.Game;
        return true;
    }

    internal static void RequestBuildSaveReload(string? runId)
    {
        autoRunSelector = null;
        autoRunCommand = RimRunnerEngineArgs.BuildSaveCommand;
        autoRunId = runId;
        mapReadyAt = -1f;
        gameAtLoadRequest = Current.Game;
    }

    internal static bool RequestContinuationAfterLoad(string command, string? runId)
    {
        if (!RimRunnerRunCoordinator.BeginCheckpointLoading(
                command, runId, out RimRunnerRunSnapshot activeRun))
        {
            RimRunnerEngineLog.Warn("拒绝续段载档：requested=" + command + "/" + runId
                                    + " active=" + activeRun.State + "/" + activeRun.Command + "/" + activeRun.RunId);
            return false;
        }

        autoRunSelector = null;
        autoRunCommand = command;
        autoRunId = runId;
        mapReadyAt = -1f;
        gameAtLoadRequest = Current.Game;
        return true;
    }

    public override void GameComponentUpdate()
    {
        Pump();
    }

    private static int lastPumpFrame = -1;

    internal static void PumpIfStarved()
    {
        if (RimRunnerFrame.Current > lastPumpFrame + 1)
        {
            Pump();
        }
    }

    private static void Pump()
    {
        if (lastPumpFrame == RimRunnerFrame.Current)
        {
            return;
        }

        lastPumpFrame = RimRunnerFrame.Current;
        RimRunnerLiveServer.PublishDiagnosticsHeartbeat();
        RimRunnerHiddenWindowMouse.Update();
        RimRunnerLogMonitor.ObservePump();
        if (!LoadPending)
        {
            RimRunnerSession.PumpActiveBody();
        }

        if (Current.ProgramState == ProgramState.Playing)
        {
            RimRunnerLiveServer.PumpMainThreadQueue();
            RimRunnerDrive.Pump();
            RimRunnerScreenshotDriver.Update();
        }

        RimRunnerIdleFrameThrottle.Update(RimRunnerRunCoordinator.IsIdle && !RimRunnerProcessMode.Showcase);
        RimRunnerPresentMode.Update();
        if (LoadPending)
        {
            return;
        }

        RimRunnerSustainedErrorBreaker.ObserveOnPump();
        RimRunnerSession.AdvancePendingCaseFinalization();

        float now = RimRunnerRealtime.Seconds;
        ProcessAutoRunAfterLoad(now);
        if (RimRunnerProcessMode.Film && Current.ProgramState == ProgramState.Playing)
        {
            RimRunnerFilmSession.Pump();
        }
        else if (RimRunnerProcessMode.Showcase && Current.ProgramState == ProgramState.Playing)
        {
            RimRunnerShowcaseSession.Pump();
        }
    }

    public override void LoadedGame()
    {
        RimRunnerLoadWindowProbe.OnLoadedGame();
        RimRunnerSession.SplitLoadWindow();
        bool checkpointLoad = RimRunnerSession.TakeCheckpointLoadFlag();
        RimRunnerPawnRegistries.OnGameLoaded(checkpointLoad);
        WorldPristineSinceLoad = RimRunnerSession.HasAnyContinuation;
        loadedAtTick = Find.TickManager.TicksGame;
        RimRunnerWorldPawnMothball.OnGameLoaded(checkpointLoad);
        RimRunnerComponentState.OnGameLoaded(checkpointLoad);
        RimRunnerMapTicks.OnGameLoaded(checkpointLoad);
        RimRunnerIsolatedMap.OnGameLoaded(checkpointLoad);
        RimRunnerContext.OnGameLoaded(checkpointLoad);
    }

    public override void GameComponentTick()
    {
        RimRunnerLoadWindowProbe.OnTick();
        RimRunnerSession.TickActiveBody();
        if (RimRunnerProcessMode.Stages)
        {
            RimRunnerSceneHost.Tick();
        }

        RimRunnerProfilerBridge.SampleTick();
    }

    private static void ProcessAutoRunAfterLoad(float now)
    {
        if (autoRunCommand == null)
        {
            return;
        }

        if (Current.ProgramState != ProgramState.Playing || RimRunnerCurrentMapProbe.EngineCurrentMap() == null)
        {
            mapReadyAt = -1f;
            return;
        }

        if (!Current.Game.PlayerHasControl)
        {
            mapReadyAt = -1f;
            return;
        }

        if (mapReadyAt < 0f)
        {
            Find.TickManager.Pause();
            if (Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
            {
                return;
            }

            mapReadyAt = now;
            return;
        }

        if (now - mapReadyAt < AutoRunMapReadyDelaySeconds)
        {
            return;
        }

        string command = autoRunCommand;
        RimRunnerCaseSelector? selector = autoRunSelector;
        bool exclusive = autoRunExclusive;
        RimRunnerShardRequest? shard = autoRunShard;
        autoRunCommand = null;
        autoRunSelector = null;
        autoRunExclusive = false;
        autoRunShard = null;
        mapReadyAt = -1f;
        string? runId = autoRunId;
        autoRunId = null;
        gameAtLoadRequest = null;
        RimRunnerTextureCacheMiss.NoteAfterLoad();
        RimRunnerSession.CloseLoadWindow(command, runId);
        if (string.Equals(command, RimRunnerEngineArgs.BuildSaveCommand, StringComparison.Ordinal))
        {
            if (RimRunnerNewGame.ReloadPending)
            {
                RimRunnerNewGame.FinishReloadCheck(command, runId);
            }
            else
            {
                RimRunnerNewGame.FinishAndSave(command, runId);
            }

            return;
        }

        if (string.Equals(command, LoadOnlyCommand, StringComparison.Ordinal))
        {
            RimRunnerRunCoordinator.Complete(command, runId);
            RimRunnerEngineLog.Info("load-only：已载档并暂停，未执行任何用例。");
            return;
        }

        if (string.Equals(command, RimRunnerEngineArgs.ShowcaseCommand, StringComparison.Ordinal))
        {
            RimRunnerRunCoordinator.Complete(command, runId);
            RimRunnerShowcaseSession.OnMapReady();
            return;
        }

        if (string.Equals(command, RimRunnerEngineArgs.FilmCommand, StringComparison.Ordinal))
        {
            RimRunnerFilmSession.OnMapReady(runId);
            return;
        }

        if (RimRunnerSession.HasContinuation(command, runId))
        {
            RimRunnerSession.ResumeAfterLoad(command, runId!);
            return;
        }

        if (selector == null)
        {
            RimRunnerSession.WriteTerminalError(
                command, runId, "载档完成，但无待执行的选择器，也无匹配的续段");
            return;
        }

        RimRunnerSession.RunCases(selector, runId, exclusive, shard);
    }

    internal static void ExecuteCommand(
        RimRunnerCaseSelector selector, string? runId = null, bool cleanSave = false, bool exclusive = false,
        RimRunnerShardRequest? shard = null)
    {
        string command = selector.ToCanonicalLabel();
        bool autoTest = GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
        string? shooter = autoTest
            ? null
            : selector.ResolveCases().FirstOrDefault(testCase =>
                testCase.Kind == RimRunnerKind.Readout && RimRunnerContractGates.CallsScreenshot(testCase.Id))?.Id;
        if (shooter != null)
        {
            RimRunnerSession.WriteTerminalError(
                command, RimRunnerSession.ResolveRunId(runId),
                "含截图用例（" + shooter + "）的批仅允许带 -" + RimRunnerEngineArgs.Enable + " 的实例",
                selector: selector);
            return;
        }

        if (autoTest && cleanSave)
        {
            string resolvedRunId = RimRunnerSession.ResolveRunId(runId);
            string? saveName = RimRunnerAutoTestSave.ResolveNameOrNull();
            if (saveName == null)
            {
                RimRunnerSession.WriteTerminalError(
                    command, resolvedRunId,
                    "自动实例没有可载入的基线档，无法执行需要 clean-save 的命令",
                    selector: selector);
                return;
            }

            if (!RequestAutoRunAfterLoad(selector, resolvedRunId, out resolvedRunId, exclusive: exclusive, shard: shard))
            {
                return;
            }

            RimRunnerSession.OpenLoadWindow(RimRunnerSession.LoadWindowSource.CleanSave);
            QueueLoadGameAfterCurrentUpdate(saveName, exception =>
            {
                ClearAutoRunRequest();
                RimRunnerSession.WriteTerminalError(
                    command, resolvedRunId, "载档调用抛出异常", exception, selector);
            });
            return;
        }

        RimRunnerSession.RunCases(selector, runId, exclusive, shard);
    }

    internal static void QueueLoadGameAfterCurrentUpdate(
        string saveName, Action<Exception> exceptionHandler)
    {
        LongEventHandler.QueueLongEvent(
            () =>
            {
                RimRunnerPassRate.NoteSaveLoad(saveName);
                GameDataSaveLoader.LoadGame(saveName);
            },
            "LoadingLongEvent",
            doAsynchronously: false,
            exceptionHandler);
    }

    internal static RimRunnerCaseSelector? ResolveCommand(string command)
    {
        RimRunnerCaseSelector? selector =
            RimRunnerCaseSelector.FromCommandString(command);
        return selector != null && selector.ResolveCases().Count > 0 ? selector : null;
    }

    internal static void ClearAutoRunRequest()
    {
        RimRunnerSession.AbandonLoadWindow();
        autoRunSelector = null;
        autoRunExclusive = false;
        autoRunShard = null;
        autoRunCommand = null;
        autoRunId = null;
        mapReadyAt = -1f;
        gameAtLoadRequest = null;
    }
}
