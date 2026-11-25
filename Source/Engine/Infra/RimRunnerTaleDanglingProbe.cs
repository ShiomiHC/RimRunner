using Verse.AI.Group;

namespace RimRunner;

internal static class RimRunnerTaleDanglingProbe
{
    internal const string AddOccasion = "入表即悬空";

    internal const string DanglingReason = "tale 仍持有已丢弃的 pawn，此后每次存档都写出 null，读档即丢";

    private static readonly Dictionary<Type, FieldInfo[]> CarrierFieldCache = [];

    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    internal static void Reset()
    {
        Reported.Clear();
    }

    internal static void ScanAndReport(string occasion)
    {
        if (Current.Game == null || Find.TaleManager == null)
        {
            return;
        }

        foreach (Tale tale in Find.TaleManager.AllTalesListForReading)
        {
            Inspect(occasion, tale);
        }
    }

    internal static void Inspect(string occasion, Tale tale)
    {
        if (tale == null)
        {
            return;
        }

        foreach (FieldInfo field in CarrierFields(tale.GetType()))
        {
            if (field.GetValue(tale) is not TaleData_Pawn data)
            {
                continue;
            }

            Pawn pawn = data.pawn;
            if (pawn == null || !pawn.Discarded)
            {
                continue;
            }

            string key = tale.id + "/" + field.Name + "/" + pawn.ThingID;
            if (!Reported.Add(key))
            {
                continue;
            }

            string origin = occasion == AddOccasion
                ? " stack=" + RimRunnerObservationBuffer.DiagStack()
                : string.Empty;
            RimRunnerSentinel.Raise(occasion + "：" + DanglingReason,
                "tale=" + tale.def?.defName + "(" + tale.GetType().Name + ")"
                + " 槽=" + field.Name + " uses=" + tale.Uses
                + " pawn=" + pawn + " id=" + pawn.ThingID + origin);
        }
    }

    internal static bool Carries(Tale tale, Pawn pawn)
    {
        foreach (FieldInfo field in CarrierFields(tale.GetType()))
        {
            if (field.GetValue(tale) is TaleData_Pawn data && data.pawn == pawn)
            {
                return true;
            }
        }

        return false;
    }

    private static FieldInfo[] CarrierFields(Type taleType)
    {
        if (CarrierFieldCache.TryGetValue(taleType, out FieldInfo[]? cached))
        {
            return cached;
        }

        FieldInfo[] fields = RimRunnerAbsentReferences
            .ReadableFields(taleType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => typeof(TaleData_Pawn).IsAssignableFrom(field.FieldType))
            .ToArray();
        CarrierFieldCache[taleType] = fields;
        return fields;
    }
}

[HarmonyPatch(typeof(MapDeiniter), nameof(MapDeiniter.Deinit))]
internal static class Patch_MapDeiniter_Deinit_RimRunnerTaleProbe
{
    private static void Postfix()
    {
        RimRunnerTaleDanglingProbe.ScanAndReport("拆图之后");
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.Destroy))]
internal static class Patch_Pawn_Destroy_RimRunnerLordDetach
{
    private static void Prefix(Pawn __instance)
    {
        if (Current.ProgramState != ProgramState.Playing
            || RimRunnerObservationBuffer.ActiveCaseId == null
            || !RimRunnerPawns.IsTestPawn(__instance))
        {
            return;
        }

        __instance.GetLord()?.RemovePawn(__instance);
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.Discard))]
internal static class Patch_Pawn_Discard_RimRunnerTaleProbe
{
    private static void Postfix()
    {
        if (Current.ProgramState != ProgramState.Playing)
        {
            RimRunnerTaleDanglingProbe.ScanAndReport("非 Playing 期丢弃之后");
        }
    }
}

[HarmonyPatch(typeof(TaleManager), nameof(TaleManager.Add))]
internal static class Patch_TaleManager_Add_RimRunnerTaleProbe
{
    private static void Postfix(Tale tale)
    {
        RimRunnerTaleDanglingProbe.Inspect(RimRunnerTaleDanglingProbe.AddOccasion, tale);
    }
}
