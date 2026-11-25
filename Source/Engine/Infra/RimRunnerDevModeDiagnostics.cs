using RimWorld.Planet;
using Verse.Sound;
using System.Reflection.Emit;

namespace RimRunner;

internal static class RimRunnerDevModeDiagnostics
{
    internal static readonly MethodInfo DevModeGetter = AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.DevMode));

    internal static readonly MethodInfo Replacement =
        AccessTools.Method(typeof(RimRunnerDevModeDiagnostics), nameof(DiagnosticsOn));

    private static bool DiagnosticsOn() => true;

    internal static IEnumerable<CodeInstruction> RewriteSingleRead(IEnumerable<CodeInstruction> instructions, string site)
    {
        List<CodeInstruction> codes = instructions.ToList();
        List<int> hits = Enumerable.Range(0, codes.Count).Where(index => codes[index].Calls(DevModeGetter)).ToList();
        if (hits.Count != 1)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " [engine] " + site + " 里 Prefs.DevMode 的读点有 "
                                                + hits.Count + " 处（预期 1），devMode 诊断补丁无法挂上");
        }

        codes[hits[0]].opcode = OpCodes.Call;
        codes[hits[0]].operand = Replacement;
        return codes;
    }
}

[HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GetValueUnfinalized))]
internal static class Patch_StatWorker_GetValueUnfinalized_RimRunnerDevModeDiagnostics
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        RimRunnerDevModeDiagnostics.RewriteSingleRead(instructions, "StatWorker.GetValueUnfinalized");
}

[HarmonyPatch(typeof(ThingWithComps), "InspectStringPartsFromComps")]
internal static class Patch_ThingWithComps_InspectStringPartsFromComps_RimRunnerDevModeDiagnostics
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        RimRunnerDevModeDiagnostics.RewriteSingleRead(instructions, "ThingWithComps.InspectStringPartsFromComps");
}

[HarmonyPatch(typeof(WorldObject), nameof(WorldObject.GetInspectString))]
internal static class Patch_WorldObject_GetInspectString_RimRunnerDevModeDiagnostics
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        RimRunnerDevModeDiagnostics.RewriteSingleRead(instructions, "WorldObject.GetInspectString");
}

[HarmonyPatch(typeof(Sustainer), nameof(Sustainer.CameraDistanceSquared), MethodType.Getter)]
internal static class Patch_Sustainer_CameraDistanceSquared_RimRunnerDevModeDiagnostics
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        RimRunnerDevModeDiagnostics.RewriteSingleRead(instructions, "Sustainer.CameraDistanceSquared");
}

[HarmonyPatch]
internal static class Patch_BackstoryDef_ConfigErrors_RimRunnerDevModeDiagnostics
{
    private static MethodBase TargetMethod() =>
        AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(BackstoryDef), nameof(BackstoryDef.ConfigErrors)));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        RimRunnerDevModeDiagnostics.RewriteSingleRead(instructions, "BackstoryDef.ConfigErrors");
}

[HarmonyPatch]
internal static class Patch_BiomeDef_ConfigErrors_RimRunnerDevModeDiagnostics
{
    private static MethodBase TargetMethod() =>
        AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(BiomeDef), nameof(BiomeDef.ConfigErrors)));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        RimRunnerDevModeDiagnostics.RewriteSingleRead(instructions, "BiomeDef.ConfigErrors");
}
