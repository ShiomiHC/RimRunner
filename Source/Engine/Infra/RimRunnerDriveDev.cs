using System.Runtime.CompilerServices;
using LudeonTK;
using RimWorld.Planet;

namespace RimRunner;

internal sealed class RimRunnerDriveDev : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Dev;

    internal override string? Refusal(RimRunnerDriveOp op)
    {
        if (RimRunnerDevMenu.Resolve(op.Arg("path")!, out DebugActionNode node) is { } missing)
        {
            return missing;
        }

        string path = node.Path;
        if (RimRunnerDevMenu.IsMenu(node))
        {
            return path + " 是" + RimRunnerDevMenu.Kind(node);
        }

        if (!node.VisibleNow)
        {
            return path + " 此刻在原版菜单里处于隐藏状态";
        }

        if (!node.ActiveNow)
        {
            return path + " 此刻不可用（" + RimRunnerDevMenu.Kind(node) + "须相机在" + (node.actionType == DebugActionType.ToolWorld ? "世界" : "地图") + "上）";
        }

        if (op.Arg("cell") != null && (node.actionType != DebugActionType.ToolMap || RimRunnerDevMenu.Setting(node) != null))
        {
            return path + " 不是地图工具，不接受 cell";
        }

        if (op.Arg("tile") != null && node.actionType != DebugActionType.ToolWorld)
        {
            return path + " 不是世界工具，不接受 tile";
        }

        if (RimRunnerDevMenu.Setting(node) != null)
        {
            return op.Arg("on") == null ? path + " 是设置项，须 --on=true|false 指定目标值"
                : op.Arg("pawn") != null ? path + " 是设置项，不接受 pawn" : null;
        }

        if (op.Arg("on") != null)
        {
            return path + " 不是设置项，不接受 on";
        }

        switch (node.actionType)
        {
            case DebugActionType.ToolMap:
                return op.Arg("cell") == null ? path + " 是地图工具，须 --cell=<x,z> 指定要点的格"
                    : op.Arg("pawn") != null ? path + " 是地图工具，不接受 pawn"
                    : RimRunnerDriveInput.Target(op, "cell", null, out _, out _);
            case DebugActionType.ToolWorld:
                return op.Arg("tile") == null ? path + " 是世界工具，须 --tile=<地块号> 指定要点的地块"
                    : op.Arg("pawn") != null ? path + " 是世界工具，不接受 pawn"
                    : RimRunnerLivePointer.LocateTile(op.Arg("tile")!, out _, out _, out _);
            case DebugActionType.ToolMapForPawns:
                if (op.Arg("pawn") == null)
                {
                    return path + " 是对 pawn 的地图工具，须 --pawn=<名>";
                }

                return node.pawnAction == null ? path + " 没有 pawnAction" : RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out _);
            default:
                if (op.Arg("pawn") != null)
                {
                    return path + " 是动作，不接受 pawn";
                }

                return node.action == null ? path + " 没有 action" : null;
        }
    }

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        RimRunnerDevMenu.Resolve(op.Arg("path")!, out DebugActionNode node);
        HashSet<Window> windowsBefore = [.. Find.WindowStack.Windows];
        DebugTool? toolBefore = DebugTools.curTool;
        Map? map = RimRunnerCurrentMapProbe.EngineCurrentMap();
        int thingsBefore = map?.listerThings.AllThings.Count ?? 0;
        int pawnsBefore = map?.mapPawns.AllPawnsSpawnedCount ?? 0;
        int worldObjectsBefore = Find.WorldObjects.AllWorldObjects.Count;

        if (RimRunnerDevMenu.Setting(node) is { } before)
        {
            bool target = op.Arg("on") == "true";
            if (before != target)
            {
                node.action();
            }

            bool after = RimRunnerDevMenu.Setting(node)!.Value;
            op.Line(before == target ? node.Path + " 已是" + OnOff(before) + "，未执行" : node.Path + " " + OnOff(before) + " → " + OnOff(after));
            if (after != target)
            {
                op.Abort("设为" + OnOff(target) + "，回读仍为" + OnOff(after));
            }
        }
        else if (node.actionType == DebugActionType.ToolMap)
        {
            RimRunnerDriveInput.Target(op, "cell", null, out Vector2 screen, out string label);
            node.Enter(null);
            DebugTool? tool = DebugTools.curTool;
            foreach (RimRunnerWait wait in RimRunnerLivePointer.Click(screen, 0))
            {
                yield return wait;
            }

            op.Line("选用 " + node.Path + "，" + RimRunnerDriveInput.Pointer(label, screen) + "，左键单击");
            if (DebugTools.curTool == tool && tool != null)
            {
                DebugTools.curTool = null;
                op.Line("点击后收起 dev 工具");
            }
        }
        else if (node.actionType == DebugActionType.ToolWorld)
        {
            RimRunnerLivePointer.LocateTile(op.Arg("tile")!, out PlanetTile tile, out Vector2 screen, out string label);
            node.Enter(null);
            DebugTool? tool = DebugTools.curTool;
            foreach (RimRunnerWait wait in RimRunnerLivePointer.Hover(screen))
            {
                yield return wait;
            }

            PlanetTile under = GenWorld.MouseTile();
            string pointer = "鼠标 → " + label + "（" + RimRunnerLivePointer.DescribeScreen(screen) + "），鼠标下为" + RimRunnerLivePointer.DescribeTile(under);
            if (under != tile)
            {
                op.Line("选用 " + node.Path + "，" + pointer);
                if (DebugTools.curTool == tool && tool != null)
                {
                    DebugTools.curTool = null;
                    op.Line("未点击，收起 dev 工具");
                }

                op.Abort("鼠标下不是" + label);
                yield break;
            }

            foreach (RimRunnerWait wait in RimRunnerLivePointer.Click(screen, 0))
            {
                yield return wait;
            }

            op.Line("选用 " + node.Path + "，" + pointer + "，左键单击");
            if (DebugTools.curTool == tool && tool != null)
            {
                DebugTools.curTool = null;
                op.Line("点击后收起 dev 工具");
            }
        }
        else if (node.actionType == DebugActionType.ToolMapForPawns)
        {
            RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out List<Pawn> pawns);
            node.pawnAction(pawns[0]);
            op.Line("对 " + RimRunnerDrivePawns.Describe(pawns[0]) + " 执行 " + node.Path);
        }
        else
        {
            node.action();
            op.Line("执行 " + node.Path);
        }

        node.DirtyLabelCache();
        string[] opened = [.. Find.WindowStack.Windows.Where(window => !windowsBefore.Contains(window)).Select(RimRunnerGizmos.DescribeWindow)];
        if (opened.Length > 0)
        {
            op.Line("新开窗口：" + string.Join("、", opened));
        }

        if (DebugTools.curTool != null && DebugTools.curTool != toolBefore)
        {
            op.Line("留下 dev 工具「" + ToolLabel(DebugTools.curTool) + "」，点" + (node.actionType == DebugActionType.ToolWorld ? "地块" : "地图") + "才执行");
        }

        int worldObjects = Find.WorldObjects.AllWorldObjects.Count;
        if (worldObjects != worldObjectsBefore)
        {
            op.Line("世界物件 " + worldObjectsBefore + " → " + worldObjects);
        }

        if (map != null && RimRunnerCurrentMapProbe.EngineCurrentMap() == map)
        {
            int things = map.listerThings.AllThings.Count;
            int pawns = map.mapPawns.AllPawnsSpawnedCount;
            if (things != thingsBefore || pawns != pawnsBefore)
            {
                op.Line("当前地图物件 " + thingsBefore + " → " + things + "，spawned pawn " + pawnsBefore + " → " + pawns);
            }
        }

        yield break;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "label")]
    internal static extern ref string ToolLabel(DebugTool tool);

    private static string OnOff(bool value) => value ? "开" : "关";
}

internal sealed class RimRunnerDriveDevExpand : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.DevExpand;

    internal override string? Refusal(RimRunnerDriveOp op)
    {
        if (RimRunnerDevMenu.Resolve(op.Arg("path")!, out DebugActionNode node) is { } missing)
        {
            return missing;
        }

        return RimRunnerDevMenu.Unexpanded(node) ? null : node.Path + " 已展开，或不是生成子菜单（" + RimRunnerDevMenu.Kind(node) + "）";
    }

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        RimRunnerDevMenu.Resolve(op.Arg("path")!, out DebugActionNode node);
        node.TrySetupChildren();
        op.Line("展开 " + node.Path + "：" + node.children.Count + " 项");
        yield break;
    }
}
