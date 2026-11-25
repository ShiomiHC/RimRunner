using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using RimWorld.Planet;
using UnityEngine.InputSystem;

namespace RimRunner;

internal static class RimRunnerLivePointer
{
    internal static Vector2? Held { get; private set; }

    internal static bool InFlight { get; private set; }

    private static Waylith.MainThreadSynchronizationContext? context;

    internal static void Release() => Held = null;

    internal static string? Locate(Map map, IntVec3? cell, Pawn? pawn, out Vector2 screen, out string label)
    {
        Vector3 world;
        if (pawn != null)
        {
            world = RimRunnerDrawnPosition.ThingCenter(pawn);
            label = RimRunnerDrivePawns.Describe(pawn);
        }
        else
        {
            world = RimRunnerDrawnPosition.CellCenter(map, cell!.Value);
            label = "格 (" + cell.Value.x + "," + cell.Value.z + ")";
        }

        Vector3 point = Find.Camera.WorldToScreenPoint(world);
        screen = new Vector2(point.x, point.y);
        if (screen.x < 0f || screen.x >= UnityEngine.Screen.width || screen.y < 0f || screen.y >= UnityEngine.Screen.height)
        {
            return label + " 在画面外（视野 " + RimRunnerDriveCamera.Describe(RimRunnerCurrentMapProbe.EngineViewRect()) + "）";
        }

        Window? cover = Find.WindowStack.GetWindowAt(ToUi(screen));
        return cover == null ? null : label + " 被窗口 " + RimRunnerGizmos.DescribeWindow(cover) + " 遮挡";
    }

    internal static string? LocateTile(string id, out PlanetTile tile, out Vector2 screen, out string label)
    {
        tile = PlanetTile.Invalid;
        screen = default;
        label = "地块 " + id;
        if (!WorldRendererUtility.WorldSelected)
        {
            return "世界视图未打开";
        }

        PlanetLayer layer = PlanetLayer.Selected;
        int number = RimRunnerDriveTable.ParseTile(id)!.Value;
        if (number >= layer.TilesCount)
        {
            return label + " 不在当前层 " + layer.Def.defName + " 内（共 " + layer.TilesCount + " 块）";
        }

        tile = new PlanetTile(number, layer);
        label = DescribeTile(tile);
        Vector3 center = Find.WorldGrid.GetTileCenter(tile);
        Camera camera = Find.WorldCamera;
        Vector3 point = camera.WorldToScreenPoint(center);
        screen = new Vector2(point.x, point.y);
        if (point.z <= 0f || screen.x < 0f || screen.x >= UnityEngine.Screen.width || screen.y < 0f || screen.y >= UnityEngine.Screen.height)
        {
            return label + " 在画面外";
        }

        if (Vector3.Dot(center - layer.Origin, camera.transform.position - center) <= 0f)
        {
            return label + " 在星球背面";
        }

        Window? cover = Find.WindowStack.GetWindowAt(ToUi(screen));
        return cover == null ? null : label + " 被窗口 " + RimRunnerGizmos.DescribeWindow(cover) + " 遮挡";
    }

    internal static string DescribeTile(PlanetTile tile) =>
        tile.Valid ? "地块 " + tile.tileId + "（层 " + tile.Layer.Def.defName + "）" : "无地块";

    internal static Vector2 ToUi(Vector2 screen) =>
        new(screen.x / Prefs.UIScale, (UnityEngine.Screen.height - screen.y) / Prefs.UIScale);

    internal static string DescribeScreen(Vector2 screen) =>
        "屏幕点 (" + Mathf.FloorToInt(screen.x) + "," + (UnityEngine.Screen.height - 1 - Mathf.FloorToInt(screen.y)) + ")";

    internal static IEnumerable<RimRunnerWait> Click(Vector2 screen, int button)
    {
        InFlight = true;
        bool pressed = false;
        try
        {
            Move(screen);
            yield return RimRunnerWait.Frames(1);
            Post(() => InputSystem.QueueMouseButton(button, true, screen));
            pressed = true;
            yield return RimRunnerWait.Frames(1);
            Post(() => InputSystem.QueueMouseButton(button, false, screen));
            pressed = false;
            yield return RimRunnerWait.Frames(1);
        }
        finally
        {
            if (pressed)
            {
                Post(() => InputSystem.QueueMouseButton(button, false, screen));
            }

            InFlight = false;
        }
    }

