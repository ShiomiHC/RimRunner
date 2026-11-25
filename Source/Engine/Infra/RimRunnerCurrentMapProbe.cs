using System.Diagnostics;
using RimWorld.Planet;

namespace RimRunner;

internal static class RimRunnerCurrentMapProbe
{
    internal const int CaptureLimitPerCase = 500;
    private const int FramesShown = 6;

    private static readonly (Type Type, string? Method)[] BenignGates =
    [
        (typeof(Verb), nameof(Verb.DrawHighlight)),
        (typeof(VerbProperties), nameof(VerbProperties.DrawRadiusRing)),
        (typeof(GenDraw), null),
        (typeof(GenView), nameof(GenView.ShouldSpawnMotesAt)),
        (typeof(SoundDefHelper), nameof(SoundDefHelper.CorrectContextNow)),
        (typeof(CompUseEffect), nameof(CompUseEffect.DoEffect)),
        (typeof(GenTicks), nameof(GenTicks.GetCameraUpdateRate)),
        (typeof(CellRect), nameof(CellRect.ViewRect)),
        (typeof(Building), "DoDestroyEffects"),
        (typeof(DamageWorker), "ExplosionStart"),
        (typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty)),
        (typeof(Game), nameof(Game.DeinitAndRemoveMap)),
        (typeof(Game), "set_CurrentMap")
    ];

    private static HashSet<MethodBase>? gateMethods;

    [ThreadStatic] private static int gateDepth;

    private static string? armedId;
    private static Type? armedType;
    private static HashSet<string>? armedOrigins;
    private static int armedThread;
    private static readonly HashSet<MethodBase> seenReaders = [];
    private static int captureLimit = CaptureLimitPerCase;
    private static int depth;
    private static bool readingView;
    private static readonly RimRunnerProbeHits hits = new();
    private static readonly RimRunnerProbeHits blind = new();

    internal static void ResetRun()
    {
        armedId = null;
        armedType = null;
        seenReaders.Clear();
        hits.Clear();
        blind.Clear();
        envChains.Clear();
        envOrder.Clear();
    }

    internal static void Arm(RimRunnerTestCase testCase)
    {
        armedId = testCase.Id;
        armedType = testCase.GetType();
        armedOrigins = RimRunnerWarnings.AckedOrigins(armedType, RimRunnerWarningKey.CurrentMapRead);
        armedThread = Environment.CurrentManagedThreadId;
        seenReaders.Clear();
        depth = 0;
    }

    internal static int SwapCaptureLimitForSelfTest(int limit)
    {
        int previous = captureLimit;
        captureLimit = limit;
        return previous;
    }

    internal static void ClearSeenReadersForSelfTest() => seenReaders.Clear();

    internal static void Disarm()
    {
        armedId = null;
        armedType = null;
        armedOrigins = null;
    }

    internal static List<(string Text, string? Origin)> Drain(string caseId) => hits.DrainWithOrigin(caseId);

    internal static List<string> DrainBlind(string caseId) => blind.Drain(caseId);

    internal static List<string> Consume(string caseId, string readerContains) =>
        hits.Consume(caseId, text => text.Contains(readerContains));

    internal static List<(string Text, string? Origin, int Count, bool Rerouted)> ConsumeReads(string caseId, string readerContains) =>
        [.. hits.ConsumeHits(caseId, text => text.Contains(readerContains)).Select(hit =>
            (hit.Text, hit.Origin, hit.Count, hit.Key.StartsWith(caseId + ReroutedInfix, StringComparison.Ordinal)))];

    internal static HashSet<string>? SwapAckedOrigins(HashSet<string>? origins)
    {
        HashSet<string>? previous = armedOrigins;
        armedOrigins = origins;
        return previous;
    }

    internal static List<(string Text, int Count)> ConsumeBlind(string caseId, BlindReason reason) =>
        blind.ConsumeKey(caseId + (reason == BlindReason.Generating ? GeneratingSuffix : CaptureLimitSuffix));

    internal static void Enter(bool view)
    {
        if (armedId != null)
        {
            if (depth++ == 0)
            {
                readingView = view;
            }
        }
    }

    internal static void Exit()
    {
        if (armedId == null || depth == 0)
        {
            return;
        }

        depth--;
        if (depth > 0 || gateDepth > 0 || (RimRunnerTimeDriver.Advancing && !RimRunnerTimeDriver.InBurstCallback) || RimRunnerCurrentMap.Borrowing || RimRunnerIsolatedMap.Capturing)
        {
            return;
        }

        if (RimRunnerSession.BodySuspendedOutside)
        {
            return;
        }

        if (RimRunnerIsolatedMap.Generating)
        {
            NoteBlind(BlindReason.Generating, "用例体内生成独立小图期间读当前地图，未观测（读点落在生图；地图组件初始化读错图在这一段看不见）");
            return;
        }

        if (rerouteSite != 0 && NoteRerouted(rerouteSite))
        {
            return;
        }

        if (seenReaders.Count >= captureLimit)
        {
            NoteBlind(BlindReason.CaptureLimit, "截栈达上限（" + captureLimit + " 个不同读者帧），其后的读当前地图未观测");
            return;
        }

        Record(new StackTrace(2, false));
    }

    private static void NoteBlind(BlindReason reason, string text)
    {
        string key = armedId + (reason == BlindReason.Generating ? GeneratingSuffix : CaptureLimitSuffix);
        if (!blind.Bump(key))
        {
            blind.Add(key, armedId, text);
        }
    }

    private static bool Record(StackTrace trace)
    {
        List<MethodBase> frames = [];
        List<bool> gapBefore = [];
        bool skipped = false;
        foreach (StackFrame frame in trace.GetFrames() ?? [])
        {
            MethodBase? method = RimRunnerCallSite.MethodOf(frame);
            if (method?.DeclaringType == null || IsGetter(method))
            {
                skipped = frames.Count > 0;
                continue;
            }

            frames.Add(method);
            gapBefore.Add(skipped);
            skipped = false;
        }

        if (frames.Count == 0 || RimRunnerDefScope.IsEngineSide(frames[0].DeclaringType!.Assembly)
            && !IsCaseSide(frames[0].DeclaringType!))
        {
            return false;
        }

        int entry = frames.FindIndex(method => IsCaseSide(method.DeclaringType!));
        if (entry < 0)
        {
            return false;
        }

        seenReaders.Add(frames[0]);

        string origin = RimRunnerOrigin.Of(frames[0], armedType);
        if (armedOrigins == null || !armedOrigins.Contains(origin))
        {
            int envEntry = EnvChainEntry(frames, gapBefore, origin);
            if (envEntry >= 0)
            {
                NoteEnvChain(frames, envEntry, origin);
                return true;
            }
        }

        string reader = Describe(frames[0]);
        string caseEntry = Describe(frames[entry]);
        string key = armedId + "|" + (readingView ? ViewGetter + "|" : "") + origin + "|" + reader + "|" + caseEntry;
        if (hits.Bump(key))
        {
            if (armedOrigins is { Count: > 0 } && !RimRunnerCurrentMapReroute.Tried(frames[0]))
            {
                RimRunnerCurrentMapReroute.Consider(frames[0], origin, armedOrigins, armedThread);
            }

            return true;
        }

        IEnumerable<string> chain = frames.Take(Math.Min(entry + 1, FramesShown)).Select(Describe);
        hits.Add(key, armedId, RimRunnerOrigin.Lead(origin) + (readingView ? ViewGetter + " ← " : "") + string.Join(" ← ", chain)
                               + (entry + 1 > FramesShown ? " ← … ← " + caseEntry : ""),
            readingView ? ViewTail : "", origin);
        if (armedOrigins is { Count: > 0 })
        {
            RimRunnerCurrentMapReroute.Consider(frames[0], origin, armedOrigins, armedThread);
        }

        return true;
    }

    private enum FrameSide
    {
        Tested,
        ThirdParty,
        Vanilla,
        Other
    }

    private static FrameSide SideOf(MethodBase method)
    {
        Type type = method.DeclaringType!;
        if (IsCaseSide(type) || RimRunnerDefScope.IsEngineSide(type.Assembly) || RimRunnerDefScope.IsOursType(type))
        {
            return FrameSide.Tested;
        }

        if (type.Assembly == typeof(Game).Assembly)
        {
            return FrameSide.Vanilla;
        }

        return RimRunnerOrigin.PackOf(type.Assembly) != null ? FrameSide.ThirdParty : FrameSide.Other;
    }

    private static int EnvChainEntry(List<MethodBase> frames, List<bool> gapBefore, string origin)
    {
        if (!origin.StartsWith(RimRunnerOrigin.ModPrefix, StringComparison.OrdinalIgnoreCase)
            || SideOf(frames[0]) != FrameSide.ThirdParty)
        {
            return -1;
        }

        int below = 1;
        while (below < frames.Count && !gapBefore[below] && SideOf(frames[below]) == FrameSide.ThirdParty)
        {
            below++;
        }

        return below < frames.Count && !gapBefore[below] && SideOf(frames[below]) == FrameSide.Vanilla ? below - 1 : -1;
    }

    private sealed class EnvChain
    {
        internal required string Origin;
        internal required string Anchor;
        internal required string Signature;
        internal required string Chain;
        internal required string Carrier;
        internal required bool View;
        internal readonly List<string> Cases = [];
        internal int Count;
    }

    private static readonly Dictionary<string, EnvChain> envChains = new(StringComparer.Ordinal);
    private static readonly List<EnvChain> envOrder = [];

    private static void NoteEnvChain(List<MethodBase> frames, int entryIndex, string origin)
    {
        string anchor = Describe(frames[entryIndex]);
        string carrier = Describe(frames[entryIndex + 1]);
        string signature = origin + "|" + (readingView ? "view" : "map") + "|" + Describe(frames[0]) + "|" + anchor + "|" + carrier;
        if (!envChains.TryGetValue(signature, out EnvChain? chain))
        {
            List<string> shown = frames.Take(entryIndex + 2).Select(Describe).ToList();
            if (shown.Count > FramesShown)
            {
                shown = [.. shown.Take(FramesShown - 3), "…", anchor, carrier];
            }

            chain = new EnvChain
            {
                Origin = origin, Anchor = anchor, Signature = signature, Chain = string.Join(" ← ", shown),
                Carrier = carrier, View = readingView
            };
            envChains[signature] = chain;
            envOrder.Add(chain);
        }

        chain.Count++;
        if (armedId != null && (chain.Cases.Count == 0 || chain.Cases[^1] != armedId) && !chain.Cases.Contains(armedId))
        {
            chain.Cases.Add(armedId);
        }
    }

    private const int EnvExamples = 3;

    private static string Describe(EnvChain chain) =>
        RimRunnerOrigin.Lead(chain.Origin) + "环境链 " + (chain.View ? ViewGetter + " ← " : "") + chain.Chain
        + "：" + chain.Cases.Count + " 条用例、观测 " + chain.Count + " 次；举例 " + string.Join("、", chain.Cases.Take(EnvExamples))
        + "；读点在他 mod，链锚 " + chain.Anchor + " 由原版代码调入";

    internal static List<(string Text, string Origin, string Anchor, string Scope)> DrainEnvChains()
    {
        List<(string, string, string, string)> drained = [.. envOrder.Select(chain =>
            (Describe(chain), chain.Origin, chain.Anchor, RimRunnerSeenLogLedger.EnvChainScope(chain.Signature)))];
        envChains.Clear();
        envOrder.Clear();
        return drained;
    }

    internal sealed class EnvChainView
    {
        internal required string Text;
        internal required string Origin;
        internal required string Anchor;
        internal required string Chain;
        internal required string Carrier;
        internal required List<string> Cases;
        internal required int Count;
    }

    internal static List<EnvChainView> ConsumeEnvChains(string anchorContains)
    {
        List<EnvChain> taken = envOrder.Where(chain => chain.Anchor.Contains(anchorContains)).ToList();
        foreach (EnvChain chain in taken)
        {
            envChains.Remove(chain.Signature);
            envOrder.Remove(chain);
        }

        return [.. taken.Select(chain => new EnvChainView
        {
            Text = Describe(chain), Origin = chain.Origin, Anchor = chain.Anchor, Chain = chain.Chain,
            Carrier = chain.Carrier, Cases = chain.Cases.ToList(), Count = chain.Count
        })];
    }

    [ThreadStatic] internal static int rerouteSite;

    private static bool NoteRerouted(int site)
    {
        (string origin, string reader) = RimRunnerCurrentMapReroute.Site(site);
        if (armedOrigins == null || !armedOrigins.Contains(origin))
        {
            return false;
        }

        string key = armedId + ReroutedInfix + (readingView ? ViewGetter + "|" : "") + site;
        if (!hits.Bump(key))
        {
            hits.Add(key, armedId, RimRunnerOrigin.Lead(origin) + (readingView ? ViewGetter + " ← " : "") + reader
                                   + "（已改道，只计次）",
                readingView ? ViewTail : "", origin);
        }

        return true;
    }

    internal enum BlindReason
    {
        Generating,
        CaptureLimit
    }

    private const string ReroutedInfix = "|rr|";
    private const string GeneratingSuffix = "|gen";
    private const string CaptureLimitSuffix = "|cap";

    internal const string ViewGetter = "CameraDriver.CurrentViewRect";

    internal const string ViewTail = "；视野取自主图";

    internal static readonly IReadOnlyCollection<MethodBase> Getters =
    [
        AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap)),
        AccessTools.PropertyGetter(typeof(Game), nameof(Game.CurrentMap)),
        AccessTools.PropertyGetter(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect))
    ];

    private static readonly string[] GetterShortNames = RimRunnerCurrentMapGetterNames.All
        .Select(name => { string[] parts = name.Split('.'); return parts[^2] + "." + parts[^1]; })
        .ToArray();

    private static bool IsGetter(MethodBase method)
    {
        if (RimRunnerCurrentMapReroute.IsShim(method))
        {
            return true;
        }

        if (method.DeclaringType is Type declaring
            && RimRunnerCurrentMapGetterNames.All.Contains(declaring.FullName + "." + method.Name))
        {
            return true;
        }

        for (int i = 0; i < GetterShortNames.Length; i++)
        {
            if (method.Name.Contains(GetterShortNames[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCaseSide(Type type) => RimRunnerContractGates.IsCaseSide(type, armedType!);

    internal static IReadOnlyCollection<MethodBase> GateMethods() => gateMethods ??= BenignGates
        .SelectMany(gate => AccessTools.GetDeclaredMethods(gate.Type)
            .Where(method => gate.Method == null || method.Name == gate.Method))
        .Where(method => !method.IsAbstract && !method.ContainsGenericParameters)
        .Cast<MethodBase>()
        .ToHashSet();

    internal static bool IsBenignGate(MethodBase method) => GateMethods().Contains(method);

    internal static void EnterGate() => gateDepth++;

    internal static void ExitGate() => gateDepth--;

    internal static Map? EngineCurrentMap()
    {
        gateDepth++;
        try
        {
            return Find.CurrentMap;
        }
        finally
        {
            gateDepth--;
        }
    }

    internal static CellRect EngineViewRect()
    {
        gateDepth++;
        try
        {
            return Find.CameraDriver.CurrentViewRect;
        }
        finally
        {
            gateDepth--;
        }
    }

    internal static Map? UnwatchedCurrentMap()
    {
        Game? game = Current.Game;
        return game == null || game.currentMapIndex < 0 ? null : game.Maps[game.currentMapIndex];
    }

    private static string Describe(MethodBase method) => method.DeclaringType!.Name + "." + method.Name;
}

[HarmonyPatch(typeof(Find), nameof(Find.CurrentMap), MethodType.Getter)]
internal static class Patch_Find_CurrentMap_RimRunnerCurrentMapProbe
{
    private static void Prefix() => RimRunnerCurrentMapProbe.Enter(view: false);

    private static void Postfix() => RimRunnerCurrentMapProbe.Exit();
}

[HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Getter)]
internal static class Patch_Game_CurrentMap_RimRunnerCurrentMapProbe
{
    private static void Prefix() => RimRunnerCurrentMapProbe.Enter(view: false);

    private static void Postfix() => RimRunnerCurrentMapProbe.Exit();
}

[HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect), MethodType.Getter)]
internal static class Patch_CameraDriver_CurrentViewRect_RimRunnerCurrentMapProbe
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix() => RimRunnerCurrentMapProbe.Enter(view: true);

    private static void Postfix() => RimRunnerCurrentMapProbe.Exit();
}

