namespace RimRunner;

internal sealed record RimRunnerCropSpec
{
    internal RimRunnerCropArea Area { get; init; }
    internal Thing[] Things { get; init; } = [];
    internal Map? Map { get; init; }
    internal CellRect Cells { get; init; }
    internal float PadCells { get; init; }
    internal float HeadroomCells { get; init; }
    internal bool Select { get; init; }
    internal bool Pane { get; init; }
    internal Type? TabType { get; init; }
    internal bool Gizmos { get; init; }
    internal Rect[] GuiRects { get; init; } = [];
    internal RimRunnerUiLevel? Ui { get; init; }

    internal bool HasScreenParts => Pane || Gizmos || GuiRects.Length > 0;

    internal RimRunnerUiLevel UiLevel =>
        Ui ?? (HasScreenParts || Area == RimRunnerCropArea.Full ? RimRunnerUiLevel.Screen : RimRunnerUiLevel.Map);

    internal bool FramesWorld => Area is RimRunnerCropArea.Things or RimRunnerCropArea.Cells;

    internal Map? TargetMap => Area switch
    {
        RimRunnerCropArea.Things => Things[0].MapHeld,
        RimRunnerCropArea.Cells => Map,
        _ => null
    };

    public bool Equals(RimRunnerCropSpec? other) =>
        other != null
        && Area == other.Area
        && Things.SequenceEqual(other.Things)
        && Map == other.Map
        && Cells.Equals(other.Cells)
        && PadCells.Equals(other.PadCells)
        && HeadroomCells.Equals(other.HeadroomCells)
        && Select == other.Select
        && Pane == other.Pane
        && TabType == other.TabType
        && Gizmos == other.Gizmos
        && GuiRects.SequenceEqual(other.GuiRects)
        && UiLevel == other.UiLevel;

    public override int GetHashCode() =>
        HashCode.Combine(Area, Things.Length, Map, Cells, PadCells, Select, Pane, HashCode.Combine(Gizmos, UiLevel, HeadroomCells));
}

internal enum RimRunnerCropArea
{
    Full,
    Things,
    Cells,
    Gui
}

/// <summary>截图取景：对准哪片世界、选不选中、哪些屏幕部件入图、画到哪一档界面。</summary>
/// <remarks>传给 ctx.AddScreenshot 出图。出图矩形是世界区域与所带屏幕部件的外接矩形，按开拍时的相机投影现算。部件未画出或区域在屏幕上不足 8 像素时记错误。界面档缺省：带屏幕部件的与 Full 取 Screen，其余取 Map。</remarks>
public class RimRunnerCrop
{
    internal readonly RimRunnerCropSpec Spec;

    internal RimRunnerCrop(RimRunnerCropSpec spec)
    {
        Spec = spec;
    }

    /// <summary>对准一个 Thing：相机框住它的占地外扩 padCells 格，开拍前选中它。</summary>
    /// <remarks>Thing 在独立小图上时切到那张图拍；视距可比游戏允许的近，最近到屏幕高度覆盖 2 格。</remarks>
    /// <param name="thing">对准的 Thing，须已生成在某张地图上。</param>
    /// <param name="padCells">占地矩形四周外扩的格数。</param>
    /// <param name="headroomCells">画面上沿再多留的格数，给墙、高建筑这类立起来画的东西留出顶部。正俯视下等于北侧多外扩 headroomCells 格。</param>
    public static RimRunnerThingCrop On(Thing thing, float padCells = 2f, float headroomCells = 0f)
    {
        if (thing == null)
        {
            throw new ArgumentNullException(nameof(thing),
                RimRunnerLog.Prefix + " RimRunnerCrop.On 须给 Thing");
        }

        return new RimRunnerThingCrop(ThingsSpec([thing], padCells, headroomCells));
    }