    internal static IEnumerable<RimRunnerWait> Drag(Vector2 from, Vector2 to, int steps)
    {
        InFlight = true;
        bool pressed = false;
        Vector2 at = from;
        try
        {
            Move(from);
            yield return RimRunnerWait.Frames(1);
            Post(() => InputSystem.QueueMouseButton(0, true, from));
            pressed = true;
            yield return RimRunnerWait.Frames(1);
            for (int i = 1; i <= steps; i++)
            {
                at = Vector2.Lerp(from, to, (float)i / steps);
                Move(at);
                yield return RimRunnerWait.Frames(1);
            }

            Post(() => InputSystem.QueueMouseButton(0, false, to));
            pressed = false;
            yield return RimRunnerWait.Frames(1);
        }
        finally
        {
            if (pressed)
            {
                Vector2 release = at;
                Post(() => InputSystem.QueueMouseButton(0, false, release));
            }

            InFlight = false;
        }
    }

    internal static IEnumerable<RimRunnerWait> Hover(Vector2 screen)
    {
        InFlight = true;
        try
        {
            Move(screen);
            yield return RimRunnerWait.Frames(1);
        }
        finally
        {
            InFlight = false;
        }
    }

    internal static IEnumerable<RimRunnerWait> PressKey(Key key, IReadOnlyList<Key> modifiers)
    {
        InFlight = true;
        List<Key> down = [];
        try
        {
            if (modifiers.Count > 0)
            {
                down.AddRange(modifiers);
                Post(() => modifiers.ToList().ForEach(modifier => InputSystem.QueueKeyChange(modifier, true)));
                yield return RimRunnerWait.Frames(1);
            }

            down.Add(key);
            Post(() => InputSystem.QueueKeyChange(key, true));
            yield return RimRunnerWait.Frames(1);
            down.Remove(key);
            Post(() => InputSystem.QueueKeyChange(key, false));
            yield return RimRunnerWait.Frames(1);
            if (modifiers.Count > 0)
            {
                down.Clear();
                Post(() => modifiers.ToList().ForEach(modifier => InputSystem.QueueKeyChange(modifier, false)));
                yield return RimRunnerWait.Frames(1);
            }
        }
        finally
        {
            if (down.Count > 0)
            {
                Key[] release = [.. down];
                Post(() => release.ToList().ForEach(held => InputSystem.QueueKeyChange(held, false)));
            }

            InFlight = false;
        }
    }

    internal static Key? KeyOf(KeyCode code)
    {
        object?[] args = [code, null];
        return KeyControlLookup?.Invoke(null, args) is true && args[1] is UnityEngine.InputSystem.Controls.KeyControl control ? control.keyCode : null;
    }

    private static readonly MethodInfo? KeyControlLookup = AccessTools.Method(typeof(Input), "TryGetKeyControl");

    internal static HashSet<KeyCode>? DispatchedKeys { get; } = ReadDispatchedKeys("ControlKeys", "GameKeys", "ModifierKeys");

    internal static HashSet<KeyCode>? CtrlTextKeys { get; } = ReadDispatchedKeys("EditShortcutKeys");

    private static HashSet<KeyCode>? ReadDispatchedKeys(params string[] fields)
    {
        Type? dispatch = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImguiDispatch");
        HashSet<KeyCode> keys = [];
        foreach (string field in fields)
        {
            if (dispatch == null || AccessTools.Field(dispatch, field)?.GetValue(null) is not KeyCode[] list)
            {
                return null;
            }

            keys.UnionWith(list);
        }

        return keys;
    }

    private static void Move(Vector2 screen)
    {
        Held = screen;
        Post(() => InputSystem.QueueMouseMove(screen));
    }

    private static void Post(Action write)
    {
        context ??= SynchronizationContext.Current as Waylith.MainThreadSynchronizationContext
                    ?? throw new InvalidOperationException(RimRunnerLog.Prefix + " 主线程的同步上下文不是 Waylith.MainThreadSynchronizationContext（"
                                                           + (SynchronizationContext.Current?.GetType().FullName ?? "null") + "）");
        context.Post(_ => write(), null);
    }

