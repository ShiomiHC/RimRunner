using LudeonTK;
using RimWorld.Planet;

namespace RimRunner;

internal static class RimRunnerDriveInput
{
    internal static string? Target(RimRunnerDriveOp op, string cellParam, string? pawnParam, out Vector2 screen, out string label,
        string? atParam = null)
    {
        if (atParam != null && op.Arg(atParam) is { } at)
        {
            return ScreenPoint(at, out screen, out label);
        }

        screen = default;
        label = "";
        Map? map = RimRunnerCurrentMapProbe.EngineCurrentMap();
        if (map == null)
        {
            return "没有当前地图";
        }

        if (WorldRendererUtility.WorldSelected)
        {
            return "世界视图已打开，画面上没有地图";
        }

        Pawn? pawn = null;
        IntVec3? cell = null;
        if (pawnParam != null && op.Arg(pawnParam) != null)
        {
            if (RimRunnerDrivePawns.Resolve(op.Arg(pawnParam), out List<Pawn> pawns) is { } missing)
            {
                return missing;
            }

            pawn = pawns[0];
        }
        else
        {
            int[] v = RimRunnerDriveTable.ParseInts(op.Arg(cellParam)!, 2)!;
            IntVec3 parsed = new(v[0], 0, v[1]);
            if (!parsed.InBounds(map))
            {
                return "格 (" + v[0] + "," + v[1] + ") 不在当前地图内（图尺寸 " + map.Size.x + "×" + map.Size.z + "）";
            }

            cell = parsed;
        }

        return RimRunnerLivePointer.Locate(map, cell, pawn, out screen, out label);
    }

    internal static string? ScreenPoint(string text, out Vector2 screen, out string label)
    {
        int[] v = RimRunnerDriveTable.ParseScreenPoint(text)!;
        label = "屏幕点 (" + v[0] + "," + v[1] + ")";
        int width = UnityEngine.Screen.width;
        int height = UnityEngine.Screen.height;
        screen = new Vector2(v[0] + 0.5f, height - v[1] - 0.5f);
        return v[0] < width && v[1] < height ? null : label + " 在画面外（画面 " + width + "×" + height + " 像素）";
    }

    internal static string Pointer(string label, Vector2 screen) =>
        "鼠标 → " + label + "（" + RimRunnerLivePointer.DescribeScreen(screen) + "），鼠标下的格 " + MouseCell();

    internal static string Landing(Vector2 screen)
    {
        Window? window = Find.WindowStack.GetWindowAt(RimRunnerLivePointer.ToUi(screen));
        if (window != null)
        {
            return "落在窗口 " + RimRunnerGizmos.DescribeWindow(window);
        }

        if (WorldRendererUtility.WorldSelected)
        {
            return "不在窗口上，其下是世界视图";
        }

        Map? map = RimRunnerCurrentMapProbe.EngineCurrentMap();
        if (map == null)
        {
            return "不在窗口上，也没有当前地图";
        }

        IntVec3 cell = UI.UIToMapPosition(screen / Prefs.UIScale).ToIntVec3();
        return cell.InBounds(map) ? "不在窗口上，其下的地图格 (" + cell.x + "," + cell.z + ")" : "不在窗口上，其下超出地图范围";
    }

    private static string MouseCell()
    {
        IntVec3 cell = UI.MouseCell();
        return "(" + cell.x + "," + cell.z + ")";
    }
}

internal sealed class RimRunnerInputScene
{
    private readonly string selection;
    private readonly HashSet<Window> windows;
    private readonly Designator? designator;
    private readonly bool targeting;
    private readonly DebugTool? tool;
    private readonly TimeSpeed speed;
    private readonly Dictionary<Pawn, string> jobs;

    internal RimRunnerInputScene()
    {
        selection = RimRunnerDrivePawns.DescribeSelection();
        windows = [.. Find.WindowStack.Windows.Where(window => window is not ImmediateWindow)];
        designator = Find.DesignatorManager.SelectedDesignator;
        targeting = Find.Targeter.IsTargeting;
        tool = DebugTools.curTool;
        speed = Find.TickManager.CurTimeSpeed;
        jobs = Find.Selector.SelectedPawns.ToDictionary(pawn => pawn, Job);
    }

