namespace RimRunner;

/// <summary>按钮栏原语：读当前选中对象的 gizmo 栏，并按原版点击的入口点其中一个按钮。</summary>
/// <remarks>同步调用，分组与代表按钮同原版按钮栏。点完保持选目标态与新开的窗口，两者都记在结果里。</remarks>
public static class RimRunnerGizmos
{
    /// <summary>读当前选中对象的按钮栏，按原版的排序与分组，每组一个按钮。</summary>
    /// <returns>按钮表，序号从 1 起，含隐藏的，没有选中即空表。</returns>
    public static IReadOnlyList<RimRunnerGizmoButton> Bar()
    {
        List<object> selection = [.. Find.Selector.SelectedObjects];
        var gizmos = new List<Gizmo>();
        foreach (object selected in selection)
        {
            if (selected is ISelectable selectable)
            {
                gizmos.AddRange(selectable.GetGizmos());
            }

            if (selected is Gizmo gizmo)
            {
                gizmos.Add(gizmo);
            }
        }

        foreach (object selected in selection)
        {
            if (selected is not Thing thing)
            {
                continue;
            }

            foreach (Designator designator in Find.ReverseDesignatorDatabase.AllDesignators)
            {
                if (designator.CreateReverseDesignationGizmo(thing) is { } reverse)
                {
                    gizmos.Add(reverse);
                }
            }
        }

        gizmos.SortStable((lhs, rhs) => lhs.Order.CompareTo(rhs.Order));
        var groups = new List<List<Gizmo>>();
        foreach (Gizmo gizmo in gizmos)
        {
            List<Gizmo>? group = groups.FirstOrDefault(candidate => candidate[0].GroupsWith(gizmo));
            if (group == null)
            {
                groups.Add([gizmo]);
                continue;
            }

            group.Add(gizmo);
            group[0].MergeWith(gizmo);
        }

        var buttons = new List<RimRunnerGizmoButton>();
        foreach (List<Gizmo> group in groups)
        {
            Gizmo shown = Representative(group);
            if (shown is Command_Ability ability)
            {
                ability.GroupAbilityCommands(group);
            }

            buttons.Add(new RimRunnerGizmoButton(buttons.Count + 1, shown, group, selection));
        }

        return buttons;
    }

    /// <summary>点 button：同组里随它点的先点，再点它本身与整组，同原版按钮栏的一次左键。</summary>
    /// <param name="button">Bar 读出的按钮，读栏之后选中须保持不变。</param>
    /// <returns>点之前与点之后的开关状态、新开的窗口、选目标态与选中的指令。</returns>
    /// <exception cref="InvalidOperationException">按钮隐藏时抛，按钮禁用时抛，读栏之后选中已改变也抛。</exception>
    public static RimRunnerGizmoClick Click(RimRunnerGizmoButton button)
    {
        if (!button.SameSelection(Find.Selector.SelectedObjects))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 读按钮栏之后选中已改变，按钮「" + button.Label + "」已不在这一栏上");
        }

