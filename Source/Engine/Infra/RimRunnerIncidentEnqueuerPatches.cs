using System.Runtime.CompilerServices;

namespace RimRunner;

[HarmonyPatch(typeof(IncidentQueue), nameof(IncidentQueue.Add), typeof(QueuedIncident))]
internal static class Patch_IncidentQueue_Add_RimRunnerEnqueuer
{
    private static void Postfix(IncidentQueue __instance)
    {
        RimRunnerStorytellerQuiesce.NoteEnqueued(__instance);
    }
}

[HarmonyPatch(typeof(IncidentQueue), nameof(IncidentQueue.Add), typeof(IncidentDef), typeof(int), typeof(IncidentParms), typeof(int))]
internal static class Patch_IncidentQueue_AddDef_RimRunnerEnqueuer
{
    private static void Postfix(IncidentQueue __instance)
    {
        RimRunnerStorytellerQuiesce.NoteEnqueued(__instance);
    }
}

[HarmonyPatch(typeof(IncidentQueue), nameof(IncidentQueue.IncidentQueueTick))]
internal static class Patch_IncidentQueue_Tick_RimRunnerHold
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "queuedIncidents")]
    private static extern ref List<QueuedIncident> Incidents(IncidentQueue queue);

    private static void Prefix(IncidentQueue __instance, out List<QueuedIncident>? __state)
    {
        __state = null;
        if (__instance.Count == 0 || !RimRunnerStorytellerQuiesce.HoldsFiring(__instance))
        {
            return;
        }

        __state = Incidents(__instance);
        RimRunnerStorytellerQuiesce.NoteHeldDue(__state);
        Incidents(__instance) = [];
    }

    private static void Finalizer(IncidentQueue __instance, List<QueuedIncident>? __state)
    {
        if (__state != null)
        {
            Incidents(__instance) = __state;
        }
    }
}
