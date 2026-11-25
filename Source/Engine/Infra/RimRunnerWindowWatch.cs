using System.Runtime.CompilerServices;
using LudeonTK;

namespace RimRunner;

[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add), typeof(Window))]
internal static class Patch_WindowStack_Add_RimRunnerWindowWatch
{
    private static void Postfix(WindowStack __instance, Window window)
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable) || window == null)
        {
            return;
        }

        try
        {
            RimRunnerWindowWatch.Observe(__instance, window);
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("窗口进栈观测失败（" + window.GetType().Name + "）：" + exception);
        }
    }
}

internal static class RimRunnerWindowWatch
{
    private sealed class Push
    {
        internal required Window Window;
        internal required string TypeName;
        internal int Tick;
        internal required string Origin;
        internal bool FromCase;
        internal bool FromLoadWindow;
        internal System.WeakReference<WindowStack>? Stack;
        internal bool Entered;
        internal bool StrayReported;
        internal bool Advancing;
        internal string? Pusher;

        internal bool OnStack(WindowStack stack) =>
            Stack != null && Stack.TryGetTarget(out WindowStack? pushed) && ReferenceEquals(pushed, stack);
    }

    internal sealed class StartPreview
    {
        internal string? StartLine;
        internal required List<Window> Ambient;
        internal required List<Window> Stray;
    }

    private sealed class StartScan
    {
        internal readonly List<KeyValuePair<string, string>> Entries = [];
        internal string? StartLine => Entries.Count == 0
            ? null
            : string.Join("；", Entries.Select(entry => entry.Key + "：" + entry.Value));
        internal readonly List<Push> Ambient = [];
        internal readonly List<Push> Stray = [];
    }

    private const string OutsideCaseWindow = "用例窗外";

    private static readonly ConditionalWeakTable<Window, Push> origins = [];
    private static readonly List<Push> casePushes = [];
    private static readonly List<string> pendingStray = [];
    private static readonly List<KeyValuePair<string, string>> pendingAmbient = [];
    private static readonly HashSet<string> ambientTypesThisRun = new(StringComparer.Ordinal);
    private static List<KeyValuePair<string, string>> startEntries = [];
    private static string? simulatedLoadWindow;

    internal static void Observe(WindowStack stack, Window window)
    {
        string? caseId = simulatedLoadWindow == null
            ? RimRunnerContext.AmbientPhase switch
            {
                RimRunnerPhase.Case => RimRunnerContext.Active!.Current?.id,
                RimRunnerPhase.Scene or RimRunnerPhase.Enumeration or RimRunnerPhase.None => null
            }
            : null;
        string? loadWindow = caseId == null ? simulatedLoadWindow ?? RimRunnerSession.OpenLoadWindowLabel : null;
        bool entered = stack.IsOpen(window);
        if (entered && origins.TryGetValue(window, out Push? previous) && previous.FromLoadWindow
            && previous.OnStack(stack) && !IsBlocking(window))
        {
            return;
        }

        Push push = new()
        {
            Window = window,
            TypeName = window.GetType().Name,
            Tick = Current.Game?.tickManager?.TicksGame ?? -1,
            Origin = caseId ?? loadWindow ?? OutsideCaseWindow,
            FromCase = caseId != null,
            FromLoadWindow = loadWindow != null,
            Stack = new System.WeakReference<WindowStack>(stack),
            Entered = entered,
            Advancing = RimRunnerTimeDriver.Advancing,
            Pusher = DirectPusher()
        };

        if (push.Entered)
        {
            origins.Remove(window);
            origins.Add(window, push);
        }

        if (push.FromCase)
        {
            casePushes.Add(push);
        }

        if (push.Entered && IsBlocking(window))
        {
            RimRunnerBuildSaveReloadCheck.OnBlockingWindowPushed(
                Label(window, push.TypeName) + " tick " + push.Tick + " 进栈，" + Source(push));
        }
    }

    internal static void BeginCase(string caseId)
    {
        casePushes.Clear();
        StartScan scan = ScanStart(commit: true);
        startEntries = scan.Entries;
        foreach (Push push in scan.Stray)
        {
            push.StrayReported = true;
            pendingStray.Add(DescribeOrigin(push) + "的窗口 " + Label(push.Window, push.TypeName)
                             + " 在 " + caseId + " 开始执行时仍打开");
        }

        foreach (Push push in scan.Ambient)
        {
            if (ambientTypesThisRun.Add(push.TypeName))
            {
                pendingAmbient.Add(new KeyValuePair<string, string>(push.TypeName,
                    DescribeOrigin(push) + "的不阻断窗口 " + push.TypeName + " 在 " + caseId + " 开始执行时处于打开状态"));
            }
        }
    }