    /// <summary>对准一组 Thing：相机框住它们占地并集外扩 padCells 格，开拍前全部选中。</summary>
    /// <param name="things">对准的 Thing，须都在同一张地图上。</param>
    /// <param name="padCells">并集矩形四周外扩的格数。</param>
    /// <param name="headroomCells">画面上沿再多留的格数，给墙、高建筑这类立起来画的东西留出顶部。正俯视下等于北侧多外扩 headroomCells 格。</param>
    public static RimRunnerThingsCrop On(IEnumerable<Thing> things, float padCells = 2f, float headroomCells = 0f)
    {
        Thing[] list = things?.ToArray() ?? throw new ArgumentNullException(nameof(things));
        if (list.Length == 0 || list.Any(thing => thing == null))
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " RimRunnerCrop.On 的 Thing 列表须非空、不含 null", nameof(things));
        }

        return new RimRunnerThingsCrop(ThingsSpec(list, padCells, headroomCells));
    }

    /// <summary>对准地图上的一片格：相机框住这个矩形，不动选中。</summary>
    /// <param name="map">格所在的地图。</param>
    /// <param name="cells">要框住的格矩形。</param>
    /// <param name="headroomCells">画面上沿再多留的格数，给墙、高建筑这类立起来画的东西留出顶部。正俯视下等于北侧多外扩 headroomCells 格。</param>
    public static RimRunnerRegionCrop Cells(Map map, CellRect cells, float headroomCells = 0f)
    {
        if (map == null)
        {
            throw new ArgumentNullException(nameof(map), RimRunnerLog.Prefix + " RimRunnerCrop.Cells 须给地图");
        }

        return new RimRunnerRegionCrop(new RimRunnerCropSpec
        {
            Area = RimRunnerCropArea.Cells,
            Map = map,
            Cells = cells,
            HeadroomCells = Mathf.Max(headroomCells, 0f)
        });
    }

    /// <summary>拍一块屏幕矩形，保持相机与选中不变。</summary>
    /// <param name="rect">IMGUI 语义的矩形：原点左上、单位逻辑像素。</param>
    public static RimRunnerRegionCrop Gui(Rect rect) =>
        new(new RimRunnerCropSpec { Area = RimRunnerCropArea.Gui, GuiRects = [rect] });

    /// <summary>全屏，保持相机与选中不变。</summary>
    public static RimRunnerCrop Full { get; } = new(new RimRunnerCropSpec { Area = RimRunnerCropArea.Full });

    private static RimRunnerCropSpec ThingsSpec(Thing[] things, float padCells, float headroomCells) => new()
    {
        Area = RimRunnerCropArea.Things,
        Things = things,
        PadCells = Mathf.Max(padCells, 0f),
        HeadroomCells = Mathf.Max(headroomCells, 0f),
        Select = true
    };

    /// <summary>返回已换界面档的取景。</summary>
    /// <remarks>带屏幕部件（WithPane、WithGizmos、WithGui，或由 Gui 起手）的取景只收 Screen，指定别的档即抛。</remarks>
    public RimRunnerCrop WithUi(RimRunnerUiLevel ui) => new(WithUiSpec(ui));

    internal RimRunnerCropSpec WithPaneSpec() => ScreenPart(Spec with { Pane = true }, "WithPane");
    internal RimRunnerCropSpec WithGizmosSpec() => ScreenPart(Spec with { Gizmos = true }, "WithGizmos");
    internal RimRunnerCropSpec WithGuiSpec(Rect rect) => ScreenPart(Spec with { GuiRects = [.. Spec.GuiRects, rect] }, "WithGui");

    internal RimRunnerCropSpec WithUiSpec(RimRunnerUiLevel ui)
    {
        if (ui != RimRunnerUiLevel.Screen && Spec.HasScreenParts)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 取景带屏幕部件（面板、gizmo 栏或屏幕矩形），界面档只能是 Screen，给的是 "
                                                + ui + "：屏幕部件只在 Screen 档画得出来");
        }

        return Spec with { Ui = ui };
    }

    private static RimRunnerCropSpec ScreenPart(RimRunnerCropSpec spec, string modifier)
    {
        if (spec.Ui is RimRunnerUiLevel ui && ui != RimRunnerUiLevel.Screen)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 取景已选界面档 " + ui + "，不能再接 " + modifier
                                                + "：屏幕部件只在 Screen 档画得出来");
        }

        return spec;
    }
}

/// <summary>可附加屏幕部件的取景：世界区域之外再框进面板、gizmo 栏或一块屏幕矩形。</summary>
/// <remarks>由 RimRunnerCrop.Cells、Gui 起手；面板与 gizmo 栏是拍那一刻选中物的那一份。</remarks>
public class RimRunnerRegionCrop : RimRunnerCrop
{
    internal RimRunnerRegionCrop(RimRunnerCropSpec spec) : base(spec)
    {
    }

    /// <summary>把屏幕左下的 inspect 面板（含检视页标签行与已打开的检视页）一并框进来。</summary>
    public RimRunnerRegionCrop WithPane() => new(WithPaneSpec());

    /// <summary>把屏幕下方的 gizmo 栏一并框进来。</summary>
    public RimRunnerRegionCrop WithGizmos() => new(WithGizmosSpec());

    /// <summary>把一块屏幕矩形一并框进来。</summary>
    /// <param name="rect">IMGUI 语义的矩形：原点左上、单位逻辑像素。</param>
    public RimRunnerRegionCrop WithGui(Rect rect) => new(WithGuiSpec(rect));

