namespace RimRunner;

[HarmonyPatch(typeof(Building_Casket), nameof(Building_Casket.TryAcceptThing), typeof(Thing), typeof(bool))]
internal static class Patch_Building_Casket_TryAcceptThing_RimRunnerSentinel
{
    private static void Postfix(Building_Casket __instance, Thing thing, bool __result)
    {
        string? caseId = RimRunnerObservationBuffer.ActiveCaseId;
        if (caseId == null || !__result || thing == null || !thing.Spawned)
        {
            return;
        }

        RimRunnerSentinel.Warn(RimRunnerWarningKey.CasketAccept, caseId + "：TryAcceptThing 返回 true 但物仍在地图上——对 spawned 物此调用恒为假成功",
            __instance.LabelShort + ".TryAcceptThing(" + thing.LabelShort + ")");
    }
}
