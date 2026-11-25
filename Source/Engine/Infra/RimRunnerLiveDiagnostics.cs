using System.Text.Json;
using Verse.Steam;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    private static volatile string publishedProgramState = "Unknown";
    private static volatile bool publishedAutoTest;

    internal static void PublishDiagnosticsHeartbeat()
    {
        publishedProgramState = Current.ProgramState.ToString();
        publishedAutoTest = GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static LiveResponse QueryDiagnosticsRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly(RimRunnerLiveContract.Progress.LatestSegment);
        bool latestOnly = p.Bool(RimRunnerLiveContract.Progress.LatestSegment) == true;
        return ExecuteOnMainThread(
            () => QueryDiagnostics(latestOnly),
            allowBusy: true,
            waitMilliseconds: 500,
            timeoutResponse: () => BasicDiagnosticsResponse(mainThreadAvailable: false, latestOnly));
    }

    private static LiveResponse QueryDiagnostics(bool latestOnly)
    {
        RimRunnerRunSnapshot run = RimRunnerRunCoordinator.GetSnapshot();
        RimRunnerSessionDiagnosticSnapshot session =
            RimRunnerSession.GetDiagnosticSnapshot();
        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Progress, writer =>
        {
            AppendDiagnosticsHead(writer, run, mainThreadAvailable: true, latestOnly);
            writer.WriteNumber("frame", RimRunnerFrame.Current);
            writer.WriteNumber("gameTick", Find.TickManager?.TicksGame ?? -1);
            writer.WriteBoolean("playerHasControl", Current.Game?.PlayerHasControl == true);
            writer.WriteBoolean("forcePaused", Find.TickManager?.ForcePaused == true);
            writer.WriteString("timeSpeed", Find.TickManager?.CurTimeSpeed.ToString());
            writer.WriteBoolean("longEventWaiting", LongEventHandler.AnyEventNowOrWaiting);
            writer.WriteBoolean("steamInitialized", SteamManager.Initialized);
            writer.WriteBoolean("anomalyActive", ModsConfig.AnomalyActive);
            writer.WriteBoolean("biotechActive", ModsConfig.BiotechActive);
            writer.WriteBoolean("harmonyLoaded", AccessTools.TypeByName("HarmonyLib.Harmony") != null);
            writer.WriteBoolean("harLoaded", AccessTools.TypeByName("AlienRace.ThingDef_AlienRace") != null);
            AppendStrings(writer, "productPacks", RimRunnerDefScope.OurPackIds
                .OrderBy(id => id, StringComparer.Ordinal));
            AppendLoadedPackIds(writer);
            writer.WriteNumber("mapId", RimRunnerIsolatedMap.LiveMap?.uniqueID ?? -1);
            AppendSessionDiagnostics(writer, session);
            AppendStrings(writer, "forcePauseWindows", Find.WindowStack?.Windows
                .Where(window => window.forcePause)
                .Select(window => window.GetType().Name)
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal)
                ?? Enumerable.Empty<string>());
            writer.WriteStartArray("testPawns");
            Pawn[] testPawns = RimRunnerIsolatedMap.LiveMap?.mapPawns.AllPawnsSpawned
                .Where(RimRunnerPawns.IsTestPawn)
                .OrderBy(pawn => pawn.ThingID, StringComparer.Ordinal)
                .ToArray() ?? [];
            foreach (Pawn pawn in testPawns)
            {
                writer.WriteStartObject();
                writer.WriteString("nick", GetPawnNick(pawn));
                writer.WriteString("id", pawn.ThingID);
                writer.WriteString("job", pawn.CurJobDef?.defName);
                writer.WriteString("jobDriver", pawn.jobs?.curDriver?.GetType().Name);
                writer.WriteString("combinedDisabledWorkTags", pawn.CombinedDisabledWorkTags.ToString());
                AppendStrings(writer, "disabledWorkTypes", pawn.GetDisabledWorkTypes()
                    .Select(workType => workType.defName)
                    .OrderBy(name => name, StringComparer.Ordinal));
                writer.WriteString("position", pawn.Position.ToString());
                writer.WriteBoolean("downed", pawn.Downed);
                writer.WriteBoolean("dead", pawn.Dead);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }));
    }

    private static LiveResponse BasicDiagnosticsResponse(bool mainThreadAvailable, bool latestOnly)
    {
        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Progress, writer =>
            WriteBasicDiagnostics(writer, mainThreadAvailable, latestOnly)));
    }

    internal static void WritePulseMembers(Utf8JsonWriter writer)
    {
        writer.WriteString(RimRunnerLiveContract.KeySchema, RimRunnerLiveContract.Schema.Progress);
        WriteBasicDiagnostics(writer, mainThreadAvailable: false, latestOnly: true);
        writer.WriteNumber(RimRunnerPipeProtocol.Keys.Frame, RimRunnerFrame.Current);
        writer.WriteNumber(RimRunnerPipeProtocol.Keys.LoadMarks, RimRunnerLoadMarks.Total);
        writer.WriteNumber(RimRunnerPipeProtocol.Keys.IdleSeconds, RimRunnerActivity.IdleSeconds);
    }

    private static void WriteBasicDiagnostics(Utf8JsonWriter writer, bool mainThreadAvailable, bool latestOnly)
    {
        AppendDiagnosticsHead(writer, RimRunnerRunCoordinator.GetSnapshot(), mainThreadAvailable, latestOnly);
        AppendSessionDiagnostics(writer, RimRunnerSession.GetPublishedDiagnosticSnapshot());
    }

    private static void AppendDiagnosticsHead(
        Utf8JsonWriter writer, RimRunnerRunSnapshot run, bool mainThreadAvailable, bool latestOnly)
    {
        writer.WriteBoolean("ok", true);
        writer.WriteString(RimRunnerLiveContract.Progress.ProgramState, publishedProgramState);
        writer.WriteBoolean(RimRunnerLiveContract.Progress.MainThreadAvailable, mainThreadAvailable);
        writer.WriteBoolean("autoTest", publishedAutoTest);
        writer.WriteString("runState", run.State.ToString().ToLowerInvariant());
        writer.WriteString("activeCommand", run.Command);
        writer.WriteString("activeRunId", run.RunId);
        AppendRunPulse(writer, latestOnly);
    }

    private static void AppendSessionDiagnostics(
        Utf8JsonWriter writer, RimRunnerSessionDiagnosticSnapshot session)
    {
        writer.WriteNumber("casesDone", session.CasesDone);
        writer.WriteNumber("casesTotal", session.CasesTotal);
        writer.WriteString("currentCase", session.CurrentCaseId);
        writer.WriteNumber("phase", session.CurrentPhase);
        writer.WriteNumber("phaseCount", session.PhaseCount);
        writer.WriteNumber("assertCount", session.AssertCount);
        writer.WriteString(RimRunnerLiveContract.Progress.FrameSpeed, session.FrameSpeed);
        writer.WriteNumber(RimRunnerLiveContract.Progress.BodyTicks, session.BodyTicks);
        writer.WriteNumber(RimRunnerLiveContract.Progress.BodyFrames, session.BodyFrames);
        writer.WriteBoolean("bodyPendingFinalize", session.BodyPendingFinalize);
        writer.WriteString("pendingArtifactId", session.PendingArtifactId);
        writer.WriteString("pendingArtifactState", session.PendingArtifactState);
    }

    private static void AppendStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static LiveResponse QueueScreenshotRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly("label", "rect");
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            return ErrorResponse(403, "截图 API 仅允许 -" + RimRunnerEngineArgs.Enable + " 实例");
        }

        if (Current.ProgramState != ProgramState.Playing)
        {
            return ErrorResponse(503, "游戏当前不在 Playing 状态（ProgramState="
                                      + Current.ProgramState + "）");
        }

        string? label = p.Text("label");
        if (!TryParseScreenshotRect(p.Numbers("rect"), out Rect? rect,
                out string? rectError))
        {
            return ErrorResponse(400, rectError);
        }

        if (!RimRunnerScreenshotDriver.TryQueue(
                label, rect, out RimRunnerScreenshotArtifact? artifact,
                out string? error))
        {
            return ErrorResponse(409, error);
        }

        EnqueueMainThreadAction(() =>
            RimRunnerScreenshotDriver.BeginCapture(artifact.Id));
        return JsonResponse(202, ScreenshotArtifactJson(artifact));
    }

    private static bool TryParseScreenshotRect(IReadOnlyList<double>? raw, out Rect? rect,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        rect = null;
        error = null;
        if (raw == null)
        {
            return true;
        }

        if (raw.Count != 4)
        {
            error = "rect 需要 4 个数：[x, y, w, h]";
            return false;
        }

        float[] values = raw.Select(value => (float)value).ToArray();
        if (values[2] <= 0f || values[3] <= 0f)
        {
            error = "rect 的宽高须为正";
            return false;
        }

        rect = new Rect(values[0], values[1], values[2], values[3]);
        return true;
    }

    private static string ScreenshotArtifactJson(
        RimRunnerScreenshotArtifact artifact) =>
        InjectSchema(RimRunnerLiveContract.Schema.Screenshot, writer => WriteScreenshotMembers(writer, artifact));

    internal static void WriteScreenshotArtifact(Utf8JsonWriter writer, RimRunnerScreenshotArtifact artifact)
    {
        writer.WriteStartObject();
        writer.WriteString(RimRunnerLiveContract.KeySchema, RimRunnerLiveContract.Schema.Screenshot);
        WriteScreenshotMembers(writer, artifact);
        writer.WriteEndObject();
    }

    private static void WriteScreenshotMembers(Utf8JsonWriter writer, RimRunnerScreenshotArtifact artifact)
    {
        writer.WriteString(RimRunnerLiveContract.Screenshot.ArtifactId, artifact.Id);
        writer.WriteString(RimRunnerLiveContract.Screenshot.State, artifact.State);
        writer.WriteString("label", artifact.Label);
        writer.WriteString("fileName", artifact.FileName);
        writer.WriteString("path", artifact.Path);
        writer.WriteString("error", artifact.Error);
        writer.WriteString("runId", artifact.RunId);
        writer.WriteString("command", artifact.Command);
        writer.WriteString("requestedRunState", artifact.RequestedRunState.ToString().ToLowerInvariant());
        writer.WriteString("caseId", artifact.CaseId);
        writer.WriteString("targetId", artifact.TargetId);
        writer.WriteNumber("gameTick", artifact.GameTick);
        writer.WriteNumber("frame", artifact.Frame);
        writer.WriteNumber("completedFrame", artifact.CompletedFrame);
        writer.WriteNumber("width", artifact.Width);
        writer.WriteNumber("height", artifact.Height);
        writer.WriteNumber("screenWidth", artifact.ScreenWidth);
        writer.WriteNumber("screenHeight", artifact.ScreenHeight);
        writer.WriteNumber("uiScale", artifact.UiScale);
        writer.WriteNumber("mapId", artifact.MapId);
        writer.WriteString("cameraPosition", artifact.CameraPosition);
        writer.WriteString("cameraZoom", artifact.CameraZoom);
    }
}