[HarmonyPatch]
internal static class Patch_BenignGate_RimRunnerCurrentMapProbe
{
    private static IEnumerable<MethodBase> TargetMethods() => RimRunnerCurrentMapProbe.GateMethods();

    [HarmonyPriority(Priority.First)]
    private static void Prefix(out bool __state)
    {
        RimRunnerCurrentMapProbe.EnterGate();
        __state = true;
    }

    private static void Finalizer(bool __state)
    {
        if (__state)
        {
            RimRunnerCurrentMapProbe.ExitGate();
        }
    }
}

[HarmonyPatch(typeof(WorldRendererUtility), nameof(WorldRendererUtility.CurrentWorldRenderMode), MethodType.Getter)]
internal static class Patch_WorldRendererUtility_CurrentWorldRenderMode_RimRunnerCurrentMapProbe
{
    private static readonly MethodInfo CurrentMapGetter = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap));

    private static readonly MethodInfo Replacement =
        AccessTools.Method(typeof(RimRunnerCurrentMapProbe), nameof(RimRunnerCurrentMapProbe.UnwatchedCurrentMap));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions.ToList();
        int replaced = 0;
        foreach (CodeInstruction code in codes.Where(code => code.Calls(CurrentMapGetter)))
        {
            code.operand = Replacement;
            replaced++;
        }

        if (replaced == 0)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " [engine] WorldRendererUtility.CurrentWorldRenderMode 里找不到 Find.CurrentMap 的读点，世界渲染模式绕开探针的补丁无法挂上");
        }

        return codes;
    }
}