    internal void Report(RimRunnerDriveOp op)
    {
        string after = RimRunnerDrivePawns.DescribeSelection();
        if (after != selection)
        {
            op.Line("选中 " + selection + " → " + after);
        }

        string[] opened = [.. Find.WindowStack.Windows.Where(window => window is not ImmediateWindow && !windows.Contains(window))
            .Select(RimRunnerGizmos.DescribeWindow)];
        if (opened.Length > 0)
        {
            op.Line("新开窗口：" + string.Join("、", opened));
        }

        string[] closed = [.. windows.Where(window => !Find.WindowStack.IsOpen(window)).Select(RimRunnerGizmos.DescribeWindow)];
        if (closed.Length > 0)
        {
            op.Line("关闭窗口：" + string.Join("、", closed));
        }

        Designator? now = Find.DesignatorManager.SelectedDesignator;
        if (now != designator)
        {
            op.Line("选中的指令 " + (designator == null ? "无" : RimRunnerGizmoButton.LabelOf(designator)) + " → "
                    + (now == null ? "无" : RimRunnerGizmoButton.LabelOf(now)));
        }

        if (Find.Targeter.IsTargeting != targeting)
        {
            op.Line(targeting ? "退出选目标态" : "进入选目标态");
        }

        if (DebugTools.curTool != tool)
        {
            op.Line("dev 工具 " + (tool == null ? "无" : "「" + RimRunnerDriveDev.ToolLabel(tool) + "」") + " → "
                    + (DebugTools.curTool == null ? "无" : "「" + RimRunnerDriveDev.ToolLabel(DebugTools.curTool) + "」"));
        }

        if (Find.TickManager.CurTimeSpeed != speed)
        {
            op.Line("游戏速度 " + RimRunnerDriveTime.SpeedName(speed) + " → " + RimRunnerDriveTime.SpeedName(Find.TickManager.CurTimeSpeed));
        }

        foreach (KeyValuePair<Pawn, string> pair in jobs)
        {
            string job = Job(pair.Key);
            if (job != pair.Value)
            {
                op.Line(RimRunnerDrivePawns.Describe(pair.Key) + " 的工作 " + pair.Value + " → " + job);
            }
        }
    }

    private static string Job(Pawn pawn) => pawn.Dead ? "（已死）" : pawn.CurJobDef?.defName ?? "无";
}

internal sealed class RimRunnerDriveClick(RimRunnerDriveTable.Verb spec, int button) : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => spec;

    internal override string? Refusal(RimRunnerDriveOp op) => RimRunnerDriveInput.Target(op, "cell", "pawn", out _, out _, "at");

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        RimRunnerDriveInput.Target(op, "cell", "pawn", out Vector2 screen, out string label, "at");
        RimRunnerInputScene scene = new();
        string? landing = op.Arg("at") != null ? RimRunnerDriveInput.Landing(screen) : null;
        foreach (RimRunnerWait wait in RimRunnerLivePointer.Click(screen, button))
        {
            yield return wait;
        }

        op.Line((landing != null ? "鼠标 → " + label + "，" + landing : RimRunnerDriveInput.Pointer(label, screen)) + "，" + (button == 0 ? "左键" : "右键") + "单击");
        scene.Report(op);
    }
}

internal sealed class RimRunnerDriveDrag : RimRunnerDriveVerb
{
    private const int Steps = 8;

    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Drag;

    internal override string? Refusal(RimRunnerDriveOp op) =>
        RimRunnerDriveInput.Target(op, "from", null, out _, out _) ?? RimRunnerDriveInput.Target(op, "to", null, out _, out _);

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        RimRunnerDriveInput.Target(op, "from", null, out Vector2 from, out string fromLabel);
        RimRunnerDriveInput.Target(op, "to", null, out Vector2 to, out string toLabel);
        RimRunnerInputScene scene = new();
        foreach (RimRunnerWait wait in RimRunnerLivePointer.Drag(from, to, Steps))
        {
            yield return wait;
        }

        op.Line("左键从" + fromLabel + "（" + RimRunnerLivePointer.DescribeScreen(from) + "）拖到" + toLabel + "（" + RimRunnerLivePointer.DescribeScreen(to) + "）");
        scene.Report(op);
    }
}

internal sealed class RimRunnerDriveHover : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Hover;

    internal override string? Refusal(RimRunnerDriveOp op) =>
        op.Arg("off") != null ? null : RimRunnerDriveInput.Target(op, "cell", "pawn", out _, out _, "at");

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        if (op.Arg("off") != null)
        {
            bool held = RimRunnerLivePointer.Held != null;
            RimRunnerLivePointer.Release();
            op.Line(!held ? "驱动未设置鼠标位置，未操作"
                : RimRunnerHiddenWindowMouse.WindowHidden ? "撤销驱动设置的鼠标位置，鼠标回到画面外" : "撤销驱动设置的鼠标位置");
            yield break;
        }

        RimRunnerDriveInput.Target(op, "cell", "pawn", out Vector2 screen, out string label, "at");
        string? landing = op.Arg("at") != null ? RimRunnerDriveInput.Landing(screen) : null;
        foreach (RimRunnerWait wait in RimRunnerLivePointer.Hover(screen))
        {
            yield return wait;
        }

        op.Line(landing != null ? "鼠标 → " + label + "，" + landing : RimRunnerDriveInput.Pointer(label, screen));
    }
}

