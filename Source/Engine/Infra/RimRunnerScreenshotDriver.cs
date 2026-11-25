using System.Collections;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using LudeonTK;
using UnityStubs.Rendering;
using Object = UnityEngine.Object;

namespace RimRunner;

internal sealed class RimRunnerScreenshotArtifact
{
    internal required string Id;
    internal required string State;
    internal required string Label;
    internal required string FileName;
    internal string? Path;
    internal string? Error;
    internal string? RunId;
    internal required string? Command;
    internal RimRunnerRunState RequestedRunState;
    internal string? CaseId;
    internal string? TargetId;
    internal int GameTick = -1;
    internal int Frame = -1;
    internal int CompletedFrame = -1;
    internal int Width;
    internal int Height;
    internal int ScreenWidth;
    internal int ScreenHeight;
    internal float UiScale;
    internal int MapId = -1;
    internal string? CameraPosition;
    internal string? CameraZoom;
    internal float CaptureStartedAt = -1f;
    internal int StartAfterFrame = -1;
    internal bool SessionOwned;
    internal required RimRunnerCropSpec Crop;
    internal string? FramingNote;
    internal DateTime QueuedAtUtc;
    internal DateTime CaptureStartedAtUtc;

    internal RimRunnerScreenshotArtifact Clone()
    {
        return (RimRunnerScreenshotArtifact)MemberwiseClone();
    }
}

internal static class RimRunnerScreenshotDriver
{
    private const int MaxRetainedArtifacts = 32;
    private const float CaptureTimeoutSeconds = 10f;
    private const int MinCaptureEdgePixels = 8;
    private const int MaxFramingPasses = 2;
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, RimRunnerScreenshotArtifact> Artifacts = new(StringComparer.Ordinal);
    private static readonly FieldInfo WindowStackWindowsField =
        AccessTools.Field(typeof(WindowStack), "windows");
    private static readonly MethodInfo WindowCloseMethod =
        AccessTools.Method(typeof(Window), "Close", [typeof(bool)]);
    private static string? activeArtifactId;
    private static volatile bool daySkyPinned;

    internal static bool CaptureActive
    {
        get
        {
            lock (Sync)
            {
                return activeArtifactId != null;
            }
        }
    }

    internal static bool DaySkyPinned => daySkyPinned;

    private sealed class CoroutineRunner : MonoBehaviour
    {
    }

    private static CoroutineRunner? coroutineRunner;

    internal static bool TryQueue(
        string? label, Rect? guiRect,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RimRunnerScreenshotArtifact? artifact,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        return TryQueueCore(
            label, null, null, sessionOwned: false,
            guiRect is Rect gui ? RimRunnerCrop.Gui(gui).Spec : RimRunnerCrop.Full.Spec,
            daySky: false, out artifact, out error);
    }

