using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LudeonTK;
using UnityStubs.Rendering;

namespace RimRunner;

internal static class RimRunnerFilmSession
{
    private enum Stage
    {
        Start,
        AwaitReload,
        Advancing,
        Settle,
        Capturing,
        Draining
    }

    private sealed class Job
    {
        internal int Seq;
        internal required bool IsShot;
        internal required RimRunnerScene Showcase;
        internal RimRunnerShot? Shot;
        internal string? Step;
        internal Stage Stage;
        internal readonly Stopwatch Clock = Stopwatch.StartNew();

        internal bool Reloaded;
        internal string? Failure;
        internal int Seed;
        internal int? SeedOverride;
        internal int SceneSeed;
        internal RimRunnerLogCapture? LogCapture;
        internal List<string> Errors = [];
        internal List<RimRunnerAssertEntry> Warnings = [];
        internal List<RimRunnerFilmStepSpan> Steps = [];
        internal int SettleFrames;
        internal int MatchedFrames;
        internal int FrameIndex;
        internal int Ticks;
        internal Vector3 LastPos;
        internal float LastSize;

        internal List<string> Path = [];
        internal int PathStartFrame;
        internal bool PathStarted;
        internal bool PathSettled;
        internal int SettleFrameIndex;
        internal int TailRemaining;
        internal int MaxFrames;
        internal int SeenAborts;
        internal readonly List<int> PendingStills = [];
        internal int StepCount;
        internal int StepStartFrame;
        internal int StepStartTick;
        internal string? LastStepName;
        internal int CurrentSpeed;
        internal string BadgeName = "立场";
        internal string? Stamp;
        internal bool Clean;
        internal string? AudioDir;
        internal bool HoldAct;
        internal bool HadTarget;
        internal readonly HashSet<HoverIssue> HoverIssuesNoted = [];
        internal bool PendingTipNoted;
    }

    private const int SettleMatchFrames = 2;
    private const int SettleGiveUpFrames = 60;
    private const int MaxPendingWrites = 6;

    private static string? runId;
    private static bool ready;
    private static int lastSeq;
    private static Job? job;
    private static string? resultJson;
    private static int pendingWrites;
    private static volatile string? writeError;
    private static volatile string? statusJson;
    private static int endSeq;
    private static bool endReloading;
    private static readonly Stopwatch EndClock = new();
    private static bool startupTaken;
    private static readonly List<string> unreportedStartupErrors = [];
    private static readonly List<string> unreportedLoadErrors = [];

    internal static string? StatusJson => statusJson;

    internal static void OnFed(string? error)
    {
        if (error != null)
        {
            writeError ??= error;
        }

        Interlocked.Decrement(ref pendingWrites);
    }

    internal static void OnMapReady(string? loadRunId)
    {
        runId = loadRunId;
        if (!RimRunnerRunCoordinator.TryStart(RimRunnerEngineArgs.FilmCommand, runId, out RimRunnerRunSnapshot active))
        {
            RimRunnerEngineFault.Raise("拍摄实例载档就绪但运行闸不在本轮：active=" + active.State + "/"
                      + active.Command + "/" + active.RunId);
        }

        if (!startupTaken)
        {
            startupTaken = true;
            unreportedStartupErrors.AddRange(RimRunnerStartupErrors.Blocking.Select(text => "本产品侧：" + text));
            unreportedStartupErrors.AddRange(RimRunnerStartupErrors.Foreign.Select(text => "他 mod " + text));
            unreportedStartupErrors.AddRange(RimRunnerStartupErrors.Unattributed.Select(text => "来源未定：" + text));
        }

        unreportedLoadErrors.AddRange(RimRunnerSession.TakePendingReloadErrors());
        RimRunnerSceneHost.OnMapReady();
        RimRunnerSceneHost.ClearRecord();
        RimRunnerSceneHost.StepEvent = OnStepEvent;
        RimRunnerSceneHost.ActGate = () => job is not { Stage: Stage.Capturing, HoldAct: true };
        RimRunnerSceneEnvironment.OnMapReady(RimRunnerCurrentMapProbe.EngineCurrentMap()!);
        ready = true;
        if (endReloading)
        {
            CompleteEnd(null, reloaded: true);
            return;
        }

        if (job != null && job.Stage == Stage.AwaitReload)
        {
            job.Reloaded = true;
            StageScene();
        }

        Publish();
    }

    internal static string? RequestRefusal()
    {
        if (RimRunnerRunCoordinator.IsDriving)
        {
            return "live 驱动在途";
        }

        if (!ready)
        {
            return "拍摄实例尚未就绪：首次载档未完成";
        }

        if (job != null)
        {
            return "上一个请求尚未完成";
        }

        if (endSeq != 0)
        {
            return "拍摄会话正在收尾";
        }

        return RimRunnerSceneRegistry.Faults.Count > 0
            ? "场面名册有登记期错误，拒绝拍摄：" + string.Join("；", RimRunnerSceneRegistry.Faults)
            : null;
    }

    internal static string? DriveRefusal()
    {
        if (!ready)
        {
            return "拍摄实例尚未就绪：首次载档未完成";
        }

        return job != null ? "拍摄请求在途" : endSeq != 0 ? "拍摄会话正在收尾" : null;
    }

