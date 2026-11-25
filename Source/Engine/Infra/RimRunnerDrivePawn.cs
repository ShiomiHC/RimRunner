namespace RimRunner;

internal static class RimRunnerDrivePawns
{
    private const int ListedNames = 20;

    internal static string? Resolve(string? names, out List<Pawn> found)
    {
        found = [];
        Map? map = RimRunnerCurrentMapProbe.EngineCurrentMap();
        if (map == null)
        {
            return "没有当前地图";
        }

        if (names == null)
        {
            return null;
        }

        IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
        foreach (string name in RimRunnerDriveTable.ParsePawnList(names)!)
        {
            if (RimRunnerLiveServer.FindSpawnedPawn(pawns, name) is not { } pawn)
            {
                string[] available = [.. pawns.Select(candidate => RimRunnerLiveServer.GetPawnNick(candidate) ?? candidate.ThingID)];
                return "当前地图上没有昵称或 ThingID 为「" + name + "」的 pawn；有：" + string.Join("、", available.Take(ListedNames))
                       + (available.Length > ListedNames ? "；其余 " + (available.Length - ListedNames) + " 个未列（总数 " + available.Length + "）" : "");
            }

            found.Add(pawn);
        }

        return null;
    }

    internal static string Describe(object selected) => selected switch
    {
        Pawn pawn => (RimRunnerLiveServer.GetPawnNick(pawn) ?? pawn.LabelShort) + "（" + pawn.ThingID + "）",
        Thing thing => thing.LabelShort + "（" + thing.ThingID + "）",
        _ => selected.ToString() ?? selected.GetType().Name,
    };

    internal static string DescribeSelection() =>
        Find.Selector.SelectedObjects.Count == 0 ? "无" : string.Join("、", Find.Selector.SelectedObjects.Select(Describe));
}

internal sealed class RimRunnerDriveSelect : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Select;

    internal override string? Refusal(RimRunnerDriveOp op) => RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out _);

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out List<Pawn> pawns);
        string before = RimRunnerDrivePawns.DescribeSelection();
        if (op.Arg("append") is not "true")
        {
            Find.Selector.ClearSelection();
        }

        foreach (Pawn pawn in pawns)
        {
            Find.Selector.Select(pawn, playSound: false, forceDesignatorDeselect: false);
        }

        op.Line("选中 " + before + " → " + RimRunnerDrivePawns.DescribeSelection());
        yield break;
    }
}

internal sealed class RimRunnerDriveDeselect : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Deselect;

    internal override string? Refusal(RimRunnerDriveOp op)
    {
        if (RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out List<Pawn> pawns) is { } refusal)
        {
            return refusal;
        }

        Pawn? stray = pawns.FirstOrDefault(pawn => !Find.Selector.IsSelected(pawn));
        return stray == null ? null : RimRunnerDrivePawns.Describe(stray) + " 未被选中；选中的是：" + RimRunnerDrivePawns.DescribeSelection();
    }

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out List<Pawn> pawns);
        string before = RimRunnerDrivePawns.DescribeSelection();
        if (op.Arg("pawn") == null)
        {
            Find.Selector.ClearSelection();
        }

        foreach (Pawn pawn in pawns)
        {
            Find.Selector.Deselect(pawn);
        }

        op.Line("选中 " + before + " → " + RimRunnerDrivePawns.DescribeSelection());
        yield break;
    }
}

internal sealed class RimRunnerDriveDraft : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Draft;

    internal override string? Refusal(RimRunnerDriveOp op)
    {
        if (RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out List<Pawn> pawns) is { } refusal)
        {
            return refusal;
        }

        Pawn pawn = pawns[0];
        if (pawn.Faction != Faction.OfPlayer)
        {
            return RimRunnerDrivePawns.Describe(pawn) + " 不属玩家派系（" + (pawn.Faction?.Name ?? "无派系") + "），无法征召";
        }

        return pawn.drafter == null ? RimRunnerDrivePawns.Describe(pawn) + " 没有征召能力（drafter 为空）" : null;
    }

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out List<Pawn> pawns);
        Pawn pawn = pawns[0];
        bool on = op.Arg("on") == "true";
        bool before = pawn.drafter.Drafted;
        pawn.drafter.Drafted = on;
        bool after = pawn.drafter.Drafted;
        op.Line(RimRunnerDrivePawns.Describe(pawn) + " 征召 " + YesNo(before) + " → " + YesNo(after));
        if (after != on)
        {
            op.Abort("设为" + YesNo(on) + "，回读仍为" + YesNo(after));
        }

        yield break;
    }

    private static string YesNo(bool value) => value ? "是" : "否";
}

internal sealed class RimRunnerDriveGizmo : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Gizmo;

    internal override string? Refusal(RimRunnerDriveOp op) => Resolve(op, out _);

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        Resolve(op, out RimRunnerGizmoButton? button);
        RimRunnerGizmoClick click = RimRunnerGizmos.Click(button!);
        op.Line("单击第 " + button!.Index + " 个按钮「" + click.Label + "」（" + button.Gizmo.GetType().Name
                + (button.GroupSize > 1 ? "，合并 " + button.GroupSize + " 个" : "") + "）；单击后选中：" + RimRunnerDrivePawns.DescribeSelection());
        if (click.ToggleBefore is { } before && click.ToggleAfter is { } after)
        {
            op.Line("开关 " + OnOff(before) + " → " + OnOff(after));
        }

        if (click.OpenedWindows.Count > 0)
        {
            op.Line("新开窗口：" + string.Join("、", click.OpenedWindows));
        }

        if (click.StartedTargeting)
        {
            op.Line("进入选目标态");
        }

        if (click.SelectedDesignator != null)
        {
            op.Line("选中指令「" + click.SelectedDesignator + "」");
        }

        yield break;
    }

    private static string? Resolve(RimRunnerDriveOp op, out RimRunnerGizmoButton? button)
    {
        button = null;
        if (RimRunnerCurrentMapProbe.EngineCurrentMap() == null)
        {
            return "没有当前地图";
        }

        if (Find.Selector.SelectedObjects.Count == 0)
        {
            return "当前没有选中，按钮栏为空";
        }

        IReadOnlyList<RimRunnerGizmoButton> bar = RimRunnerGizmos.Bar();
        string key = op.Arg("gizmo")!;
        if (int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index))
        {
            if (index < 1 || index > bar.Count)
            {
                return "序号 " + key + " 不在按钮栏上（共 " + bar.Count + " 个）";
            }

            button = bar[index - 1];
        }
        else
        {
            RimRunnerGizmoButton[] matched = [.. bar.Where(candidate => candidate.Label == key)];
            if (matched.Length == 0)
            {
                return "按钮栏上没有文字为「" + key + "」的按钮；有：" + string.Join("、", bar.Select(candidate => candidate.Label));
            }

            if (matched.Length > 1)
            {
                return "文字为「" + key + "」的按钮有 " + matched.Length + " 个：序号 " + string.Join("、", matched.Select(candidate => candidate.Index));
            }

            button = matched[0];
        }

        if (!button.Visible)
        {
            return "第 " + button.Index + " 个按钮「" + button.Label + "」处于隐藏状态";
        }

        return button.Disabled ? "第 " + button.Index + " 个按钮「" + button.Label + "」禁用：" + (button.DisabledReason ?? "无理由") : null;
    }

    private static string OnOff(bool value) => value ? "开" : "关";
}
