using System.Runtime.CompilerServices;

namespace RimRunner;

/// <summary>相机原语：把当前地图的相机对准一格或一片格矩形，等相机落定后回报画面。</summary>
/// <remarks>目标图须是当前地图。用例里改动的相机位置、视距与视距范围随用例收尾还原。</remarks>
public static class RimRunnerCamera
{
    internal const float MinRootSize = 1f;

    private const float FramePadCells = 1f;

    private const int MaxRefinePasses = 2;

    private const int SettleFrames = 2;

    private const float EdgeTolerancePixels = 0.5f;

    private static (Vector3 RootPos, float RootSize, FloatRange SizeRange)? before;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "rootPos")]
    private static extern ref Vector3 CameraRootPos(CameraDriver driver);

    /// <summary>框住 area 加四周 1 格。指定 zoom 即按 zoom 取视距，相机对准 area 中心。</summary>
    /// <param name="map">目标图，须是当前地图。</param>
    /// <param name="area">要看的格矩形，须整片在图内。</param>
    /// <param name="zoom">视距，即屏幕高度覆盖的格数之半；null 时按 area 算。</param>
    /// <returns>yield 本操作推到相机落定，之后读 Result。</returns>
    /// <exception cref="InvalidOperationException">目标图不是当前地图时抛（yield 时）。</exception>
    /// <exception cref="ArgumentOutOfRangeException">area 不整片在图内或 zoom 不为正时抛（yield 时）。</exception>
    public static RimRunnerOp<RimRunnerCameraShot> Frame(Map map, CellRect area, float? zoom = null) =>
        new("camera", op => Steps(op, map, area, zoom, keepSize: false));

    /// <summary>相机对准 cell。指定 zoom 即按 zoom 取视距，未指定时视距不变。</summary>
    /// <param name="map">目标图，须是当前地图。</param>
    /// <param name="cell">要看的格，须在图内。</param>
    /// <param name="zoom">视距，即屏幕高度覆盖的格数之半；null 时保持当前视距。</param>
    /// <returns>yield 本操作推到相机落定，之后读 Result。</returns>
    /// <exception cref="InvalidOperationException">目标图不是当前地图时抛（yield 时）。</exception>
    /// <exception cref="ArgumentOutOfRangeException">cell 不在图内或 zoom 不为正时抛（yield 时）。</exception>
    public static RimRunnerOp<RimRunnerCameraShot> Frame(Map map, IntVec3 cell, float? zoom = null) =>
        new("camera", op => Steps(op, map, CellRect.SingleCell(cell), zoom, keepSize: zoom == null));

    /// <summary>按视距把 area（缺省全图）切成一屏一块，蛇形逐块对准，每块停留 dwellFrames 帧。</summary>
    /// <remarks>块按正俯视的视野大小切，自南向北逐行、行内往返。扫完相机停留在最后一块。</remarks>
    /// <param name="map">目标图，须是当前地图。</param>
    /// <param name="area">要扫的格矩形，须整片在图内；null 即全图。</param>
    /// <param name="zoom">视距，即屏幕高度覆盖的格数之半；null 时保持当前视距。</param>
    /// <param name="dwellFrames">每块停留的帧数，至少为 1。</param>
    /// <returns>yield 本操作推到扫完，之后读 Result。</returns>
    /// <exception cref="InvalidOperationException">目标图不是当前地图时抛（yield 时）。</exception>
    /// <exception cref="ArgumentOutOfRangeException">area 不整片在图内、zoom 不为正或 dwellFrames 小于 1 时抛（yield 时）。</exception>
    public static RimRunnerOp<RimRunnerSweepResult> Sweep(Map map, CellRect? area = null, float? zoom = null,
        int dwellFrames = 1) =>
        new("sweep", op => SweepSteps(op, map, area, zoom, dwellFrames));

    private static IEnumerable<RimRunnerWait> SweepSteps(
        RimRunnerOp<RimRunnerSweepResult> op, Map map, CellRect? area, float? zoom, int dwellFrames)
    {
        CellRect target = area ?? CellRect.WholeMap(map);
        Require(map, target, zoom);
        if (dwellFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dwellFrames), dwellFrames, RimRunnerLog.Prefix + " 每块停留的帧数至少为 1");
        }

        CameraDriver driver = Find.CameraDriver;
        TakeTracked();
        float size = Mathf.Clamp(zoom ?? driver.RootSize, MinRootSize, OriginalSizeRange.max);
        float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
        List<float> columns = TileCenters(target.minX, target.Width, 2f * size * aspect);
        List<float> rows = TileCenters(target.minZ, target.Height, 2f * size);
        int tiles = 0;
        for (int row = 0; row < rows.Count; row++)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                float x = row % 2 == 0 ? columns[i] : columns[columns.Count - 1 - i];
                Apply(new Vector3(x, 0f, rows[row]), size);
                tiles++;
                yield return RimRunnerWait.Frames(dwellFrames);
            }
        }

        op.SetResult(new RimRunnerSweepResult(target, tiles, tiles * dwellFrames, driver.RootSize));
    }

    private static List<float> TileCenters(int min, int count, float span)
    {
        if (count <= span)
        {
            return [min + count / 2f];
        }

        int n = Mathf.CeilToInt(count / span);
        List<float> centers = [];
        for (int i = 0; i < n; i++)
        {
            centers.Add(Mathf.Min(min + span * (i + 0.5f), min + count - span / 2f));
        }

        return centers;
    }

    private static void Require(Map map, CellRect area, float? zoom)
    {
        if (map != RimRunnerCurrentMapProbe.EngineCurrentMap())
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 相机只能对准当前地图：目标图 " + map
                                                + "，当前地图 " + RimRunnerCurrentMapProbe.EngineCurrentMap());
        }

        if (!area.InBounds(map))
        {
            throw new ArgumentOutOfRangeException(nameof(area), area, RimRunnerLog.Prefix + " 目标不整片在图内：图尺寸 " + map.Size);
        }

        if (zoom is <= 0f or float.NaN)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), zoom, RimRunnerLog.Prefix + " 视距须为正");
        }
    }

    private static void TakeTracked()
    {
        if (Take())
        {
            RimRunnerStepper.TrackRestore(Release, "还原相机位置、视距与视距范围");
        }
    }

    private static IEnumerable<RimRunnerWait> Steps(
        RimRunnerOp<RimRunnerCameraShot> op, Map map, CellRect area, float? zoom, bool keepSize)
    {
        Require(map, area, zoom);
        CameraDriver driver = Find.CameraDriver;
        RimRunnerCameraPose poseBefore = Pose(driver);
        TakeTracked();

        Rect ground = RimRunnerDrawnPosition.GroundBounds(map, area);
        float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
        (Vector3 center, float needed) = TopDownFocus(ground, FramePadCells, 0f, aspect);
        bool fit = zoom == null && !keepSize;
        Apply(center, zoom ?? (keepSize ? driver.RootSize : needed));

        for (int pass = 0; pass < MaxRefinePasses; pass++)
        {
            yield return RimRunnerWait.Frames(1);
            RimRunnerCropProjection.ScreenBox box = Project(ground, FramePadCells);
            RimRunnerCropProjection.Framing framing = RimRunnerCropProjection.Refine(
                ToScreen, box, CameraRootPos(driver), driver.RootSize, Screen.width, Screen.height);
            bool recenter = Mathf.Abs(box.CenterX - Screen.width / 2f) > Screen.width * 0.02f
                            || Mathf.Abs(box.CenterY - Screen.height / 2f) > Screen.height * 0.02f;
            float reachable = Mathf.Clamp(framing.RootSize, MinRootSize, OriginalSizeRange.max);
            bool rescale = fit && Mathf.Abs(reachable - driver.RootSize) > driver.RootSize * 0.02f;
            if (!recenter && !rescale)
            {
                break;
            }

            Apply(framing.RootPos, fit ? framing.RootSize : driver.RootSize);
        }

        yield return RimRunnerWait.Frames(SettleFrames);
        RimRunnerCropProjection.ScreenBox shown = Project(ground, 0f);
        bool visible = shown.XMin >= -EdgeTolerancePixels && shown.YMin >= -EdgeTolerancePixels
                       && shown.XMax <= Screen.width + EdgeTolerancePixels
                       && shown.YMax <= Screen.height + EdgeTolerancePixels;
        op.SetResult(new RimRunnerCameraShot(area, poseBefore, Pose(driver), visible, needed,
            OriginalSizeRange.max, RimRunnerCurrentMapProbe.EngineViewRect()));
    }

    private static RimRunnerCameraPose Pose(CameraDriver driver)
    {
        Vector3 root = CameraRootPos(driver);
        return new RimRunnerCameraPose(root.x, root.z, driver.RootSize);
    }

    internal static bool Take()
    {
        if (before != null)
        {
            return false;
        }

        CameraDriver driver = Find.CameraDriver;
        before = (CameraRootPos(driver), driver.RootSize, driver.config.sizeRange);
        return true;
    }

    internal static bool Held => before != null;

    internal static FloatRange OriginalSizeRange => before?.SizeRange ?? Find.CameraDriver.config.sizeRange;

    internal static bool Apply(Vector3 center, float desired)
    {
        FloatRange original = OriginalSizeRange;
        desired = Mathf.Max(desired, MinRootSize);
        CameraDriver driver = Find.CameraDriver;
        driver.config.sizeRange = new FloatRange(Mathf.Min(original.min, desired), original.max);
        driver.SetRootPosAndSize(center, Mathf.Min(desired, original.max));
        return desired > original.max;
    }

    internal static void Release()
    {
        if (before is not (Vector3 rootPos, float rootSize, FloatRange sizeRange))
        {
            return;
        }

        before = null;
        Find.CameraDriver.config.sizeRange = sizeRange;
        Find.CameraDriver.SetRootPosAndSize(rootPos, rootSize);
    }

    internal static void Forget() => before = null;

    internal static Vector3 RootPos(CameraDriver driver) => CameraRootPos(driver);

    internal static Vector3 ToScreen(Vector3 world) => Find.Camera.WorldToScreenPoint(world);

    private static RimRunnerCropProjection.ScreenBox Project(Rect ground, float pad) =>
        RimRunnerCropProjection.Project(ToScreen,
            ground.xMin - pad, ground.yMin - pad, ground.xMax + pad, ground.yMax + pad, 0f);

    internal static (Vector3 Center, float RootSize) TopDownFocus(Rect area, float pad, float headroom, float aspect) =>
        (new Vector3(area.center.x, 0f, area.yMin + (area.height + headroom) / 2f),
            FocusRootSize(area.width, area.height + headroom, pad, aspect));

    internal static float FocusRootSize(float width, float height, float pad, float aspect) =>
        Mathf.Max(Mathf.Max(height / 2f + pad, (width / 2f + pad) / aspect), MinRootSize);
}

