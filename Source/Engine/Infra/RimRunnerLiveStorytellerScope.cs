using System.Diagnostics;
using RimWorld.QuestGen;

namespace RimRunner;

internal static class RimRunnerLiveStorytellerScope
{
    private const int FramesShown = 4;

    private static int windowDepth;
    private static readonly RimRunnerProbeHits hits = new();

    internal static bool Excludes(Map map) => DebugSettings.enableStoryteller && RimRunnerIsolatedMap.IsForeignMap(map);

    internal static bool ExcludesFromHomePick(Map map) =>
        (DebugSettings.enableStoryteller || RimRunnerFrozenMapSpawnProbe.Armed) && RimRunnerIsolatedMap.IsForeignMap(map);

    internal static bool InPickWindow => windowDepth > 0;

    internal static void EnterPickWindow() => windowDepth++;

    internal static void ExitPickWindow()
    {
        if (windowDepth > 0)
        {
            windowDepth--;
        }
    }

    internal static void ResetRun()
    {
        hits.Clear();
    }

    internal static List<(string Text, string? Origin)> Drain(string caseId) => hits.DrainWithOrigin(caseId);

    internal static List<string> DrainRest() => hits.DrainAll();

    internal static int Consume(string caseId, string defName) =>
        hits.Consume(caseId, text => RimRunnerOrigin.Body(text).StartsWith(defName + " ", StringComparison.Ordinal)).Count;

    internal static void NoteBlocked(IncidentDef? def, StackTrace trace)
    {
        string? caseId = RimRunnerObservationBuffer.ActiveCaseId;
        List<string> callers = [];
        MethodBase? initiator = null;
        foreach (StackFrame frame in trace.GetFrames() ?? [])
        {
            MethodBase? method = RimRunnerCallSite.MethodOf(frame);
            if (method?.DeclaringType == null || method.Name.Contains("TryExecute"))
            {
                continue;
            }

            initiator ??= method;
            callers.Add(method.DeclaringType.Name + "." + method.Name);
            if (callers.Count == FramesShown)
            {
                break;
            }
        }

        string defName = def?.defName ?? "(null)";
        string key = caseId + "|" + defName + "|" + (callers.Count > 0 ? callers[0] : "");
        if (hits.Bump(key))
        {
            return;
        }

        Type? caseType = caseId == null
            ? null
            : RimRunnerRegistry.All.FirstOrDefault(item => string.Equals(item.Id, caseId, StringComparison.Ordinal))?.GetType();
        string? origin = initiator == null ? null : RimRunnerOrigin.Of(initiator, caseType);
        hits.Add(key, caseId, (origin == null ? "" : RimRunnerOrigin.Lead(origin)) + defName + " 目标图非本用例的图，未执行"
                              + (callers.Count > 0 ? "：" + string.Join(" ← ", callers) : ""), origin: origin);
    }
}

[HarmonyPatch(typeof(Storyteller), nameof(Storyteller.AllIncidentTargets), MethodType.Getter)]
internal static class Patch_Storyteller_AllIncidentTargets_RimRunnerLiveScope
{
    private static void Postfix(List<IIncidentTarget> __result)
    {
        if (__result == null || !DebugSettings.enableStoryteller || RimRunnerIsolatedMap.Frozen == null)
        {
            return;
        }

        __result.RemoveAll(target => target is Map map && RimRunnerLiveStorytellerScope.Excludes(map));
    }
}

[HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
internal static class Patch_IncidentWorker_TryExecute_RimRunnerLiveScope
{
    private static bool Prefix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
    {
        if (parms?.target is not Map map || !RimRunnerLiveStorytellerScope.Excludes(map))
        {
            return true;
        }

        RimRunnerLiveStorytellerScope.NoteBlocked(__instance.def, new StackTrace(1, false));
        __result = false;
        return false;
    }
}

[HarmonyPatch]
internal static class Patch_PlayerHomePickers_RimRunnerLiveScope
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        (Type type, string name, bool getter)[] wanted =
        [
            (typeof(Game), nameof(Game.AnyPlayerHomeMap), true),
            (typeof(Game), nameof(Game.RandomPlayerHomeMap), true),
            (typeof(Game), nameof(Game.PlayerHomeMaps), true),
            (typeof(Game), nameof(Game.RandomSurfacePlayerHomeMap), true),
            (typeof(Game), nameof(Game.RandomRootSurfacePlayerHomeMap), true),
            (typeof(QuestGen_Get), nameof(QuestGen_Get.GetMap), false),
            (typeof(QuestNode_GetMap), "TryFindMap", false),
            (typeof(QuestNode_GetNearestHomeMapOf), "DoWork", false),
            (typeof(QuestNode_Root_WorkSite), "RunInt", false),
            (typeof(QuestNode_Root_WorkSite), "TestRunInt", false),
            (typeof(QuestNode_Root_WorkSite), nameof(QuestNode_Root_WorkSite.BestAppearanceFrequency), false),
            (typeof(QuestNode_Root_ReliquaryPilgrims), "GetMap", false),
            (AccessTools.TypeByName("RimWorld.ScenPart_CreateIncident"), "Tick", false)
        ];

        foreach ((Type type, string name, bool getter) in wanted)
        {
            MethodBase? method = type == null ? null
                : getter ? AccessTools.PropertyGetter(type, name) : AccessTools.DeclaredMethod(type, name);
            if (method == null)
            {
                RimRunnerEngineFault.Raise("叙事者照发的挑图入口缺失 " + (type?.Name ?? "(类型缺失)") + "." + name
                          + "：该入口挑「玩家家园」时会挑中冻结的主图");
                continue;
            }

            yield return method;
        }
    }

    private static void Prefix() => RimRunnerLiveStorytellerScope.EnterPickWindow();

    private static void Finalizer() => RimRunnerLiveStorytellerScope.ExitPickWindow();
}

[HarmonyPatch(typeof(Map), nameof(Map.IsPlayerHome), MethodType.Getter)]
internal static class Patch_Map_IsPlayerHome_RimRunnerLiveScope
{
    private static void Postfix(Map __instance, ref bool __result)
    {
        if (__result && RimRunnerLiveStorytellerScope.InPickWindow && RimRunnerLiveStorytellerScope.ExcludesFromHomePick(__instance))
        {
            __result = false;
        }
    }
}