    internal static string? AcceptShot(string id, string shotName, int? seed = null, string? stamp = null,
        bool clean = false, string? audioDir = null)
    {
        RimRunnerScene? showcase = RimRunnerSceneRegistry.Find(id);
        if (showcase == null)
        {
            return RimRunnerSceneRegistry.UnknownIdRefusal(id);
        }

        if (showcase is not IRimRunnerFilm film)
        {
            return "场面 " + id + " 没有镜头（未实现 IRimRunnerFilm）";
        }

        RimRunnerShot? shot = film.Shots.FirstOrDefault(item => string.Equals(item.Name, shotName, StringComparison.Ordinal));
        if (shot == null)
        {
            return "场面 " + id + " 没有镜头「" + shotName + "」；可用："
                   + (film.Shots.Count == 0 ? "无" : string.Join("、", film.Shots.Select(item => item.Name)));
        }

        Accept(new Job
        {
            IsShot = true, Showcase = showcase, Shot = shot, Step = shot.Setup, SeedOverride = seed, Stamp = stamp,
            Clean = clean, AudioDir = audioDir,
        });
        return null;
    }

    internal static string? AcceptPaths(string id, string? leaf, int fps, string? stamp = null, bool clean = false)
    {
        if (!RimRunnerShot.PathsFpsValid(fps))
        {
            return "路径录像的帧率须是 " + GenTicks.TicksPerRealSecond + " 的约数：" + fps;
        }

        RimRunnerScene? scene = RimRunnerSceneRegistry.Find(id);
        if (scene == null)
        {
            return RimRunnerSceneRegistry.UnknownIdRefusal(id);
        }

        List<string> leaves = RimRunnerSceneStepTree.Leaves(scene.Steps);
        bool emptyPath = scene.Steps.Count == 0;
        if (emptyPath ? leaf != null : leaf == null || !leaves.Contains(leaf))
        {
            return "场面 " + id + " 没有叶步骤「" + (leaf ?? "") + "」；可用："
                   + (leaves.Count == 0 ? "无" : string.Join("、", leaves));
        }

        Accept(new Job
        {
            IsShot = true, Showcase = scene, Shot = RimRunnerShot.Paths(scene, leaf, fps), Step = null, Stamp = stamp,
            Clean = clean,
        });
        return null;
    }

    internal static string? AcceptStage(string id, string? step)
    {
        RimRunnerScene? showcase = RimRunnerSceneRegistry.Find(id);
        if (showcase == null)
        {
            return RimRunnerSceneRegistry.UnknownIdRefusal(id);
        }

        if (step != null && showcase.Steps.All(item => !string.Equals(item.Name, step, StringComparison.Ordinal)))
        {
            return "场面 " + id + " 没有步骤「" + step + "」；可用："
                   + (showcase.Steps.Count == 0 ? "无" : string.Join("、", showcase.Steps.Select(item => item.Name)));
        }

        Accept(new Job { IsShot = false, Showcase = showcase, Step = step });
        return null;
    }

    internal static string? EndRefusal()
    {
        if (RimRunnerRunCoordinator.IsDriving)
        {
            return "live 驱动在途";
        }

        if (job != null)
        {
            return "上一个请求尚未完成";
        }

        if (endSeq != 0)
        {
            return "拍摄会话正在收尾";
        }

        return RimRunnerRunCoordinator.IsIdle ? "没有进行中的拍摄会话" : null;
    }

    internal static string AcceptEnd()
    {
        endSeq = ++lastSeq;
        EndClock.Restart();
        Publish();
        return statusJson!;
    }

    private static void Accept(Job accepted)
    {
        if (RimRunnerRunCoordinator.IsIdle)
        {
            RimRunnerRunCoordinator.TryStart(RimRunnerEngineArgs.FilmCommand, runId, out _);
        }

        accepted.Seq = ++lastSeq;
        accepted.Stage = Stage.Start;
        job = accepted;
        Publish();
    }