    /// <summary>返回已换界面档的取景。</summary>
    /// <remarks>带屏幕部件的取景只收 Screen，给别的档当场抛。</remarks>
    public new RimRunnerRegionCrop WithUi(RimRunnerUiLevel ui) => new(WithUiSpec(ui));
}

/// <summary>对准一组 Thing 的取景：缺省选中它们，可改为不选中。</summary>
/// <remarks>由 RimRunnerCrop.On 起手。</remarks>
public class RimRunnerThingsCrop : RimRunnerRegionCrop
{
    internal RimRunnerThingsCrop(RimRunnerCropSpec spec) : base(spec)
    {
    }

    /// <summary>不选中对准的 Thing：拍用例体留下的选中。</summary>
    public RimRunnerThingsCrop Unselected() => new(Spec with { Select = false });

    /// <summary>把屏幕左下的 inspect 面板（含检视页标签行与已打开的检视页）一并框进来。</summary>
    public new RimRunnerThingsCrop WithPane() => new(WithPaneSpec());

    /// <summary>把屏幕下方的 gizmo 栏一并框进来。</summary>
    public new RimRunnerThingsCrop WithGizmos() => new(WithGizmosSpec());

    /// <summary>把一块屏幕矩形一并框进来。</summary>
    /// <param name="rect">IMGUI 语义的矩形：原点左上、单位逻辑像素。</param>
    public new RimRunnerThingsCrop WithGui(Rect rect) => new(WithGuiSpec(rect));

    /// <summary>返回已换界面档的取景。</summary>
    /// <remarks>带屏幕部件的取景只收 Screen，给别的档当场抛；Scene 档不画选中，对准的 Thing 仍选中。</remarks>
    public new RimRunnerThingsCrop WithUi(RimRunnerUiLevel ui) => new(WithUiSpec(ui));
}

/// <summary>对准单个 Thing 的取景：另可切到它的某个检视页。</summary>
/// <remarks>由 RimRunnerCrop.On(Thing) 起手。</remarks>
public sealed class RimRunnerThingCrop : RimRunnerThingsCrop
{
    internal RimRunnerThingCrop(RimRunnerCropSpec spec) : base(spec)
    {
    }

    /// <summary>选中对准的 Thing、切到检视页 T，面板连同检视页一并框进来。</summary>
    public RimRunnerTabCrop WithPane<T>() where T : ITab =>
        new(WithPaneSpec() with { Select = true, TabType = typeof(T) });

    /// <summary>把屏幕左下的 inspect 面板（含检视页标签行与已打开的检视页）一并框进来。</summary>
    public new RimRunnerThingCrop WithPane() => new(WithPaneSpec());

    /// <summary>把屏幕下方的 gizmo 栏一并框进来。</summary>
    public new RimRunnerThingCrop WithGizmos() => new(WithGizmosSpec());

    /// <summary>把一块屏幕矩形一并框进来。</summary>
    /// <param name="rect">IMGUI 语义的矩形：原点左上、单位逻辑像素。</param>
    public new RimRunnerThingCrop WithGui(Rect rect) => new(WithGuiSpec(rect));

    /// <summary>返回已换界面档的取景。</summary>
    /// <remarks>带屏幕部件的取景只收 Screen，给别的档当场抛；Scene 档不画选中，对准的 Thing 仍选中。</remarks>
    public new RimRunnerThingCrop WithUi(RimRunnerUiLevel ui) => new(WithUiSpec(ui));
}

/// <summary>对准单个 Thing 并切到某个检视页的取景：面板连同检视页已在图里。</summary>
/// <remarks>由 RimRunnerThingCrop.WithPane&lt;T&gt; 起手。</remarks>
public sealed class RimRunnerTabCrop : RimRunnerCrop
{
    internal RimRunnerTabCrop(RimRunnerCropSpec spec) : base(spec)
    {
    }

    /// <summary>把屏幕下方的 gizmo 栏一并框进来。</summary>
    public RimRunnerTabCrop WithGizmos() => new(WithGizmosSpec());

    /// <summary>把一块屏幕矩形一并框进来。</summary>
    /// <param name="rect">IMGUI 语义的矩形：原点左上、单位逻辑像素。</param>
    public RimRunnerTabCrop WithGui(Rect rect) => new(WithGuiSpec(rect));

    /// <summary>返回已换界面档的取景。</summary>
    /// <remarks>带屏幕部件的取景只收 Screen，给别的档当场抛。</remarks>
    public new RimRunnerTabCrop WithUi(RimRunnerUiLevel ui) => new(WithUiSpec(ui));
}