internal sealed class RimRunnerDriveMenuPick : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.MenuPick;

    internal override string? Refusal(RimRunnerDriveOp op) => Resolve(op.Arg("option")!, out _, out _, out _, out _);

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        Resolve(op.Arg("option")!, out _, out FloatMenuOption? option, out int index, out Vector2 screen);
        foreach (RimRunnerWait wait in Pick(op, option!, index, screen))
        {
            yield return wait;
        }
    }

    internal static string? Resolve(string key, out FloatMenu? menu, out FloatMenuOption? option, out int index, out Vector2 screen)
    {
        option = null;
        index = 0;
        screen = default;
        menu = RimRunnerFloatMenus.Top();
        if (menu == null)
        {
            return "没有打开的右键菜单";
        }

        if (RimRunnerFloatMenus.Lookup(menu, key, out option, out index) is { } missing)
        {
            return missing;
        }

        if (option!.Disabled)
        {
            return "第 " + index + " 项「" + option.Label + "」已禁用";
        }

        if (RimRunnerFloatMenus.ClickPoint(menu, option) is not { } point)
        {
            return "第 " + index + " 项「" + option.Label + "」此刻未绘制在菜单的可见区内";
        }

        screen = point;
        return null;
    }

    internal static IEnumerable<RimRunnerWait> Pick(RimRunnerDriveOp op, FloatMenuOption option, int index, Vector2 screen)
    {
        RimRunnerInputScene scene = new();
        foreach (RimRunnerWait wait in RimRunnerLivePointer.Click(screen, 0))
        {
            yield return wait;
        }

        op.Line("鼠标点第 " + index + " 项「" + option.Label + "」（" + RimRunnerLivePointer.DescribeScreen(screen) + "）");
        scene.Report(op);
    }
}

internal sealed class RimRunnerDriveMenuClose : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.MenuClose;

    internal override string? Refusal(RimRunnerDriveOp op) => RimRunnerFloatMenus.Top() == null ? "没有打开的右键菜单" : null;

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        FloatMenu menu = RimRunnerFloatMenus.Top()!;
        menu.Close();
        op.Line("关闭" + RimRunnerGizmos.DescribeWindow(menu));
        yield break;
    }
}

internal sealed class RimRunnerDriveOrder : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Order;

    internal override string? Refusal(RimRunnerDriveOp op) =>
        RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out _) ?? RimRunnerDriveInput.Target(op, "cell", null, out _, out _);

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out List<Pawn> pawns);
        Pawn pawn = pawns[0];
        string before = RimRunnerDrivePawns.DescribeSelection();
        Find.Selector.ClearSelection();
        Find.Selector.Select(pawn, playSound: false, forceDesignatorDeselect: false);
        op.Line("选中 " + before + " → " + RimRunnerDrivePawns.DescribeSelection());

        if (RimRunnerDriveInput.Target(op, "cell", null, out Vector2 screen, out string label) is { } blocked)
        {
            op.Abort(blocked);
            yield break;
        }

        FloatMenu? menuBefore = RimRunnerFloatMenus.Top();
        RimRunnerInputScene scene = new();
        foreach (RimRunnerWait wait in RimRunnerLivePointer.Click(screen, 1))
        {
            yield return wait;
        }

        op.Line(RimRunnerDriveInput.Pointer(label, screen) + "，右键单击");
        FloatMenu? menu = RimRunnerFloatMenus.Top();
        if (menu == null || menu == menuBefore)
        {
            op.Line("未弹出菜单");
            scene.Report(op);
            if (op.Arg("option") != null)
            {
                op.Abort("未弹出菜单，option 未使用");
            }

            yield break;
        }

        List<FloatMenuOption> options = RimRunnerFloatMenus.Options(menu);
        op.Line("弹出右键菜单 " + options.Count + " 项：");
        for (int i = 0; i < options.Count; i++)
        {
            op.Line("  " + (i + 1) + "  " + options[i].Label + (options[i].Disabled ? " · 禁用" : ""));
        }

        if (op.Arg("option") is not { } key)
        {
            scene.Report(op);
            yield break;
        }

        if (RimRunnerDriveMenuPick.Resolve(key, out _, out FloatMenuOption? option, out int index, out Vector2 point) is { } refusal)
        {
            scene.Report(op);
            op.Abort(refusal);
            yield break;
        }

        foreach (RimRunnerWait wait in RimRunnerDriveMenuPick.Pick(op, option!, index, point))
        {
            yield return wait;
        }
    }
}