    internal static readonly string[] WindowPointerCallbackNames =
        ["OnPointerMoved", "OnPointerExited", "OnPointerPressed", "OnPointerReleased", "OnPointerCanceled"];

    internal static readonly string[] WindowKeyCallbackNames = ["OnKeyDown", "OnKeyUp"];

    internal const string WindowInputBridgeName = "RimWorldWaylith.WindowInputBridge";

    internal static List<MethodBase> WindowInputCallbacks(params string[] names)
    {
        Type? bridge = AccessTools.TypeByName(WindowInputBridgeName);
        return bridge == null
            ? []
            : [.. names.Select(name => bridge.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)).OfType<MethodBase>()];
    }

    internal static bool PrepareWindowInputPatch(params string[] names)
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            return false;
        }

        int found = WindowInputCallbacks(names).Count;
        if (found != names.Length)
        {
            RimRunnerEngineFault.Raise("只找到 " + WindowInputBridgeName + " 的 " + found + " 个输入入口（应有 "
                                       + string.Join("、", names) + "）：live 驱动输入时不拦截真实输入事件");
            return false;
        }

        return true;
    }
}

[HarmonyPatch]
internal static class Patch_WindowInputBridge_Pointer_RimRunnerLivePointer
{
    private static bool Prepare() => RimRunnerLivePointer.PrepareWindowInputPatch(RimRunnerLivePointer.WindowPointerCallbackNames);

    private static IEnumerable<MethodBase> TargetMethods() =>
        RimRunnerLivePointer.WindowInputCallbacks(RimRunnerLivePointer.WindowPointerCallbackNames);

    private static bool Prefix(Waylith.Windowing.WindowHandlerDecorator __instance, MethodBase __originalMethod,
        Waylith.Windowing.IWindow window, in Waylith.Windowing.PointerEvent e)
    {
        if (!RimRunnerLivePointer.InFlight)
        {
            return true;
        }

        Waylith.Windowing.IWindowHandler inner = __instance.Inner;
        switch (__originalMethod.Name)
        {
            case "OnPointerMoved": inner.OnPointerMoved(window, in e); break;
            case "OnPointerExited": inner.OnPointerExited(window, in e); break;
            case "OnPointerPressed": inner.OnPointerPressed(window, in e); break;
            case "OnPointerReleased": inner.OnPointerReleased(window, in e); break;
            case "OnPointerCanceled": inner.OnPointerCanceled(window, in e); break;
            default: return true;
        }

        return false;
    }
}

[HarmonyPatch]
internal static class Patch_WindowInputBridge_Scroll_RimRunnerLivePointer
{
    private static bool Prepare() => RimRunnerLivePointer.PrepareWindowInputPatch("OnScrolled");

    private static IEnumerable<MethodBase> TargetMethods() => RimRunnerLivePointer.WindowInputCallbacks("OnScrolled");

    private static bool Prefix(Waylith.Windowing.WindowHandlerDecorator __instance, Waylith.Windowing.IWindow window,
        in Waylith.Windowing.ScrollEvent e)
    {
        if (!RimRunnerLivePointer.InFlight)
        {
            return true;
        }

        __instance.Inner.OnScrolled(window, in e);
        return false;
    }
}

[HarmonyPatch]
internal static class Patch_WindowInputBridge_Text_RimRunnerLivePointer
{
    private static bool Prepare() => RimRunnerLivePointer.PrepareWindowInputPatch("OnTextInput");

    private static IEnumerable<MethodBase> TargetMethods() => RimRunnerLivePointer.WindowInputCallbacks("OnTextInput");

    private static bool Prefix(Waylith.Windowing.WindowHandlerDecorator __instance, Waylith.Windowing.IWindow window,
        in Waylith.Windowing.TextInputEvent e)
    {
        if (!RimRunnerLivePointer.InFlight)
        {
            return true;
        }

        __instance.Inner.OnTextInput(window, in e);
        return false;
    }
}

[HarmonyPatch]
internal static class Patch_WindowInputBridge_Key_RimRunnerLivePointer
{
    private static bool Prepare() => RimRunnerLivePointer.PrepareWindowInputPatch(RimRunnerLivePointer.WindowKeyCallbackNames);

