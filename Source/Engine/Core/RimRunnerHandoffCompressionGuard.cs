namespace RimRunner;

internal static class RimRunnerHandoffCompressionGuard
{
    private static HashSet<Thing>? held;

    internal static void Arm(IEnumerable<Dictionary<string, string>?> checkpointStates)
    {
        held = [];
        foreach (Dictionary<string, string>? state in checkpointStates)
        {
            if (state == null)
            {
                continue;
            }

            foreach (KeyValuePair<string, string> pair in state)
            {
                if (!pair.Key.StartsWith(RimRunnerContext.RegisteredThingStateKeyPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                Thing? thing = RimRunnerContext.FindSpawnedThingById(pair.Value);
                if (thing != null)
                {
                    held.Add(thing);
                }
            }
        }
    }

    internal static void Disarm()
    {
        held = null;
    }

    internal static bool Holds(Thing thing)
    {
        return held != null && thing != null && held.Contains(thing);
    }
}

[HarmonyPatch(typeof(CompressibilityDeciderUtility), nameof(CompressibilityDeciderUtility.IsSaveCompressible))]
internal static class Patch_CompressibilityDeciderUtility_IsSaveCompressible_RimRunnerHandoff
{
    private static void Postfix(Thing t, ref bool __result)
    {
        if (__result && RimRunnerHandoffCompressionGuard.Holds(t))
        {
            __result = false;
        }
    }
}