    internal static void Pump()
    {
        if (endSeq != 0 && !endReloading)
        {
            try
            {
                End();
            }
            catch (Exception exception)
            {
                RimRunnerEngineFault.Raise("拍摄收尾抛出异常：" + exception);
                CompleteEnd("收尾抛出异常：" + exception.GetType().Name + ": " + exception.Message, reloaded: false);
            }

            return;
        }

        if (job == null)
        {
            return;
        }

        try
        {
            switch (job.Stage)
            {
                case Stage.Start:
                    Begin();
                    break;
                case Stage.Advancing:
                    AdvanceStage();
                    break;
                case Stage.Settle:
                    Settle();
                    break;
                case Stage.Draining:
                    if (Volatile.Read(ref pendingWrites) == 0)
                    {
                        Complete();
                    }

                    break;
            }
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("拍摄会话抛出异常：" + exception);
            Fail("拍摄会话抛出异常：" + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void Begin()
    {
        if (job!.IsShot)
        {
            RimRunnerShotOutput output = job.Shot!.Output;
            if (!ResizeWindow(output.Width, output.Height))
            {
                return;
            }
        }

        bool dirty = CleanupScene();
        bool reload = job.IsShot ? !RimRunnerGameComponent.WorldPristineSinceLoad : dirty;
        if (!reload)
        {
            StageScene();
            return;
        }

        string? saveName = RimRunnerAutoTestSave.ResolveNameOrNull();
        if (saveName == null)
        {
            Fail("无法解析基线档名，回载无从进行");
            return;
        }

        if (!RimRunnerGameComponent.RequestContinuationAfterLoad(RimRunnerEngineArgs.FilmCommand, runId))
        {
            Fail("运行闸拒绝回载");
            return;
        }

        job.Stage = Stage.AwaitReload;
        Publish();
        RimRunnerSession.OpenLoadWindow(RimRunnerSession.LoadWindowSource.FilmReload);
        RimRunnerGameComponent.QueueLoadGameAfterCurrentUpdate(saveName, exception =>
        {
            RimRunnerGameComponent.ClearAutoRunRequest();
            unreportedLoadErrors.AddRange(RimRunnerSession.TakePendingReloadErrors());
            RimRunnerRunCoordinator.TryStart(RimRunnerEngineArgs.FilmCommand, runId, out _);
            RimRunnerEngineFault.Raise("拍摄回载基线档抛出异常：" + exception);
            Fail("回载基线档抛出异常：" + exception.GetType().Name + ": " + exception.Message);
        });
    }

    private static bool CleanupScene()
    {
        RimRunnerSceneHost.ClearRecord();
        RimRunnerSceneHost.Cleanup();
        return RimRunnerSceneHost.WorldDirty;
    }

    private static void End()
    {
        bool dirty = CleanupScene();
        if (!dirty && RimRunnerGameComponent.WorldPristineSinceLoad)
        {
            CompleteEnd(null, reloaded: false);
            return;
        }

        string? saveName = RimRunnerAutoTestSave.ResolveNameOrNull();
        if (saveName == null)
        {
            CompleteEnd("无法解析基线档名，回载无从进行", reloaded: false);
            return;
        }

        if (!RimRunnerGameComponent.RequestContinuationAfterLoad(RimRunnerEngineArgs.FilmCommand, runId))
        {
            CompleteEnd("运行闸拒绝回载", reloaded: false);
            return;
        }

        endReloading = true;
        Publish();
        RimRunnerSession.OpenLoadWindow(RimRunnerSession.LoadWindowSource.FilmReload);
        RimRunnerGameComponent.QueueLoadGameAfterCurrentUpdate(saveName, exception =>
        {
            RimRunnerGameComponent.ClearAutoRunRequest();
            unreportedLoadErrors.AddRange(RimRunnerSession.TakePendingReloadErrors());
            RimRunnerRunCoordinator.TryStart(RimRunnerEngineArgs.FilmCommand, runId, out _);
            RimRunnerEngineFault.Raise("拍摄收尾回载基线档抛出异常：" + exception);
            CompleteEnd("回载基线档抛出异常：" + exception.GetType().Name + ": " + exception.Message, reloaded: false);
        });
    }

    private static void CompleteEnd(string? failure, bool reloaded)
    {
        if (failure == null)
        {
            RimRunnerRunCoordinator.Complete(RimRunnerEngineArgs.FilmCommand, runId);
        }

        resultJson = RimRunnerLiveServer.BuildFilmResultJson(new RimRunnerFilmResult
        {
            Seq = endSeq,
            IsShot = false,
            Id = "",
            Failure = failure,
            Warnings = [],
            Steps = [],
            Messages = [],
            StagingErrors = [],
            StepAborts = [],
            Errors = [],
            StartupErrors = TakeUnreported(unreportedStartupErrors),
            LoadErrors = TakeUnreported(unreportedLoadErrors),
            Reloaded = reloaded,
            ElapsedMs = EndClock.ElapsedMilliseconds
        });
        endSeq = 0;
        endReloading = false;
        Publish();
        string result = resultJson;
        RimRunnerPipeLink.Emit(RimRunnerPipeProtocol.Events.FilmResult, writer =>
        {
            writer.WritePropertyName(RimRunnerPipeProtocol.Keys.Result);
            writer.WriteRawValue(result);
        });
    }

    private static void StageScene()
    {
        string id = job!.Showcase.Id;
        job.SceneSeed = job.Showcase.ResolveSceneSeed();
        if (job.IsShot && RimRunnerFilmMusic.Check(job.Shot!.Music) is { } musicFault)
        {
            job.Failure ??= musicFault;
            Finish();
            return;
        }

        RimRunnerFilmAudio.Begin(job.AudioDir);
        if (job.IsShot)
        {
            job.Seed = job.SeedOverride ?? job.Shot!.ResolveSeed(id);
            RimRunnerFilmClock.Hold();
            RimRunnerFilmMusic.Hold();
        }

        RimRunnerGameComponent.MarkWorldTouched();
        bool staged = RimRunnerSceneHost.Stage(id);
        if (!staged)
        {
            Finish();
            return;
        }

        if (job.Step != null)
        {
            RimRunnerSceneHost.RunStep(job.Step);
            if (RimRunnerSceneHost.StepAborts.Count > 0)
            {
                Finish();
                return;
            }
        }

        if (RimRunnerSceneHost.Chain is { Active: true })
        {
            job.Stage = Stage.Advancing;
            RimRunnerFilmClock.ArmStage(Fail);
            Publish();
            return;
        }

        if (!job.IsShot)
        {
            Finish();
            return;
        }

        PrepareShot();
    }

    private static void AdvanceStage()
    {
        job!.HadTarget |= RimRunnerHiddenWindowMouse.HasTarget;
        if (RimRunnerSceneHost.Chain is { Active: true })
        {
            return;
        }

        RimRunnerFilmClock.DisarmStage();
        if (RimRunnerSceneHost.StepAborts.Count > 0)
        {
            Finish();
            return;
        }

        if (!job!.IsShot)
        {
            Finish();
            return;
        }

        PrepareShot();
    }

    private static void PrepareShot()
    {
        RimRunnerShot shot = job!.Shot!;
        job.LogCapture = RimRunnerLogMonitor.Snapshot(owner: "拍摄 " + job.Showcase.Id + "/" + shot.Name);

        RimRunnerScreenshotDriver.CloseDebugWindows();
        if (Find.WindowStack.IsOpen(typeof(EditWindow_Log)))
        {
            Find.WindowStack.TryRemove(typeof(EditWindow_Log));
        }

        if (RimRunnerUiLevels.Apply(shot.Camera.Ui) is string refused)
        {
            Fail(refused);
            return;
        }

        if (shot.Camera.Ui == RimRunnerUiLevel.Scene)
        {
            Find.Selector.ClearSelection();
        }

        if (!TryCamera(0, out Vector3 position, out float size, out string? lost))
        {
            Fail(lost);
            return;
        }

        RimRunnerFilmClock.LockCamera(position, size);
        job.Stage = Stage.Settle;
        Publish();
    }

    private static bool ResizeWindow(int width, int height)
    {
        if (Waylith.EngineWindow.SizeLocked)
        {
            if (Waylith.EngineWindow.Width == width && Waylith.EngineWindow.Height == height)
            {
                return true;
            }

            Fail("窗口尺寸已锁定，无法调整到 " + width + "×" + height
                 + "（实际 " + Waylith.EngineWindow.Width + "×" + Waylith.EngineWindow.Height + "）");
            return false;
        }

        if (Waylith.EngineWindow.DisplayMode != Waylith.Graphics.DisplayMode.Windowed)
        {
            Waylith.EngineWindow.SetDisplayMode(Waylith.Graphics.DisplayMode.Windowed);
        }

        RimRunnerWindowSize.RequestPhysical(width, height);
        return true;
    }

    private static string ResizeFailure(int width, int height)
    {
        int actualWidth = Waylith.EngineWindow.Width;
        int actualHeight = Waylith.EngineWindow.Height;
        string line = "窗口未能调整到 " + width + "×" + height + "（实际 " + actualWidth + "×" + actualHeight + "）";
        if (actualWidth <= 0 || actualHeight <= 0 || (actualWidth >= width && actualHeight >= height))
        {
            return line;
        }

        double factor = Math.Min((double)actualWidth / width, (double)actualHeight / height);
        return line + "；同宽高比能容纳的最大输出 " + ((int)(width * factor) & ~1) + "×" + ((int)(height * factor) & ~1);
    }

    private static void Settle()
    {
        job!.HadTarget |= RimRunnerHiddenWindowMouse.HasTarget;
        RimRunnerShotOutput output = job.Shot!.Output;
        job.SettleFrames++;
        job.MatchedFrames = Waylith.EngineWindow.Width == output.Width && Waylith.EngineWindow.Height == output.Height ? job.MatchedFrames + 1 : 0;
        if (job.MatchedFrames < SettleMatchFrames)
        {
            if (job.SettleFrames >= SettleGiveUpFrames)
            {
                Fail(ResizeFailure(output.Width, output.Height));
            }

            return;
        }

        job.FrameIndex = 0;
        job.Path = PathSteps(job.Showcase, job.Shot).Select(step => step.Name).ToList();
        job.PathStartFrame = job.Shot.LeadFrameCount;
        job.MaxFrames = MaxFrames(job.Showcase, job.Shot);
        job.CurrentSpeed = job.Shot.Speed;
        if (!job.Shot.IsClip)
        {
            job.PathStarted = true;
            job.PathSettled = true;
            job.SettleFrameIndex = 0;
            job.TailRemaining = 0;
        }

        writeError = null;
        job.Stage = Stage.Capturing;
        RimRunnerFilmClock.Arm(job.Shot.Speed, job.Shot.Fps, FrameReady, DoFrame);
        RimRunnerFilmAudio.MarkFirstFrame();
        RimRunnerFilmMusic.Start(job.Shot.Music);
        Publish();
    }

    private static List<RimRunnerSceneStep> PathSteps(RimRunnerScene scene, RimRunnerShot shot)
    {
        if (shot.Through == null)
        {
            return [];
        }

        List<string> path = RimRunnerSceneStepTree.PathTo(scene.Steps, shot.Through);
        int start = 0;
        if (shot.Setup != null)
        {
            int index = path.IndexOf(shot.Setup);
            if (index >= 0)
            {
                start = index + 1;
            }
        }

        return path.Skip(start)
            .Select(name => scene.Steps.First(step => string.Equals(step.Name, name, StringComparison.Ordinal)))
            .ToList();
    }

    private static int MaxFrames(RimRunnerScene scene, RimRunnerShot shot)
    {
        int frames = shot.LeadFrameCount + shot.TailFrameCount;
        foreach (RimRunnerSceneStep step in PathSteps(scene, shot))
        {
            frames++;
            int ticks = step.DwellTicks;
            if (step.Until != null)
            {
                ticks += step.WithinTicks > 0 ? step.WithinTicks : scene.DefaultWithinTicks;
            }

            int ticksPerFrame = shot.TicksPerFrame(shot.SpeedForStep(step));
            if (ticksPerFrame > 0)
            {
                frames += (ticks + ticksPerFrame - 1) / ticksPerFrame;
            }
        }

        return frames;
    }

    private static bool FrameReady()
    {
        return Volatile.Read(ref pendingWrites) < MaxPendingWrites;
    }

    private static int DoFrame()
    {
        if (job == null || job.Stage != Stage.Capturing)
        {
            return 0;
        }

        job.HadTarget |= RimRunnerHiddenWindowMouse.HasTarget;
        try
        {
            if (writeError is string unfed)
            {
                Fail("帧未送入编码器：" + unfed);
                return 0;
            }

            RimRunnerShot shot = job.Shot!;
            int index = job.FrameIndex;
            job.PendingStills.Clear();
            if (job.HoldAct)
            {
                job.HoldAct = false;
                RimRunnerSceneHost.Chain?.Advance();
                if (AbortAt(job.FrameIndex))
                {
                    return 0;
                }
            }

            if (SegmentSpeed(job) > 0)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            }

            if (index == 0)
            {
                RimRunnerSceneSeed.Pin(job.Seed);
            }

            job.CurrentSpeed = SegmentSpeed(job);
            job.BadgeName = BadgeName(job);

            int advanced = 0;
            if (index > 0)
            {
                int advanceSpeed = SegmentSpeed(job);
                RimRunnerFilmClock.SetSpeed(advanceSpeed);
                advanced = shot.TicksPerFrame(advanceSpeed);
                for (int i = 0; i < advanced; i++)
                {
                    Find.TickManager.DoSingleTick();
                }

                job.Ticks += advanced;
                if (AbortAt(index))
                {
                    return advanced;
                }
            }

            if (shot.IsPath && index == job.PathStartFrame - 1)
            {
                job.PendingStills.Add(0);
            }

            if (!job.PathStarted && index >= job.PathStartFrame)
            {
                job.PathStarted = true;
                int stepsBefore = job.StepCount;
                job.StepStartFrame = index;
                job.StepStartTick = TicksGame;
                RimRunnerSceneHost.RunPath(job.Path);
                if (AbortAt(index))
                {
                    return advanced;
                }

                if (RimRunnerSceneHost.Chain is not { Active: true })
                {
                    bool ranSteps = job.StepCount > stepsBefore;
                    job.PathSettled = true;
                    job.SettleFrameIndex = ranSteps ? index : index - 1;
                    job.TailRemaining = shot.TailFrameCount;
                    if (!ranSteps && shot.TailFrameCount == 0)
                    {
                        Finish();
                        return advanced;
                    }
                }
            }

            if (job.PathStarted && !job.PathSettled && RimRunnerSceneHost.Chain is not { Active: true })
            {
                job.PathSettled = true;
                job.SettleFrameIndex = index;
                job.TailRemaining = shot.TailFrameCount;
            }

            RimRunnerFilmClock.SetSpeed(SegmentSpeed(job));

            if (TryCamera(index, out Vector3 position, out float size, out _))
            {
                RimRunnerFilmClock.LockCamera(position, size);
            }

            RimRunnerScreenshotDriver.StartCapture(CaptureAtEndOfFrame(job, index));
            return advanced;
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("拍摄逐帧抛出异常：" + exception);
            Fail("第 " + job.FrameIndex + " 帧抛出异常：" + exception.GetType().Name + ": " + exception.Message);
            return 0;
        }
    }

    private static int SegmentSpeed(Job owner)
    {
        RimRunnerShot shot = owner.Shot!;
        if (!owner.PathStarted || owner.PathSettled)
        {
            return shot.Speed;
        }

        if (RimRunnerSceneHost.Chain is not { Active: true } chain)
        {
            return shot.Speed;
        }

        RimRunnerSceneStep? step = owner.Showcase.Steps
            .FirstOrDefault(item => string.Equals(item.Name, chain.StepName, StringComparison.Ordinal));
        return step == null ? shot.Speed : shot.SpeedForStep(step);
    }

    private static string BadgeName(Job owner)
    {
        return RimRunnerSceneHost.Chain is { Active: true } chain ? chain.StepName : owner.LastStepName ?? "立场";
    }

    private static bool AbortAt(int index)
    {
        if (job == null || RimRunnerSceneHost.StepAborts.Count <= job.SeenAborts)
        {
            return false;
        }

        job.SeenAborts = RimRunnerSceneHost.StepAborts.Count;
        Fail("第 " + index + " 帧时" + RimRunnerSceneHost.StepAborts[^1]);
        return true;
    }

    private static void OnStepEvent(RimRunnerStepEvent stepEvent)
    {
        if (job == null || job.Stage != Stage.Capturing)
        {
            return;
        }

        switch (stepEvent.Kind)
        {
            case RimRunnerStepEventKind.ActDone when stepEvent.Ticks != 0:
                job.Warnings.Add(new RimRunnerAssertEntry
                {
                    label = "步骤「" + stepEvent.StepName + "」的动作内同步推进 " + stepEvent.Ticks + " tick",
                    expected = string.Empty,
                    actual = "未录入片段",
                    pass = true,
                    source = "RimRunnerStepChain",
                    sceneKind = RimRunnerLiveContract.SceneFinding.FilmNote
                });
                break;
            case RimRunnerStepEventKind.StepDone:
                job.StepCount++;
                job.Steps.Add(new RimRunnerFilmStepSpan(stepEvent.StepName, job.StepStartFrame, job.FrameIndex,
                    TicksGame - job.StepStartTick));
                job.StepStartFrame = job.FrameIndex + 1;
                job.StepStartTick = TicksGame;
                job.LastStepName = stepEvent.StepName;
                job.HoldAct = true;
                if (job.Shot is { IsPath: true })
                {
                    job.PendingStills.Add(job.StepCount);
                }

                break;
        }
    }

    private static IEnumerator CaptureAtEndOfFrame(Job owner, int index)
    {
        yield return new WaitForEndOfFrame();
        CaptureNow(owner, index);
    }

    private static void CaptureNow(Job owner, int index)
    {
        if (job != owner || job.Stage != Stage.Capturing)
        {
            return;
        }

        try
        {
            RimRunnerShotOutput output = job!.Shot!.Output;
            IScreenCaptureBackend backend = ScreenCaptureBackend.Current;
            if (backend == null)
            {
                Fail("运行时没有读屏后端");
                return;
            }

            if (!backend.TryReadScreen(out byte[] rgbaTopDown, out int width, out int height))
            {
                Fail("第 " + index + " 帧无法读取屏幕画面");
                return;
            }

            if (width != output.Width || height != output.Height)
            {
                Fail("第 " + index + " 帧读到的画面是 " + width + "×" + height + "，与 " + output.Width + "×" + output.Height + " 不匹配");
                return;
            }

            if (!RimRunnerPipeLink.Connected)
            {
                Fail("本实例未接入实例管道，帧无交付去处");
                return;
            }

            int[] stills = job.PendingStills.ToArray();
            NoteHoverAtCapture(owner, stills);
            int seq = job.Seq;
            Interlocked.Increment(ref pendingWrites);
            RimRunnerPipeLink.Emit(RimRunnerPipeProtocol.Events.FilmFrame, writer =>
            {
                writer.WriteNumber(RimRunnerPipeProtocol.Keys.Seq, seq);
                writer.WriteNumber(RimRunnerPipeProtocol.Keys.Index, index);
                if (stills.Length > 0)
                {
                    writer.WriteStartArray(RimRunnerPipeProtocol.Keys.Still);
                    foreach (int still in stills)
                    {
                        writer.WriteNumberValue(still);
                    }

                    writer.WriteEndArray();
                }
            }, binary: rgbaTopDown, counted: false);

            job.FrameIndex = index + 1;
            if (job.PathSettled)
            {
                if (job.TailRemaining > 0 && index > job.SettleFrameIndex)
                {
                    job.TailRemaining--;
                }

                if (job.TailRemaining <= 0)
                {
                    Finish();
                    return;
                }
            }
            else if (job.FrameIndex > job.MaxFrames)
            {
                Fail("超过帧数上限 " + job.MaxFrames);
                return;
            }

            RimRunnerFilmClock.FrameCaptured();
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("拍摄取像抛出异常：" + exception);
            Fail("第 " + index + " 帧取像抛出异常：" + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void NoteHoverAtCapture(Job owner, int[] stills)
    {
        if (RimRunnerHiddenWindowMouse.LastIssue is { } issue && owner.HoverIssuesNoted.Add(issue))
        {
            string target = RimRunnerHiddenWindowMouse.TargetLabel;
            owner.Warnings.Add(new RimRunnerAssertEntry
            {
                label = issue switch
                {
                    HoverIssue.OffMap => "悬停目标不在当前地图上，鼠标位于画面外",
                    HoverIssue.CellOutOfBounds => "悬停格不在当前地图界内",
                    HoverIssue.OffScreen => "悬停目标在画面外",
                    HoverIssue.Fogged => "悬停目标在迷雾下，原版不登记它的 tooltip"
                },
                expected = string.Empty,
                actual = target,
                pass = true,
                source = "RimRunnerHiddenWindowMouse",
                sceneKind = RimRunnerLiveContract.SceneFinding.FilmNote
            });
        }

        bool still = owner.Shot is { IsClip: false } || stills.Length > 0;
        if (!still || owner.PendingTipNoted || !RimRunnerHiddenWindowMouse.HasTarget
            || RimRunnerUiLevels.Applied != RimRunnerUiLevel.Screen)
        {
            return;
        }

        if (RimRunnerTooltipState.PendingSeconds() is not { } seconds)
        {
            return;
        }

        string where = stills.Length > 0 ? "静帧 " + stills[^1] + "，" : string.Empty;
        owner.Warnings.Add(new RimRunnerAssertEntry
        {
            label = "取像时鼠标下的 tooltip 尚未出现",
            expected = string.Empty,
            actual = where + "还差 " + (int)Math.Ceiling(seconds * GenTicks.TicksPerRealSecond) + " tick",
            pass = true,
            source = "RimRunnerFilmSession",
            sceneKind = RimRunnerLiveContract.SceneFinding.FilmNote
        });
        owner.PendingTipNoted = true;
    }

    internal static Thing? ResolveFollowed(Thing thing)
    {
        if (thing is Pawn { Dead: true, Corpse: not null } dead)
        {
            thing = dead.Corpse;
        }

        return thing.SpawnedParentOrMe;
    }

    private static bool TryCamera(int index, out Vector3 position, out float size,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? lost)
    {
        RimRunnerShot shot = job!.Shot!;
        float aspect = shot.Output.Width / (float)shot.Output.Height;
        bool found = TryTarget(shot.Camera.Start, aspect, out position, out size, out lost);
        if (found && shot.Camera.End != null)
        {
            float seconds = shot.Camera is RimRunnerShotPan pan ? pan.Seconds : 0f;
            float t = seconds > 0f ? Mathf.Clamp01(index / (seconds * shot.Fps)) : 1f;
            found = TryTarget(shot.Camera.End, aspect, out Vector3 endPosition, out float endSize, out lost);
            position = Vector3.Lerp(position, endPosition, t);
            size = Mathf.Lerp(size, endSize, t);
        }

        if (!found)
        {
            position = job.LastPos;
            size = job.LastSize;
            return index > 0;
        }

        job.LastPos = position;
        job.LastSize = size;
        return true;
    }

    private static bool TryTarget(RimRunnerShotCamera camera, float aspect, out Vector3 position, out float size,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? lost)
    {
        position = default;
        size = 0f;
        lost = null;
        if (camera.Kind == RimRunnerShotCamera.TargetKind.Cell)
        {
            position = RimRunnerCurrentMapProbe.EngineCurrentMap() is Map current ? RimRunnerDrawnPosition.CellCenter(current, camera.Cell) : camera.Cell.ToVector3Shifted();
            size = camera.Size;
            return true;
        }

        Thing? thing = camera.Kind == RimRunnerShotCamera.TargetKind.Focus
            ? RimRunnerSceneHost.SceneFocus
            : RimRunnerSceneHost.Scene?.RegisteredOrNull(camera.Key!);
        if (thing == null)
        {
            lost = camera.Kind == RimRunnerShotCamera.TargetKind.Focus
                ? "机位的聚焦对象不存在"
                : "机位的登记键「" + camera.Key + "」没有登记对象（RegisterPawn / RegisterThing）";
            return false;
        }

        thing = ResolveFollowed(thing);
        if (thing == null || !thing.Spawned || thing.Map != RimRunnerCurrentMapProbe.EngineCurrentMap())
        {
            lost = "机位跟随的对象不在当前地图上";
            return false;
        }

        CellRect occupied = thing.OccupiedRect();
        position = RimRunnerDrawnPosition.ThingCenter(thing);
        size = Mathf.Max(occupied.Height / 2f + camera.Size, (occupied.Width / 2f + camera.Size) / aspect);
        return true;
    }

    private static int TicksGame => Find.TickManager?.TicksGame ?? 0;

    internal static void DrawSpeedBadge()
    {
        if (job == null || job.Stage != Stage.Capturing || job.Shot is not { IsPath: true } || job.Clean)
        {
            return;
        }

        DrawShadowedLabel(new Rect(UI.screenWidth - BadgeWidth - BadgeMargin, BadgeMargin, BadgeWidth, BadgeHeight),
            TextAnchor.UpperRight, job.BadgeName + " · ×" + job.CurrentSpeed);
    }

    internal static void RequestStamp()
    {
        if (job?.Stamp is not { } stamp)
        {
            return;
        }

        GameFont fontBefore = Text.Font;
        Text.Font = GameFont.Small;
        Vector2 size = Text.CalcSize(stamp);
        Text.Font = fontBefore;
        stampSize = new Vector2(size.x + StampPadding * 2f, size.y + StampPadding);
        Find.WindowStack.ImmediateWindow(StampWindowId,
            new Rect(BadgeMargin, UI.screenHeight - stampSize.y - BadgeMargin, stampSize.x, stampSize.y),
            WindowLayer.Super, DrawStamp, doBackground: false, shadowAlpha: 0f);
    }

    private static Vector2 stampSize;

    private static void DrawStamp()
    {
        if (job is not { Stage: Stage.Capturing, Stamp: { } stamp })
        {
            return;
        }

        Widgets.DrawBoxSolid(new Rect(0f, 0f, stampSize.x, stampSize.y), StampBackground);
        DrawShadowedLabel(new Rect(StampPadding, StampPadding / 2f, stampSize.x, stampSize.y), TextAnchor.UpperLeft,
            stamp);
    }

    private static void DrawShadowedLabel(Rect rect, TextAnchor anchor, string text)
    {
        GameFont fontBefore = Text.Font;
        TextAnchor anchorBefore = Text.Anchor;
        Color colorBefore = GUI.color;
        try
        {
            Text.Font = GameFont.Small;
            Text.Anchor = anchor;
            GUI.color = Color.black;
            Widgets.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text);
            GUI.color = Color.white;
            Widgets.Label(rect, text);
        }
        finally
        {
            Text.Font = fontBefore;
            Text.Anchor = anchorBefore;
            GUI.color = colorBefore;
        }
    }

    private const int StampWindowId = 0x52524653;
    private const float StampPadding = 4f;
    private static readonly Color StampBackground = new(0f, 0f, 0f, 0.8f);
    private const float BadgeWidth = 260f;
    private const float BadgeHeight = 28f;
    private const float BadgeMargin = 8f;

    private static void Fail(string line)
    {
        if (job == null)
        {
            return;
        }

        job.Failure ??= line;
        Finish();
    }

    private static void Finish()
    {
        if (job == null || job.Stage == Stage.Draining)
        {
            return;
        }

        if (job.Failure == null)
        {
            RimRunnerFilmAudio.Write();
        }

        RimRunnerFilmAudio.Reset();
        RimRunnerFilmMusic.Release();
        RimRunnerFilmClock.Disarm();
        RimRunnerUiLevels.Restore();

        if (Current.ProgramState == ProgramState.Playing)
        {
            Find.TickManager.Pause();
        }

        if (job.LogCapture != null)
        {
            job.Errors = RimRunnerLogMonitor.DiffSince(job.LogCapture).Select(line => line.Text).ToList();
        }

        job.Stage = Stage.Draining;
        Publish();
    }

    private static void Complete()
    {
        try
        {
            RimRunnerSceneHost.Cleanup();
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("拍摄清场抛出异常：" + exception);
            job!.Failure ??= "清场抛出异常：" + exception.GetType().Name + ": " + exception.Message;
        }

        string? error = writeError;
        writeError = null;
        if (error != null && job!.Failure == null)
        {
            job.Failure = "帧未送入编码器：" + error;
        }

        Job owner = job!;
        if (owner.Shot is { } shot && owner.HadTarget && shot.Camera.Ui != RimRunnerUiLevel.Screen)
        {
            string level = shot.Camera.Ui.ToString();
            owner.Warnings.Add(new RimRunnerAssertEntry
            {
                label = "悬停时界面档不是 Screen，不绘制 tooltip、格读数与温度",
                expected = string.Empty,
                actual = level,
                pass = true,
                source = "RimRunnerFilmSession",
                sceneKind = RimRunnerLiveContract.SceneFinding.FilmNote
            });
        }

        resultJson = RimRunnerLiveServer.BuildFilmResultJson(new RimRunnerFilmResult
        {
            Seq = job!.Seq,
            IsShot = job.IsShot,
            Id = job.Showcase.Id,
            Shot = job.Shot?.Name,
            Step = job.Step,
            Failure = job.Failure,
            AbortReason = RimRunnerSceneHost.AbortLine,
            Warnings = [.. RimRunnerSceneHost.Warnings, .. job.Warnings],
            Steps = job.Steps.ToList(),
            Messages = RimRunnerSceneHost.Messages.ToList(),
            StagingErrors = RimRunnerSceneHost.StagingErrors.ToList(),
            StepAborts = RimRunnerSceneHost.StepAborts.ToList(),
            Errors = job.Errors,
            StartupErrors = TakeUnreported(unreportedStartupErrors),
            LoadErrors = TakeUnreported(unreportedLoadErrors),
            Reloaded = job.Reloaded,
            Frames = job.FrameIndex,
            Ticks = job.Ticks,
            Width = job.Shot?.Output.Width ?? 0,
            Height = job.Shot?.Output.Height ?? 0,
            Seed = job.Seed,
            SceneSeed = job.SceneSeed,
            TimeUnpinned = job.Stage >= Stage.Capturing ? RimRunnerFilmClock.TimeUnpinned : null,
            ElapsedMs = job.Clock.ElapsedMilliseconds
        });
        job = null;
        Publish();
        string result = resultJson;
        RimRunnerPipeLink.Emit(RimRunnerPipeProtocol.Events.FilmResult, writer =>
        {
            writer.WritePropertyName(RimRunnerPipeProtocol.Keys.Result);
            writer.WriteRawValue(result);
        });
    }

    private static List<string> TakeUnreported(List<string> pending)
    {
        List<string> taken = [.. pending];
        pending.Clear();
        return taken;
    }

    private static void Publish()
    {
        statusJson = RimRunnerLiveServer.BuildFilmStatusJson(ready, job != null || endSeq != 0, lastSeq, resultJson);
    }
}

internal sealed class RimRunnerFilmResult
{
    internal int Seq;
    internal required bool IsShot;
    internal required string Id;
    internal string? Shot;
    internal string? Step;
    internal string? Failure;
    internal string? AbortReason;
    internal required List<RimRunnerAssertEntry> Warnings;
    internal required List<RimRunnerFilmStepSpan> Steps;
    internal required List<string> Messages;
    internal required List<string> StagingErrors;
    internal required List<string> StepAborts;
    internal required List<string> Errors;
    internal List<string> StartupErrors = [];
    internal List<string> LoadErrors = [];
    internal bool Reloaded;
    internal int Frames;
    internal int Ticks;
    internal int Width;
    internal int Height;
    internal int Seed;
    internal int SceneSeed;
    internal string? TimeUnpinned;
    internal long ElapsedMs;

    internal bool Ok => Failure == null && AbortReason == null && StagingErrors.Count == 0 && StepAborts.Count == 0
                        && Errors.Count == 0 && TimeUnpinned == null;
}

internal readonly record struct RimRunnerFilmStepSpan(string Name, int StartFrame, int EndFrame, int Ticks);

[HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
internal static class Patch_UIRootPlay_UIRootOnGUI_RimRunnerFilm
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Film;
    }

    private static void Postfix()
    {
        RimRunnerFilmSession.DrawSpeedBadge();
        RimRunnerFilmSession.RequestStamp();
    }
}