    internal static StartPreview PeekStart()
    {
        StartScan scan = ScanStart(commit: false);
        return new StartPreview
        {
            StartLine = scan.StartLine,
            Ambient = scan.Ambient.Select(push => push.Window).ToList(),
            Stray = scan.Stray.Select(push => push.Window).ToList()
        };
    }

    private static StartScan ScanStart(bool commit)
    {
        StartScan scan = new();
        foreach (Window window in OpenWindows())
        {
            Push push = commit
                ? origins.GetValue(window, Unrecorded)
                : origins.TryGetValue(window, out Push? recorded) ? recorded : Unrecorded(window);
            if (push.FromLoadWindow && !IsBlocking(window))
            {
                scan.Ambient.Add(push);
                continue;
            }

            scan.Entries.Add(new KeyValuePair<string, string>(Label(window, push.TypeName) + " " + DescribeOrigin(push),
                IsBlocking(window) ? "开始执行时已在栈上。" + BlockingConsequence : "开始执行时已在栈上"));
            if (!push.FromCase && !push.StrayReported)
            {
                scan.Stray.Add(push);
            }
        }

        return scan;
    }

    private static Push Unrecorded(Window window) => new()
    {
        Window = window,
        TypeName = window.GetType().Name,
        Tick = -1,
        Origin = "未经 WindowStack.Add",
        Entered = true
    };

    internal static void EndCase()
    {
        casePushes.Clear();
        startEntries = [];
    }

    internal static void WriteCaseLines(Action<string, string> fingerprint, Action<string, string> residue, bool close = false)
    {
        foreach (KeyValuePair<string, string> entry in startEntries)
        {
            fingerprint(entry.Key, entry.Value);
        }

        if (casePushes.Count == 0)
        {
            return;
        }

        List<Push> residual = casePushes.Where(push => push.Entered && IsOpen(push.Window)).ToList();
        Dictionary<Push, string> closed = [];
        if (close)
        {
            foreach (Push push in residual.Where(push => IsBlocking(push.Window)))
            {
                closed[push] = Close(push.Window);
            }
        }

        foreach (Push push in casePushes)
        {
            fingerprint(Label(push.Window, push.TypeName) + " tick " + push.Tick + " " + Source(push),
                "本用例期间进栈（" + (closed.TryGetValue(push, out string? closedOutcome) ? "收尾时仍打开：" + closedOutcome : State(push)) + "）");
        }

        foreach (Push push in residual)
        {
            string what = "栈上窗口 " + Label(push.Window, push.TypeName) + " tick " + push.Tick + " 进栈，" + Source(push)
                          + (closed.TryGetValue(push, out string? outcome) ? "；" + outcome : "");
            residue(what, IsBlocking(push.Window) && !closed.ContainsKey(push) ? "（" + BlockingConsequence + "）" : "");
        }
    }

    private static string Close(Window window)
    {
        try
        {
            Find.WindowStack.TryRemove(window, doCloseSound: false);
        }
        catch (Exception exception)
        {
            return "引擎关窗时抛出 " + exception.GetType().Name + "：" + exception.Message;
        }

        return IsOpen(window) ? "引擎关窗后仍在栈上" : "已由引擎关闭";
    }

    private static string Source(Push push)
    {
        string pusher = push.Pusher ?? "（调用方未获取）";
        return (push.Advancing ? "推 tick 期间" : "未推 tick 时") + "由 " + pusher + " 推入";
    }

    private static string? DirectPusher()
    {
        System.Diagnostics.StackFrame[] frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
        if (frames == null)
        {
            return null;
        }

        foreach (System.Diagnostics.StackFrame frame in frames)
        {
            MethodBase? method = RimRunnerCallSite.MethodOf(frame);
            Type? type = method?.DeclaringType;
            if (method == null || type == null || type == typeof(WindowStack) || type == typeof(RimRunnerWindowWatch)
                || type == typeof(Patch_WindowStack_Add_RimRunnerWindowWatch)
                || (type.Namespace != null && (type.Namespace.StartsWith("HarmonyLib", StringComparison.Ordinal)
                                               || type.Namespace.StartsWith("MonoMod", StringComparison.Ordinal))))
            {
                continue;
            }

            while (type.DeclaringType != null && type.Name.StartsWith("<", StringComparison.Ordinal))
            {
                type = type.DeclaringType;
            }

            return Owner(type.Assembly) + " 的 " + type.Name + "." + method.Name;
        }

        return null;
    }