/// <summary>一次扫图的结果：扫的矩形、块数、总让出帧数与扫时的视距。</summary>
/// <param name="Area">扫的格矩形。</param>
/// <param name="Tiles">块数。</param>
/// <param name="Frames">让出的总帧数，即块数乘每块停的帧数。</param>
/// <param name="Size">扫时的视距（钳进游戏视距范围之后）。</param>
public sealed record RimRunnerSweepResult(CellRect Area, int Tiles, int Frames, float Size);

/// <summary>相机的一个状态：根位置的地面坐标与视距。</summary>
/// <param name="X">根位置的 x。</param>
/// <param name="Z">根位置的 z。</param>
/// <param name="Size">视距，即屏幕高度覆盖的格数之半。</param>
public readonly record struct RimRunnerCameraPose(float X, float Z, float Size);

/// <summary>一次相机原语的结果：前后状态、目标是否完整在画面内、要多大视距与最远能给多大。</summary>
public sealed class RimRunnerCameraShot
{
    internal RimRunnerCameraShot(CellRect target, RimRunnerCameraPose before, RimRunnerCameraPose after,
        bool targetVisible, float sizeNeeded, float sizeMax, CellRect viewRect)
    {
        Target = target;
        Before = before;
        After = after;
        TargetVisible = targetVisible;
        SizeNeeded = sizeNeeded;
        SizeMax = sizeMax;
        ViewRect = viewRect;
    }

    /// <summary>要看的格矩形。</summary>
    public CellRect Target { get; }

    /// <summary>原语执行前的相机。</summary>
    public RimRunnerCameraPose Before { get; }

    /// <summary>相机落定后的状态；After.Size 即实际给的视距。</summary>
    public RimRunnerCameraPose After { get; }

    /// <summary>目标矩形整片在画面内。</summary>
    public bool TargetVisible { get; }

    /// <summary>框住目标加四周 1 格要的视距（正俯视推算）。</summary>
    public float SizeNeeded { get; }

    /// <summary>游戏的最远视距。</summary>
    public float SizeMax { get; }

    /// <summary>相机落定后的视野格矩形。</summary>
    public CellRect ViewRect { get; }
}
