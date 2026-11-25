using UnityEngine.InputSystem;

namespace RimRunner;

internal sealed class RimRunnerDriveKey : RimRunnerDriveVerb
{
    private static readonly (string Param, KeyCode Code, string Label)[] Modifiers =
        [("ctrl", KeyCode.LeftControl, "Ctrl"), ("shift", KeyCode.LeftShift, "Shift"), ("alt", KeyCode.LeftAlt, "Alt")];

    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Key;

    internal override string? Refusal(RimRunnerDriveOp op) => Resolve(op, out _, out _, out _);

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        Resolve(op, out KeyCode code, out Key key, out List<Key> modifiers);
        string[] held = [.. Modifiers.Where(modifier => op.Arg(modifier.Param) == "true").Select(modifier => modifier.Label)];
        RimRunnerInputScene scene = new();
        foreach (RimRunnerWait wait in RimRunnerLivePointer.PressKey(key, modifiers))
        {
            yield return wait;
        }

        op.Line("按下并松开 " + string.Join("+", [.. held, code.ToString()]));
        if (RimRunnerLivePointer.DispatchedKeys is not { } dispatched || RimRunnerLivePointer.CtrlTextKeys is not { } ctrlText)
        {
            op.Line("无法读取宿主 ImguiDispatch 的按键名单，无法判断宿主是否为 " + code + " 派发 IMGUI 按键事件");
        }
        else if (!dispatched.Contains(code) && op.Arg("ctrl") == "true" && ctrlText.Contains(code))
        {
            op.Line("宿主只在文本框获得焦点并请求该键时为 Ctrl+" + code + " 派发 IMGUI 按键事件");
        }
        else if (!dispatched.Contains(code))
        {
            op.Line("宿主不为 " + code + " 派发 IMGUI 按键事件，只有直接读 Input 的代码能看到它");
        }

        scene.Report(op);
    }

    private static string? Resolve(RimRunnerDriveOp op, out KeyCode code, out Key key, out List<Key> modifiers)
    {
        key = default;
        modifiers = [];
        string name = op.Arg("key")!.Trim();
        if (!Enum.TryParse(name, ignoreCase: true, out code) || int.TryParse(name, out _) || !Enum.IsDefined(code))
        {
            return "KeyCode 里没有「" + name + "」";
        }

        if (RimRunnerLivePointer.KeyOf(code) is not { } found)
        {
            return "仿制面的键盘上没有 " + code;
        }

        key = found;
        foreach ((string param, KeyCode modifierCode, _) in Modifiers)
        {
            if (op.Arg(param) != "true")
            {
                continue;
            }

            if (RimRunnerLivePointer.KeyOf(modifierCode) is not { } modifier)
            {
                return "仿制面的键盘上没有 " + modifierCode;
            }

            modifiers.Add(modifier);
        }

        return null;
    }
}

internal sealed class RimRunnerDriveWindowClose : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.WindowClose;

    internal static IReadOnlyList<Window> Listed() => [.. Find.WindowStack.Windows.Where(window => window is not ImmediateWindow)];

    internal override string? Refusal(RimRunnerDriveOp op) => Resolve(op.Arg("window")!, out _, out _);

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        Resolve(op.Arg("window")!, out Window? window, out int index);
        RimRunnerInputScene scene = new();
        window!.Close();
        op.Line("对第 " + index + " 个窗口调用 Close");
        scene.Report(op);
        if (Find.WindowStack.IsOpen(window))
        {
            op.Abort("Close 之后窗口仍在栈里");
        }
        yield break;
    }

    private static string? Resolve(string key, out Window? window, out int index)
    {
        window = null;
        index = 0;
        IReadOnlyList<Window> windows = Listed();
        if (int.TryParse(key, out int number))
        {
            if (number < 1 || number > windows.Count)
            {
                return "窗口栈只有 " + windows.Count + " 个窗口，没有第 " + number + " 个";
            }

            index = number;
            window = windows[number - 1];
            return null;
        }

        int[] hits = [.. Enumerable.Range(0, windows.Count).Where(i => windows[i].GetType().Name == key)];
        if (hits.Length != 1)
        {
            return hits.Length == 0
                ? "窗口栈里没有类型为 " + key + " 的窗口；有：" + (windows.Count == 0 ? "无" : string.Join("、", windows.Select(item => item.GetType().Name)))
                : "窗口栈里类型为 " + key + " 的有 " + hits.Length + " 个（第 " + string.Join("、", hits.Select(i => i + 1)) + " 个）";
        }

        index = hits[0] + 1;
        window = windows[hits[0]];
        return null;
    }
}

internal sealed class RimRunnerDriveTab : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Tab;

    internal override string? Refusal(RimRunnerDriveOp op) => Resolve(op.Arg("name")!, out _, out _);

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        Resolve(op.Arg("name")!, out MainButtonDef? def, out Vector2 screen);
        RimRunnerInputScene scene = new();
        foreach (RimRunnerWait wait in RimRunnerLivePointer.Click(screen, 0))
        {
            yield return wait;
        }

        op.Line("鼠标点主标签页按钮「" + def!.LabelCap + "」（" + def.defName + "，" + RimRunnerLivePointer.DescribeScreen(screen) + "）");
        scene.Report(op);
    }

    private static string? Resolve(string name, out MainButtonDef? def, out Vector2 screen)
    {
        screen = default;
        MainButtonDef[] hits = [.. DefDatabase<MainButtonDef>.AllDefs.Where(candidate =>
            candidate.defName == name || candidate.LabelCap.ToString() == name || candidate.label == name)];
        def = hits.Length == 1 ? hits[0] : null;
        if (def == null)
        {
            string[] shown = [.. DefDatabase<MainButtonDef>.AllDefs.Where(candidate => candidate.Worker.Visible)
                .Select(candidate => candidate.defName + "「" + candidate.LabelCap + "」")];
            return (hits.Length == 0 ? "没有 defName 或文字为「" + name + "」的主标签页按钮" : "「" + name + "」匹配到 " + hits.Length + " 个主标签页按钮")
                   + "；此刻显示的有：" + string.Join("、", shown);
        }

        if (!def.Worker.Visible)
        {
            return "主标签页按钮 " + def.defName + " 此刻不显示";
        }

        if (def.Worker.Disabled)
        {
            return "主标签页按钮 " + def.defName + " 此刻禁用";
        }

        if (RimRunnerDrawnPoints.Get(def) is not { } point)
        {
            return "主标签页按钮 " + def.defName + " 此刻未绘制";
        }

        if (Find.WindowStack.GetWindowAt(RimRunnerLivePointer.ToUi(point)) is { } cover)
        {
            return "主标签页按钮 " + def.defName + " 被窗口 " + RimRunnerGizmos.DescribeWindow(cover) + " 遮挡";
        }

        screen = point;
        return null;
    }
}