    private static readonly Dictionary<Assembly, string> owners = [];

    internal static string Owner(Assembly assembly)
    {
        if (owners.TryGetValue(assembly, out string? owner))
        {
            return owner;
        }

        if (assembly == typeof(Game).Assembly)
        {
            owner = "RimWorld";
        }
        else
        {
            ModContentPack mod = LoadedModManager.RunningModsListForReading
                .FirstOrDefault(pack => pack.assemblies.loadedAssemblies.Contains(assembly));
            owner = mod != null ? mod.Name : "程序集 " + assembly.GetName().Name;
        }

        owners[assembly] = owner;
        return owner;
    }

    internal static void DrainStray(List<string> sink)
    {
        sink.AddRange(pendingStray);
        pendingStray.Clear();
    }

    internal static void DrainAmbient(List<string> sink, List<string> freshSink)
    {
        foreach (KeyValuePair<string, string> pending in pendingAmbient)
        {
            sink.Add(pending.Value);
            if (RimRunnerSeenLogLedger.IsFreshAmbient(pending.Key))
            {
                freshSink.Add(pending.Value);
            }
        }

        pendingAmbient.Clear();
    }

    internal static void ResetForRun()
    {
        ambientTypesThisRun.Clear();
        pendingAmbient.Clear();
    }

    internal static IDisposable SimulateLoadWindow(string label)
    {
        if (simulatedLoadWindow != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 载档窗模拟已开启，不得嵌套");
        }

        simulatedLoadWindow = label;
        return new SimulationScope();
    }

    private sealed class SimulationScope : IDisposable
    {
        public void Dispose()
        {
            simulatedLoadWindow = null;
        }
    }

    internal static string? DescribeOrigin(Window window)
    {
        return window != null && origins.TryGetValue(window, out Push? push) ? DescribeOrigin(push) : null;
    }

    private static string DescribeOrigin(Push push)
    {
        if (push.Tick < 0)
        {
            return push.Origin + "进栈";
        }

        return push.FromCase
            ? "tick " + push.Tick + " 于 " + push.Origin + " 期间进栈"
            : "tick " + push.Tick + " " + push.Origin + "进栈";
    }

    private const string BlockingConsequence =
        "带 absorbInput 的窗口在栈上时 Mouse.IsOver 对任何区域为假，带 forcePause 的窗口使时间暂停";

    private static string State(Push push)
    {
        if (!push.Entered)
        {
            return push.Window is Dialog_GiveName ? "自动命名已提交，未进栈" : "Add 返回时已不在栈上";
        }

        return IsOpen(push.Window) ? "仍打开" : "已关闭";
    }

    private static string Label(Window window, string typeName)
    {
        List<string> flags = new(2);
        if (window.forcePause)
        {
            flags.Add("forcePause");
        }

        if (window.absorbInputAroundWindow)
        {
            flags.Add("absorbInput");
        }

        return flags.Count == 0 ? typeName : typeName + "［" + string.Join("·", flags) + "］";
    }

    private static bool IsBlocking(Window window) => window.forcePause || window.absorbInputAroundWindow;

    internal static void DisableLogWindowAutoOpen()
    {
        FieldInfo canAutoOpen = AccessTools.Field(typeof(EditWindow_Log), "canAutoOpen");
        if (canAutoOpen == null)
        {
            RimRunnerEngineFault.Raise("关闭日志窗自动弹出失败：EditWindow_Log.canAutoOpen 不存在");
            return;
        }

        canAutoOpen.SetValue(null, false);
        EditWindow_Log.wantsToOpen = false;
    }

    private static bool IsOpen(Window window) => Find.WindowStack != null && Find.WindowStack.IsOpen(window);

    private static List<Window> OpenWindows()
    {
        WindowStack stack = Find.WindowStack;
        return stack == null
            ? []
            : stack.Windows.Where(window => window is not null and not ImmediateWindow).ToList();
    }
}