        if (!button.Visible)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 按钮「" + button.Label + "」处于隐藏状态，原版按钮栏上无法点击");
        }

        if (button.Disabled)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 按钮「" + button.Label + "」禁用：" + (button.DisabledReason ?? "无理由"));
        }

        bool? toggleBefore = button.ToggleActive;
        HashSet<Window> windowsBefore = [.. Find.WindowStack.Windows];
        bool targetingBefore = Find.Targeter.IsTargeting;
        Designator? designatorBefore = Find.DesignatorManager.SelectedDesignator;

        Event previous = Event.current;
        var click = new Event { type = EventType.MouseDown, rawType = EventType.MouseDown, button = 0, clickCount = 1 };
        Event.current = click;
        try
        {
            Gizmo shown = button.Gizmo;
            foreach (Gizmo other in button.Group)
            {
                if (other != shown && !other.Disabled && shown.InheritInteractionsFrom(other))
                {
                    other.ProcessInput(click);
                }
            }

            shown.ProcessInput(click);
            shown.ProcessGroupInput(click, button.Group);
            click.Use();
        }
        finally
        {
            Event.current = previous;
        }

        string[] opened = [.. Find.WindowStack.Windows.Where(window => !windowsBefore.Contains(window)).Select(DescribeWindow)];
        Designator? designator = Find.DesignatorManager.SelectedDesignator;
        return new RimRunnerGizmoClick(button.Label, toggleBefore, button.ToggleActive, opened,
            !targetingBefore && Find.Targeter.IsTargeting,
            designator != null && designator != designatorBefore ? RimRunnerGizmoButton.LabelOf(designator) : null);
    }

    private static Gizmo Representative(List<Gizmo> group)
    {
        Gizmo? shown = group.FirstOrDefault(gizmo => !gizmo.Disabled);
        if (shown == null)
        {
            return group[0];
        }

        if (shown is Command_Toggle toggle)
        {
            if (!toggle.activateIfAmbiguous && !toggle.isActive())
            {
                shown = group.FirstOrDefault(gizmo => gizmo is Command_Toggle { Disabled: false } other && other.isActive()) ?? shown;
            }

            if (toggle.activateIfAmbiguous && toggle.isActive())
            {
                shown = group.FirstOrDefault(gizmo => gizmo is Command_Toggle { Disabled: false } other && !other.isActive()) ?? shown;
            }
        }

        return shown;
    }

    internal static string DescribeWindow(Window window) =>
        window is FloatMenu menu ? "右键菜单（" + RimRunnerFloatMenus.Options(menu).Count + " 个选项）" : window.GetType().Name;
}

/// <summary>按钮栏上的一个按钮：原版按钮栏上一组合成的那一个。</summary>
public sealed class RimRunnerGizmoButton
{
    private readonly object[] selection;

    internal RimRunnerGizmoButton(int index, Gizmo gizmo, List<Gizmo> group, List<object> selection)
    {
        Index = index;
        Gizmo = gizmo;
        Group = group;
        this.selection = [.. selection];
        Label = LabelOf(gizmo);
    }

    /// <summary>在栏上的序号，从 1 起。</summary>
    public int Index { get; }

    /// <summary>按钮上显示的文字，去掉富文本标记，没有文字的取类型名。</summary>
    public string Label { get; }

    /// <summary>代表这一组的按钮。</summary>
    public Gizmo Gizmo { get; }

    /// <summary>这一组里合成的按钮个数，选中多个对象时大于 1。</summary>
    public int GroupSize => Group.Count;

    /// <summary>此刻在栏上可见。</summary>
    public bool Visible => Gizmo.Visible;

    /// <summary>此刻禁用。</summary>
    public bool Disabled => Gizmo.Disabled;

    /// <summary>禁用理由，未禁用或未给出理由为 null。</summary>
    public string? DisabledReason => Gizmo.Disabled && !Gizmo.disabledReason.NullOrEmpty() ? Gizmo.disabledReason.StripTags() : null;

    /// <summary>开关型按钮此刻是否开启，不是开关型为 null。</summary>
    public bool? ToggleActive => Gizmo is Command_Toggle { isActive: { } isActive } ? isActive() : null;

    internal List<Gizmo> Group { get; }

    internal bool SameSelection(List<object> current) => current.SequenceEqual(selection);

    internal static string LabelOf(Gizmo gizmo)
    {
        string? text = gizmo is Command command ? command.LabelCap : null;
        return text.NullOrEmpty() ? gizmo.GetType().Name : text!.StripTags().Replace('\n', ' ').Trim();
    }
}

/// <summary>点一个按钮的结果：开关状态、新开的窗口、选目标态与选中的指令。</summary>
/// <param name="Label">点的按钮的文字。</param>
/// <param name="ToggleBefore">点击前是否开启，不是开关型为 null。</param>
/// <param name="ToggleAfter">点击后是否开启，不是开关型为 null。</param>
/// <param name="OpenedWindows">点之后新进栈的窗口，右键菜单附带选项个数。</param>
/// <param name="StartedTargeting">点之后进选目标态。</param>
/// <param name="SelectedDesignator">点之后新选中的指令的文字，没有新选中的指令为 null。</param>
public sealed record RimRunnerGizmoClick(string Label, bool? ToggleBefore, bool? ToggleAfter, IReadOnlyList<string> OpenedWindows,
    bool StartedTargeting, string? SelectedDesignator);