    private static IEnumerable<MethodBase> TargetMethods() =>
        RimRunnerLivePointer.WindowInputCallbacks(RimRunnerLivePointer.WindowKeyCallbackNames);

    private static bool Prefix(Waylith.Windowing.WindowHandlerDecorator __instance, MethodBase __originalMethod,
        Waylith.Windowing.IWindow window, in Waylith.Windowing.KeyEvent e, ref bool __result)
    {
        if (!RimRunnerLivePointer.InFlight)
        {
            return true;
        }

        Waylith.Windowing.IWindowHandler inner = __instance.Inner;
        switch (__originalMethod.Name)
        {
            case "OnKeyDown": __result = inner.OnKeyDown(window, in e); break;
            case "OnKeyUp": __result = inner.OnKeyUp(window, in e); break;
            default: return true;
        }

        return false;
    }
}

internal static class RimRunnerDrawnPoints
{
    private sealed class Drawn
    {
        internal Vector2 Screen;
        internal int Frame;
    }

    private static readonly ConditionalWeakTable<object, Drawn> drawn = new();

    internal static void Record(object element, Vector2 gui)
    {
        if (Event.current?.type != EventType.Repaint)
        {
            return;
        }

        Vector2 ui = UI.GUIToScreenPoint(gui);
        Drawn entry = drawn.GetOrCreateValue(element);
        entry.Screen = new Vector2(ui.x * Prefs.UIScale, UnityEngine.Screen.height - ui.y * Prefs.UIScale);
        entry.Frame = RimRunnerFrame.Current;
    }

    internal static Vector2? Get(object element) =>
        drawn.TryGetValue(element, out Drawn? entry) && RimRunnerFrame.Current - entry.Frame <= 2 ? entry.Screen : null;
}

internal static class RimRunnerFloatMenus
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "options")]
    private static extern ref List<FloatMenuOption> OptionsField(FloatMenu menu);

    internal static List<FloatMenuOption> Options(FloatMenu menu) => OptionsField(menu);

    internal static FloatMenu? Top() => Find.WindowStack?.Windows.OfType<FloatMenu>().LastOrDefault();

    internal static void Record(FloatMenuOption option, Rect rect) =>
        RimRunnerDrawnPoints.Record(option, new Vector2(rect.x + 2f, rect.y + (rect.height - 1f) / 2f));

    internal static Vector2? ClickPoint(FloatMenu menu, FloatMenuOption option) =>
        RimRunnerDrawnPoints.Get(option) is { } screen && Find.WindowStack.GetWindowAt(RimRunnerLivePointer.ToUi(screen)) == menu ? screen : null;

    internal static string? Lookup(FloatMenu menu, string key, out FloatMenuOption? option, out int index)
    {
        option = null;
        index = 0;
        List<FloatMenuOption> options = Options(menu);
        if (int.TryParse(key, out int number))
        {
            if (number < 1 || number > options.Count)
            {
                return "菜单只有 " + options.Count + " 项，没有第 " + number + " 项";
            }

            index = number;
            option = options[number - 1];
            return null;
        }

        int[] hits = [.. Enumerable.Range(0, options.Count).Where(i => options[i].Label == key)];
        if (hits.Length != 1)
        {
            return hits.Length == 0 ? "菜单里没有文字为「" + key + "」的项" : "菜单里文字为「" + key + "」的有 " + hits.Length + " 项（第 "
                + string.Join("、", hits.Select(i => i + 1)) + " 项）";
        }

        index = hits[0] + 1;
        option = options[hits[0]];
        return null;
    }
}

[HarmonyPatch(typeof(FloatMenuOption), nameof(FloatMenuOption.DoGUI))]
internal static class Patch_FloatMenuOption_DoGUI_RimRunnerFloatMenus
{
    private static bool Prepare() => GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);

    private static void Prefix(FloatMenuOption __instance, Rect rect) => RimRunnerFloatMenus.Record(__instance, rect);
}

[HarmonyPatch(typeof(MainButtonWorker), nameof(MainButtonWorker.DoButton))]
internal static class Patch_MainButtonWorker_DoButton_RimRunnerDrawnPoints
{
    private static bool Prepare() => GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);

    private static void Prefix(MainButtonWorker __instance, Rect rect) => RimRunnerDrawnPoints.Record(__instance.def, rect.center);
}
