using System.Diagnostics;

namespace RimRunner;

internal static class RimRunnerFrozenMapSpawnProbe
{
    private const int CaptureLimitPerCase = 200;
    private const int FramesShown = 6;

    private static readonly string[] Plumbing =
    [
        ".SpawnSetup",
        "GenSpawn.",
        "GenPlace.",
        "GenDrop."
    ];

    private static string? armedId;
    private static Type? armedType;
    private static int captures;
    private static readonly RimRunnerProbeHits hits = new();

    internal static void ResetRun()
    {
        armedId = null;
        hits.Clear();
    }

    internal static void Arm(RimRunnerTestCase testCase)
    {
        armedId = testCase.Id;
        armedType = testCase.GetType();
        captures = 0;
    }

    internal static void Disarm()
    {
        armedId = null;
        armedType = null;
    }

    internal static bool Armed => armedId != null;

    internal static List<(string Text, string? Origin)> Drain(string caseId) => hits.DrainWithOrigin(caseId);

    internal static int Consume(string caseId, string defName) =>
        hits.Consume(caseId, text => RimRunnerOrigin.Body(text).StartsWith(defName + " ", StringComparison.Ordinal)).Count;

    internal static void Note(Thing thing, Map map)
    {
        if (armedId == null || !RimRunnerIsolatedMap.IsFrozenMap(map))
        {
            return;
        }

        if (captures >= CaptureLimitPerCase)
        {
            if (captures++ == CaptureLimitPerCase)
            {
                hits.Add(armedId + "|cap", armedId, "落物记录达上限 " + CaptureLimitPerCase + " 条，本条其后落到冻结图上的不再记录");
            }

            return;
        }

        captures++;
        List<string> frames = [];
        MethodBase? initiator = null;
        foreach (StackFrame frame in new StackTrace(2, false).GetFrames() ?? [])
        {
            MethodBase? method = RimRunnerCallSite.MethodOf(frame);
            if (method?.DeclaringType == null)
            {
                continue;
            }

            string described = method.DeclaringType.Name + "." + method.Name;
            if (frames.Count == 0 && Plumbing.Any(described.Contains))
            {
                continue;
            }

            initiator ??= method;
            frames.Add(described);
            if (frames.Count == FramesShown)
            {
                break;
            }
        }

        string defName = thing.def?.defName ?? "(null)";
        string key = armedId + "|" + defName + "|" + (frames.Count > 0 ? frames[0] : "");
        if (hits.Bump(key))
        {
            return;
        }

        string where = map == RimRunnerIsolatedMap.Frozen
            ? "冻结的主图"
            : "停放的独立小图 " + map + "（" + (RimRunnerIsolatedMap.ParkedOwner(map) ?? "归属未知") + "）";
        string? origin = initiator == null ? null : RimRunnerOrigin.Of(initiator, armedType);
        hits.Add(key, armedId, (origin == null ? "" : RimRunnerOrigin.Lead(origin)) + defName + " 落到" + where
                               + (frames.Count > 0 ? "：" + string.Join(" ← ", frames) : ""), origin: origin);
    }
}

[HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
internal static class Patch_Thing_SpawnSetup_RimRunnerFrozenMapSpawnProbe
{
    private static void Prefix(Thing __instance, Map map, bool respawningAfterLoad)
    {
        if (respawningAfterLoad)
        {
            return;
        }

        try
        {
            RimRunnerFrozenMapSpawnProbe.Note(__instance, map);
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("冻结图落物探针自身抛出（" + (__instance.def?.defName ?? "(null)") + "）", exception);
        }
    }
}