    internal static void CloseDebugWindows()
    {
        if (WindowStackWindowsField?.GetValue(Find.WindowStack) is not IEnumerable windows
            || WindowCloseMethod == null)
        {
            throw new MissingMemberException(
                RimRunnerLog.Prefix + " 无法定位 WindowStack.windows 或 Window.Close(bool)");
        }

        List<Window> debugWindows = windows.Cast<object>()
            .OfType<Window>()
            .Where(window =>
            {
                string name = window.GetType().Name;
                return name.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) >= 0
                       || name.IndexOf("DevPalette", StringComparison.OrdinalIgnoreCase) >= 0;
            })
            .ToList();
        foreach (Window window in debugWindows)
        {
            WindowCloseMethod.Invoke(window, [false]);
        }
    }

    internal static bool TryQueueSession(
        string? label,
        string? caseId,
        RimRunnerScreenshotRequest request,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RimRunnerScreenshotArtifact? artifact,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        string? targetId = request.Crop.Things.Length > 0
            ? string.Join(",", request.Crop.Things.Select(thing => thing.ThingID))
            : null;
        return TryQueueCore(
            label, caseId, targetId, sessionOwned: true, request.Crop,
            daySky: !request.ClockSky, out artifact, out error);
    }

    private static bool TryQueueCore(
        string? label,
        string? caseId,
        string? targetId,
        bool sessionOwned,
        RimRunnerCropSpec crop,
        bool daySky,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RimRunnerScreenshotArtifact? artifact,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        lock (Sync)
        {
            if (activeArtifactId != null && Artifacts.TryGetValue(activeArtifactId, out RimRunnerScreenshotArtifact? active))
            {
                ExpireUnsafe(active);
            }

            if (activeArtifactId != null)
            {
                artifact = null;
                error = "已有截图请求尚未完成：" + activeArtifactId;
                return false;
            }

            PruneCompletedArtifacts();
            RimRunnerRunSnapshot run = RimRunnerRunCoordinator.GetSnapshot();
            string id = Guid.NewGuid().ToString("N");
            string safeLabel = SanitizeLabel(label);
            string identity = sessionOwned
                ? (run.RunId ?? "norun")[..Math.Min(8, (run.RunId ?? "norun").Length)]
                  + "-" + SanitizeLabel(caseId) + "-"
                : DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-";
            artifact = new RimRunnerScreenshotArtifact
            {
                Id = id,
                State = "queued",
                Label = string.IsNullOrWhiteSpace(label) ? "capture" : label.Trim(),
                FileName = "rimrunner-" + identity + safeLabel + "-" + RimRunnerTextureTier.Current + "-" + id[..8],
                RunId = run.RunId,
                Command = run.Command,
                RequestedRunState = run.State,
                CaseId = caseId,
                TargetId = targetId,
                StartAfterFrame = sessionOwned ? RimRunnerFrame.Current + 1 : -1,
                SessionOwned = sessionOwned,
                Crop = crop,
                QueuedAtUtc = DateTime.UtcNow
            };
            Artifacts.Add(id, artifact);
            activeArtifactId = id;
            daySkyPinned = daySky;
            artifact = artifact.Clone();
            error = null;
            return true;
        }
    }

    internal static bool TryQueueFocused(
        string? label,
        string? caseId,
        RimRunnerScreenshotRequest request,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RimRunnerScreenshotArtifact? artifact,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error,
        out string? framingNote)
    {
        framingNote = null;
        if (!TryQueueSession(label, caseId, request, out artifact, out error))
        {
            return false;
        }

        bool prepared = false;
        try
        {
            ClosePreparedWindows();
            framingNote = PrepareFocus(request.Crop);
            if (request.Prepare != null)
            {
                RunPrepare(request.Prepare);
            }

            prepared = true;
            SetFramingNote(artifact.Id, framingNote);
        }
        finally
        {
            if (!prepared)
            {
                Cancel(artifact.Id, request.Prepare != null ? "截图相机编排或拍前准备失败，已释放截图槽" : "截图相机编排失败，已释放截图槽");
            }
        }

        return true;
    }

    internal static bool Preparing { get; private set; }

    private static readonly List<Window> preparedWindows = [];

    internal static void ThrowIfPreparing(string entry)
    {
        if (Preparing)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 截图的拍前准备中禁止推 tick（" + entry
                + "）：截图都在 phase 返回后拍摄，准备仅设置界面");
        }
    }

    private static void RunPrepare(Action prepare)
    {
        HashSet<Window> before = [.. Find.WindowStack.Windows];
        Preparing = true;
        try
        {
            prepare();
        }
        finally
        {
            Preparing = false;
            selectionTakenByFocus = true;
            preparedWindows.AddRange(Find.WindowStack.Windows.Where(window => !before.Contains(window)));
        }
    }

    private static void ClosePreparedWindows()
    {
        foreach (Window window in preparedWindows)
        {
            if (Find.WindowStack.IsOpen(window))
            {
                Find.WindowStack.TryRemove(window, doCloseSound: false);
            }
        }

        preparedWindows.Clear();
    }

    private static List<object>? selectionBeforeFocus;
    private static Type? inspectTabBeforeFocus;
    private static bool selectionTakenByFocus;

    internal static string? PrepareFocus(RimRunnerCropSpec crop)
    {
        string? framingNote = null;
        CloseDebugWindows();
        RimRunnerCamera.Take();
        if (selectionBeforeFocus == null)
        {
            selectionBeforeFocus = [.. Find.Selector.SelectedObjects];
            inspectTabBeforeFocus = InspectWindow.OpenTabType;
        }

        Map? map = crop.TargetMap;
        RimRunnerIsolatedMap.ShowForCapture(map);
        bool onCurrentMap = map != null && map == RimRunnerCurrentMapProbe.EngineCurrentMap();
        if (crop.FramesWorld && onCurrentMap && WorldArea(crop) is Rect area)
        {
            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
            (Vector3 center, float desired) = RimRunnerCamera.TopDownFocus(area, crop.PadCells, crop.HeadroomCells, aspect);
            framingNote = ApplyFocus(center, desired, crop, area);
        }

        if (crop is not { Area: RimRunnerCropArea.Things, Select: true } || !onCurrentMap)
        {
            if (selectionTakenByFocus)
            {
                RestoreSelectionBeforeFocus();
            }

            return framingNote;
        }

        selectionTakenByFocus = true;
        Find.Selector.ClearSelection();
        foreach (Thing thing in crop.Things.Where(thing => thing.Spawned))
        {
            Find.Selector.Select(thing, playSound: false, forceDesignatorDeselect: false);
        }

        if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect)
        {
            Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
        }

        if (crop.TabType != null)
        {
            InspectPaneUtility.OpenTab(crop.TabType);
        }

        return framingNote;
    }

    internal static Rect? WorldArea(RimRunnerCropSpec crop)
    {
        if (crop.Area == RimRunnerCropArea.Cells)
        {
            return crop.Map is Map cellsMap ? RimRunnerDrawnPosition.GroundBounds(cellsMap, crop.Cells) : null;
        }

        Rect? union = null;
        foreach (Thing thing in crop.Things)
        {
            if (!thing.Spawned)
            {
                continue;
            }

            Rect occupied = RimRunnerDrawnPosition.GroundBounds(thing.Map, thing.OccupiedRect());
            union = union is Rect sofar
                ? Rect.MinMaxRect(
                    Mathf.Min(sofar.xMin, occupied.xMin), Mathf.Min(sofar.yMin, occupied.yMin),
                    Mathf.Max(sofar.xMax, occupied.xMax), Mathf.Max(sofar.yMax, occupied.yMax))
                : occupied;
        }

        return union;
    }

    private static string? ApplyFocus(Vector3 center, float desired, RimRunnerCropSpec crop, Rect area) =>
        RimRunnerCamera.Apply(center, desired)
            ? $"框 {area.width:0.#}×{area.height:0.#} 格加边距 {crop.PadCells:0.#} 格超出最远视距，按最远视距拍摄，边缘出屏"
            : null;

    private static bool RefineFocus(string id)
    {
        RimRunnerCropSpec crop;
        lock (Sync)
        {
            if (!Artifacts.TryGetValue(id, out RimRunnerScreenshotArtifact? artifact) || artifact.State != "capturing")
            {
                return false;
            }

            crop = artifact.Crop;
        }

        if (!crop.FramesWorld || !RimRunnerCamera.Held || crop.TargetMap is not Map map || map != RimRunnerCurrentMapProbe.EngineCurrentMap()
            || WorldArea(crop) is not Rect area)
        {
            return false;
        }

        CameraDriver driver = Find.CameraDriver;
        RimRunnerCropProjection.ScreenBox box = ProjectWorldArea(crop, area);
        RimRunnerCropProjection.Framing framing = RimRunnerCropProjection.Refine(
            ToScreen, box, RimRunnerCamera.RootPos(driver), driver.RootSize, Screen.width, Screen.height);
        float reachable = Mathf.Clamp(framing.RootSize, RimRunnerCamera.MinRootSize, RimRunnerCamera.OriginalSizeRange.max);
        bool rescale = Mathf.Abs(reachable - driver.RootSize) > driver.RootSize * 0.02f;
        bool recenter = Mathf.Abs(box.CenterX - Screen.width / 2f) > Screen.width * 0.02f
                        || Mathf.Abs(box.CenterY - Screen.height / 2f) > Screen.height * 0.02f;
        if (!rescale && !recenter)
        {
            return false;
        }

        SetFramingNote(id, ApplyFocus(framing.RootPos, framing.RootSize, crop, area));
        return true;
    }

    private static Vector3 ToScreen(Vector3 world) => RimRunnerCamera.ToScreen(world);

    private static RimRunnerCropProjection.ScreenBox ProjectWorldArea(RimRunnerCropSpec crop, Rect area)
    {
        float pad = crop.PadCells;
        return RimRunnerCropProjection.Project(ToScreen,
            area.xMin - pad, area.yMin - pad, area.xMax + pad, area.yMax + pad, crop.HeadroomCells);
    }

    private static void SetFramingNote(string id, string? note)
    {
        lock (Sync)
        {
            if (Artifacts.TryGetValue(id, out RimRunnerScreenshotArtifact? artifact))
            {
                artifact.FramingNote = note;
            }
        }
    }

    private static MainTabWindow_Inspect InspectWindow =>
        (MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow;

    private static void RestoreSelectionBeforeFocus()
    {
        Find.Selector.ClearSelection();
        foreach (object selected in selectionBeforeFocus!)
        {
            Map? map = selected switch
            {
                Thing thing when !thing.Destroyed => thing.MapHeld,
                Zone zone when zone.zoneManager != null => zone.Map,
                Plan plan when plan.planManager != null => plan.Map,
                _ => null
            };
            if (map != null && map == RimRunnerCurrentMapProbe.EngineCurrentMap())
            {
                Find.Selector.Select(selected, playSound: false, forceDesignatorDeselect: false);
            }
        }

        InspectWindow.OpenTabType = inspectTabBeforeFocus;
        if (Find.Selector.NumSelected == 0)
        {
            CloseInspectPane();
        }

        selectionTakenByFocus = false;
    }

    private static void CloseInspectPane()
    {
        if (Find.MainTabsRoot.OpenTab == MainButtonDefOf.Inspect)
        {
            Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
        }
    }

    internal static void ReleaseFocus()
    {
        RimRunnerUiLevels.Restore();
        ClosePreparedWindows();
        Find.Selector.ClearSelection();
        CloseInspectPane();
        selectionBeforeFocus = null;
        inspectTabBeforeFocus = null;
        selectionTakenByFocus = false;

        RimRunnerIsolatedMap.RestoreAfterCapture();
        RimRunnerCamera.Release();
    }

    internal static void BeginCapture(string id)
    {
        RimRunnerScreenshotArtifact? artifact = null;
        RimRunnerUiLevel? level;
        try
        {
            lock (Sync)
            {
                if (!Artifacts.TryGetValue(id, out artifact)
                    || artifact.State != "queued")
                {
                    return;
                }

                if (Current.ProgramState != ProgramState.Playing)
                {
                    FailUnsafe(artifact, "截图执行时游戏不在 Playing 状态");
                    return;
                }

                if (LongEventHandler.ShouldWaitForEvent)
                {
                    FailUnsafe(artifact, "截图执行时主线程正等待 LongEvent");
                    return;
                }

                if (artifact.SessionOwned)
                {
                    RimRunnerRunSnapshot run =
                        RimRunnerRunCoordinator.GetSnapshot();
                    if (run.State != artifact.RequestedRunState
                        || !string.Equals(run.RunId, artifact.RunId, StringComparison.Ordinal)
                        || !string.Equals(run.Command, artifact.Command, StringComparison.Ordinal))
                    {
                        FailUnsafe(artifact,
                            "截图请求已过期：请求时 run=" + artifact.RequestedRunState
                                               + "/" + artifact.Command + "/" + artifact.RunId
                                               + "，执行时 run=" + run.State + "/" + run.Command + "/" + run.RunId);
                        return;
                    }
                }

                if (!artifact.SessionOwned)
                {
                    RimRunnerSessionDiagnosticSnapshot session =
                        RimRunnerSession.GetDiagnosticSnapshot();
                    artifact.CaseId = session.CurrentCaseId;
                }
                artifact.GameTick = Find.TickManager?.TicksGame ?? -1;
                artifact.Frame = RimRunnerFrame.Current;
                artifact.ScreenWidth = Screen.width;
                artifact.ScreenHeight = Screen.height;
                artifact.UiScale = Prefs.UIScale;
                artifact.MapId = RimRunnerCurrentMapProbe.EngineCurrentMap()?.uniqueID ?? -1;
                artifact.CameraPosition = Find.CameraDriver?.MapPosition.ToString();
                artifact.CameraZoom = Find.CameraDriver?.CurrentZoom.ToString();
                artifact.Path = RimRunnerOutput.Absolute(OutputPathOf(artifact));
                artifact.CaptureStartedAt = RimRunnerRealtime.Seconds;
                artifact.CaptureStartedAtUtc = DateTime.UtcNow;
                artifact.State = "capturing";
                level = artifact.SessionOwned ? artifact.Crop.UiLevel : null;
            }

            StartCapture(CaptureRoutine(id, level));
        }
        catch (Exception exception)
        {
            lock (Sync)
            {
                if (artifact != null
                    && (artifact.State == "queued" || artifact.State == "capturing"))
                {
                    FailUnsafe(artifact,
                        "截图主线程执行失败："
                        + exception.GetType().Name + ": " + exception.Message);
                }
            }

            RimRunnerEngineLog.Warn("截图 artifact " + id
                                    + " 请求失败：" + exception);
        }
    }

    internal static void StartCapture(IEnumerator routine)
    {
        if (!coroutineRunner)
        {
            GameObject host = new("RimRunnerCoroutineRunner");
            Object.DontDestroyOnLoad(host);
            coroutineRunner = host.AddComponent<CoroutineRunner>();
        }

        coroutineRunner!.StartCoroutine(routine);
    }

    private static IEnumerator CaptureRoutine(string id, RimRunnerUiLevel? level)
    {
        bool settle = false;
        if (Find.WindowStack != null && Find.WindowStack.IsOpen(typeof(EditWindow_Log)))
        {
            Find.WindowStack.TryRemove(typeof(EditWindow_Log));
            settle = true;
        }

        if (level is RimRunnerUiLevel ui)
        {
            if (RimRunnerUiLevels.Apply(ui) is string refused)
            {
                lock (Sync)
                {
                    if (Artifacts.TryGetValue(id, out RimRunnerScreenshotArtifact? artifact) && artifact.State == "capturing")
                    {
                        FailUnsafe(artifact, "截图失败：" + refused);
                    }
                }

                yield break;
            }

            settle = true;
        }

        if (settle)
        {
            int changedAt = RimRunnerFrame.Current;
            while (RimRunnerFrame.Current <= changedAt)
            {
                yield return null;
            }
        }

        for (int pass = 0; ; pass++)
        {
            yield return new WaitForEndOfFrame();
            if (pass >= MaxFramingPasses || !TryRefineFocus(id))
            {
                break;
            }

            int movedAt = RimRunnerFrame.Current;
            while (RimRunnerFrame.Current <= movedAt)
            {
                yield return null;
            }
        }

        CaptureNow(id);
        RimRunnerUiLevels.Restore();
    }

    private static bool TryRefineFocus(string id)
    {
        try
        {
            return RefineFocus(id);
        }
        catch (Exception exception)
        {
            lock (Sync)
            {
                if (Artifacts.TryGetValue(id, out RimRunnerScreenshotArtifact? artifact) && artifact.State == "capturing")
                {
                    FailUnsafe(artifact, "截图相机修正失败：" + exception.GetType().Name + ": " + exception.Message);
                }
            }

            RimRunnerEngineLog.Warn("截图 artifact " + id + " 相机修正失败：" + exception);
            return false;
        }
    }

    private static void CaptureNow(string id)
    {
        RimRunnerScreenshotArtifact? artifact;
        lock (Sync)
        {
            if (!Artifacts.TryGetValue(id, out artifact) || artifact.State != "capturing")
            {
                return;
            }
        }

        try
        {
            RectInt crop = ResolveCropRect(artifact, out string? missing);
            IScreenCaptureBackend backend = ScreenCaptureBackend.Current;
            string? unreadable = null;
            byte[]? rgbaTopDown = null;
            int frameWidth = 0;
            int frameHeight = 0;
            if (missing != null)
            {
                unreadable = missing;
            }
            else if (backend == null)
            {
                unreadable = "运行时没有读屏后端";
            }
            else if (!backend.TryReadScreen(out rgbaTopDown, out frameWidth, out frameHeight))
            {
                unreadable = "这一帧无法读取屏幕画面";
            }
            else if (crop.xMin < 0 || crop.yMin < 0 || crop.xMax > frameWidth || crop.yMax > frameHeight)
            {
                unreadable = "裁剪框 " + crop + " 超出读到的画面 " + frameWidth + "×" + frameHeight;
            }

            if (unreadable != null)
            {
                lock (Sync)
                {
                    if (artifact.State == "capturing")
                    {
                        FailUnsafe(artifact, "截图失败：" + unreadable);
                    }
                }

                RimRunnerEngineLog.Warn("截图 artifact " + id + " 出图失败：" + unreadable);
                return;
            }

            byte[] pixels = rgbaTopDown!;
            int width = frameWidth;
            int height = frameHeight;
            int frame = RimRunnerFrame.Current;
            ThreadPool.QueueUserWorkItem(_ => Finish(artifact, pixels, width, height, crop, frame));
        }
        catch (Exception exception)
        {
            lock (Sync)
            {
                if (artifact.State == "capturing")
                {
                    FailUnsafe(artifact, "截图协程执行失败："
                                         + exception.GetType().Name + ": " + exception.Message);
                }
            }

            RimRunnerEngineLog.Warn("截图 artifact " + id + " 出图失败：" + exception);
        }
    }

    private static void Finish(RimRunnerScreenshotArtifact artifact, byte[] rgbaTopDown, int frameWidth, int frameHeight,
        RectInt crop, int frame)
    {
        try
        {
            byte[] png = RimRunnerPng.Encode(rgbaTopDown, frameWidth, frameHeight, crop);
            lock (Sync)
            {
                if (artifact.State != "capturing")
                {
                    return;
                }

                if (artifact.SessionOwned)
                {
                    RimRunnerOutput.Replace(OutputPathOf(artifact), png);
                }

                artifact.Width = crop.width;
                artifact.Height = crop.height;
                artifact.State = "complete";
                artifact.CompletedFrame = frame;
                activeArtifactId = null;
                daySkyPinned = false;
                Announce(artifact, png);
            }
        }
        catch (Exception exception)
        {
            lock (Sync)
            {
                if (artifact.State == "capturing")
                {
                    FailUnsafe(artifact, "截图编码失败：" + exception.GetType().Name + ": " + exception.Message);
                }
            }

            RimRunnerEngineLog.Warn("截图 artifact " + artifact.Id + " 编码失败：" + exception);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TabRect")]
    private static extern Rect InspectTabRect(InspectTabBase tab);

    private static RectInt ResolveCropRect(RimRunnerScreenshotArtifact artifact, out string? missing)
    {
        missing = null;
        int screenWidth = Screen.width;
        int screenHeight = Screen.height;
        RimRunnerCropSpec crop = artifact.Crop;
        if (crop.Area == RimRunnerCropArea.Full)
        {
            return new RectInt(0, 0, screenWidth, screenHeight);
        }

        float x0 = float.PositiveInfinity;
        float x1 = float.NegativeInfinity;
        float y0 = float.PositiveInfinity;
        float y1 = float.NegativeInfinity;
        void Include(float left, float right, float bottom, float top)
        {
            x0 = Mathf.Min(x0, left);
            x1 = Mathf.Max(x1, right);
            y0 = Mathf.Min(y0, bottom);
            y1 = Mathf.Max(y1, top);
        }

        void IncludeGui(Rect gui)
        {
            float scale = Prefs.UIScale;
            Include(gui.xMin * scale, gui.xMax * scale, screenHeight - gui.yMax * scale, screenHeight - gui.yMin * scale);
        }

        if (crop.FramesWorld)
        {
            if (WorldArea(crop) is not Rect area)
            {
                missing = "对准的对象在拍摄时都已不在地图上";
                return default;
            }

            RimRunnerCropProjection.ScreenBox box = ProjectWorldArea(crop, area);
            float visibleWidth = Mathf.Min(box.XMax, screenWidth) - Mathf.Max(box.XMin, 0f);
            float visibleHeight = Mathf.Min(box.YMax, screenHeight) - Mathf.Max(box.YMin, 0f);
            if (visibleWidth < MinCaptureEdgePixels || visibleHeight < MinCaptureEdgePixels)
            {
                missing = $"对准的世界区域在拍摄时于屏幕内只有 {Mathf.Max(visibleWidth, 0f):0}×{Mathf.Max(visibleHeight, 0f):0} 像素，不足 {MinCaptureEdgePixels} 像素";
                return default;
            }

            Include(box.XMin, box.XMax, box.YMin, box.YMax);
        }

        foreach (Rect gui in crop.GuiRects)
        {
            IncludeGui(gui);
        }

        if (crop.Pane)
        {
            if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect || Find.Selector.NumSelected == 0)
            {
                missing = "请求 inspect 面板，拍摄时未绘制（"
                          + (Find.Selector.NumSelected == 0 ? "当时没有选中" : "当时 inspect 页未打开") + "）";
                return default;
            }

            MainTabWindow_Inspect pane = InspectWindow;
            IncludeGui(pane.windowRect);
            IncludeGui(new Rect(0f, pane.PaneTopY - 30f, InspectPaneUtility.PaneWidthFor(pane), 30f));
            InspectTabBase? open = pane.CurTabs?.FirstOrDefault(tab => tab.GetType() == pane.OpenTabType);
            if (open != null)
            {
                IncludeGui(InspectTabRect(open));
            }
        }

        if (crop.Gizmos)
        {
            float height = GizmoGridDrawer.HeightDrawnRecently;
            if (height <= 0f)
            {
                missing = "请求 gizmo 栏，拍摄时未绘制" + (Find.Selector.NumSelected == 0 ? "（当时没有选中）" : "");
                return default;
            }

            float startX = 14f;
            if (Find.WindowStack.WindowOfType<IInspectPane>() is { } inspectPane)
            {
                startX += InspectPaneUtility.PaneWidthFor(inspectPane);
            }

            IncludeGui(new Rect(startX, UI.screenHeight - height, UI.screenWidth - 147f - startX, height - 35f));
        }

        int left = Mathf.Clamp(Mathf.FloorToInt(x0), 0, screenWidth - MinCaptureEdgePixels);
        int bottom = Mathf.Clamp(Mathf.FloorToInt(y0), 0, screenHeight - MinCaptureEdgePixels);
        int width = Mathf.Clamp(
            Mathf.CeilToInt(x1) - left, MinCaptureEdgePixels, screenWidth - left);
        int height2 = Mathf.Clamp(
            Mathf.CeilToInt(y1) - bottom, MinCaptureEdgePixels, screenHeight - bottom);
        return new RectInt(left, bottom, width, height2);
    }

    internal static void Update()
    {
        string? queuedSessionId = null;
        lock (Sync)
        {
            if (activeArtifactId != null
                && Artifacts.TryGetValue(activeArtifactId, out RimRunnerScreenshotArtifact? active))
            {
                if (active.State == "queued" && active.SessionOwned
                                             && RimRunnerFrame.Current >= active.StartAfterFrame)
                {
                    queuedSessionId = active.Id;
                }
                else
                {
                    ExpireUnsafe(active);
                }
            }
        }

        if (queuedSessionId != null)
        {
            BeginCapture(queuedSessionId);
        }
    }

    internal static RimRunnerScreenshotArtifact? Get(string id)
    {
        lock (Sync)
        {
            if (!Artifacts.TryGetValue(id, out RimRunnerScreenshotArtifact? artifact))
            {
                return null;
            }

            ExpireUnsafe(artifact);
            return artifact.Clone();
        }
    }

    private static void ExpireUnsafe(RimRunnerScreenshotArtifact artifact)
    {
        DateTime startedAt = artifact.State == "capturing"
            ? artifact.CaptureStartedAtUtc
            : artifact.QueuedAtUtc;
        if ((artifact.State == "queued" || artifact.State == "capturing")
            && DateTime.UtcNow - startedAt >= TimeSpan.FromSeconds(CaptureTimeoutSeconds))
        {
            string seconds = CaptureTimeoutSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            FailUnsafe(artifact, artifact.State == "queued"
                ? $"截图请求后 {seconds} 秒内未进入主线程"
                : $"截图协程 {seconds} 秒内未出图");
        }
    }

    private static string OutputPathOf(RimRunnerScreenshotArtifact artifact) =>
        RimRunnerOutputPath.Join(RimRunnerPaths.ScreenshotsPath, artifact.FileName + ".png");

    private static void Announce(RimRunnerScreenshotArtifact artifact, byte[]? png)
    {
        if (artifact.SessionOwned)
        {
            return;
        }

        RimRunnerPipeLink.Emit(RimRunnerPipeProtocol.Events.Screenshot, writer =>
        {
            if (png != null)
            {
                writer.WriteString(RimRunnerPipeProtocol.Keys.Path, OutputPathOf(artifact));
            }

            writer.WritePropertyName(RimRunnerPipeProtocol.Keys.Snapshot);
            RimRunnerLiveServer.WriteScreenshotArtifact(writer, artifact);
        }, binary: png);
    }

    internal static RimRunnerScreenshotArtifact? Cancel(string id, string error)
    {
        lock (Sync)
        {
            if (!Artifacts.TryGetValue(id, out RimRunnerScreenshotArtifact? artifact))
            {
                return null;
            }

            if (artifact.State is "queued" or "capturing")
            {
                FailUnsafe(artifact, error);
            }

            return artifact.Clone();
        }
    }

    private static string SanitizeLabel(string? label)
    {
        string value = string.IsNullOrWhiteSpace(label) ? "capture" : label.Trim();
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = value
            .Take(48)
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character)
                ? '-'
                : character)
            .ToArray();
        string sanitized = new string(chars).Trim('-');
        return sanitized.Length == 0 ? "capture" : sanitized;
    }

    private static void FailUnsafe(
        RimRunnerScreenshotArtifact artifact, string error)
    {
        artifact.State = "error";
        artifact.Error = error;
        artifact.CompletedFrame = RimRunnerFrame.Current;
        activeArtifactId = null;
        daySkyPinned = false;
        Announce(artifact, null);
    }

    private static void PruneCompletedArtifacts()
    {
        if (Artifacts.Count < MaxRetainedArtifacts)
        {
            return;
        }

        string? removable = Artifacts.Values
            .Where(artifact => artifact.Id != activeArtifactId)
            .OrderBy(artifact => artifact.FileName, StringComparer.Ordinal)
            .Select(artifact => artifact.Id)
            .FirstOrDefault();
        if (removable != null)
        {
            Artifacts.Remove(removable);
        }
    }
}
