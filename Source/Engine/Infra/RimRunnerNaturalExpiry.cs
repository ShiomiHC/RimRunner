using System.Runtime.CompilerServices;

namespace RimRunner;

internal static class RimRunnerNaturalExpiry
{
    private sealed class Record
    {
        internal bool Gone;
        internal int Dissolved;
    }

    private static readonly ConditionalWeakTable<Thing, Record> records = [];

    internal static void NoteGone(Thing thing)
    {
        records.GetOrCreateValue(thing).Gone = true;
    }

    internal static void NoteDissolved(Thing thing, int amount)
    {
        records.GetOrCreateValue(thing).Dissolved += amount;
    }

    internal static bool Gone(IEnumerable<Thing> instances)
    {
        return instances.Any(thing => records.TryGetValue(thing, out Record? record) && record.Gone);
    }

    internal static int Dissolved(IEnumerable<Thing> instances)
    {
        return instances.Sum(thing => records.TryGetValue(thing, out Record? record) ? record.Dissolved : 0);
    }
}

[HarmonyPatch(typeof(CompRottable), "TickInterval")]
internal static class Patch_CompRottable_TickInterval_RimRunnerNaturalExpiry
{
    private static void Postfix(CompRottable __instance)
    {
        if (__instance.parent.Destroyed)
        {
            RimRunnerNaturalExpiry.NoteGone(__instance.parent);
        }
    }
}

[HarmonyPatch(typeof(CompLifespan), "Expire")]
internal static class Patch_CompLifespan_Expire_RimRunnerNaturalExpiry
{
    private static void Postfix(CompLifespan __instance)
    {
        if (__instance.parent.Destroyed)
        {
            RimRunnerNaturalExpiry.NoteGone(__instance.parent);
        }
    }
}

[HarmonyPatch(typeof(CompDissolution), nameof(CompDissolution.TriggerDissolutionEvent))]
internal static class Patch_CompDissolution_TriggerDissolutionEvent_RimRunnerNaturalExpiry
{
    private static void Prefix(CompDissolution __instance, out int __state)
    {
        __state = __instance.parent.stackCount;
    }

    private static void Postfix(CompDissolution __instance, int __state)
    {
        Thing parent = __instance.parent;
        RimRunnerNaturalExpiry.NoteDissolved(parent, __state - Math.Max(parent.stackCount, 0));
        if (parent.Destroyed)
        {
            RimRunnerNaturalExpiry.NoteGone(parent);
        }
    }
}

[HarmonyPatch(typeof(SteadyEnvironmentEffects), nameof(SteadyEnvironmentEffects.DoDeteriorationDamage))]
internal static class Patch_SteadyEnvironmentEffects_DoDeteriorationDamage_RimRunnerNaturalExpiry
{
    private static void Postfix(Thing t)
    {
        if (t.Destroyed)
        {
            RimRunnerNaturalExpiry.NoteGone(t);
        }
    }
}
