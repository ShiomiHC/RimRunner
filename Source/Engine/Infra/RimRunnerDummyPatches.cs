using Verse.AI;

namespace RimRunner;

[HarmonyPatch(typeof(Pawn), nameof(Pawn.HealthScale), MethodType.Getter)]
internal static class Patch_Pawn_HealthScale_RimRunnerDummy
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(Pawn __instance, ref float __result)
    {
        if (RimRunnerDummies.TryGet(__instance, out RimRunnerDummyComp? comp))
        {
            __result *= comp.HealthScale;
        }
    }
}

[HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GetValue), typeof(StatRequest), typeof(bool))]
internal static class Patch_StatWorker_GetValue_RimRunnerDummy
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(StatRequest req, StatDef ___stat, ref float __result)
    {
        if (RimRunnerDummies.TryGet(req.Thing, out RimRunnerDummyComp? comp)
            && comp.TryGetStatFinalized(___stat, out float value))
        {
            __result = value;
        }
    }
}

internal static class Patch_StatWorker_GetBaseValueFor_RimRunnerDummy
{
    internal static void Postfix(StatRequest request, StatDef ___stat, ref float __result)
    {
        RimRunnerEarlyPatches.BaseValueHits++;
        if (RimRunnerDummies.TryGet(request.Thing, out RimRunnerDummyComp? comp)
            && comp.TryGetStatBase(___stat, out float value))
        {
            __result = value;
        }
    }
}

[HarmonyPatch(typeof(Pawn_Thinker), nameof(Pawn_Thinker.MainThinkTree), MethodType.Getter)]
internal static class Patch_PawnThinker_MainThinkTree_RimRunnerDummy
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(Pawn_Thinker __instance, ref ThinkTreeDef __result)
    {
        if (RimRunnerDummies.TryGet(__instance.pawn, out RimRunnerDummyComp? comp) && comp.ThinkTree != null)
        {
            __result = comp.ThinkTree;
        }
    }
}

[HarmonyPatch(typeof(Pawn_Thinker), nameof(Pawn_Thinker.ConstantThinkTree), MethodType.Getter)]
internal static class Patch_PawnThinker_ConstantThinkTree_RimRunnerDummy
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(Pawn_Thinker __instance, ref ThinkTreeDef __result)
    {
        if (RimRunnerDummies.TryGet(__instance.pawn, out RimRunnerDummyComp? comp) && comp.ConstantThinkTree != null)
        {
            __result = comp.ConstantThinkTree;
        }
    }
}

[HarmonyPatch(typeof(Pawn_NeedsTracker), "ShouldHaveNeed")]
internal static class Patch_PawnNeedsTracker_ShouldHaveNeed_RimRunnerDummy
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Pawn ___pawn, ref bool __result)
    {
        if (__result && RimRunnerDummies.TryGet(___pawn, out _))
        {
            __result = false;
        }
    }
}

[HarmonyPatch(typeof(HediffSet), nameof(HediffSet.GetRandomNotMissingPart))]
internal static class Patch_HediffSet_GetRandomNotMissingPart_RimRunnerDummy
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static readonly HashSet<string> notedThisCase = [];

    internal static void BeginCase() => notedThisCase.Clear();

    private static void Postfix(HediffSet __instance, DamageDef? damDef, BodyPartDepth depth, BodyPartRecord? partParent,
        ref BodyPartRecord? __result)
    {
        if (__result != null || depth == BodyPartDepth.Undefined || partParent != null
            || !RimRunnerDummies.TryGet(__instance.pawn, out _))
        {
            return;
        }

        BodyDef body = __instance.pawn.RaceProps.body;
        BodyPartRecord core = body.corePart;
        if (body.AllParts.Count == 1 && !__instance.PartIsMissing(core))
        {
            __result = core;
            string kind = __instance.pawn.kindDef?.defName ?? "<kind?>";
            string damage = damDef?.defName ?? "<伤型?>";
            if (notedThisCase.Add(kind + "|" + damage + "|" + depth))
            {
                RimRunnerEngineLog.Info("假人深度退路：" + kind + " 受到 " + damage + "（限定深度 " + depth
                                        + "），无法挑出部位，落到唯一部位 " + core.def.defName + "；本用例里同 kind、伤型、深度不再记");
            }
        }
    }
}

[HarmonyPatch(typeof(HediffSet), nameof(HediffSet.GetBrain))]
internal static class Patch_HediffSet_GetBrain_RimRunnerDummy
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(HediffSet __instance, ref BodyPartRecord? __result)
    {
        if (__result != null || !RimRunnerDummies.TryGet(__instance.pawn, out _))
        {
            return;
        }

        BodyDef body = __instance.pawn.RaceProps.body;
        if (body.AllParts.Count == 1 && !__instance.PartIsMissing(body.corePart))
        {
            __result = body.corePart;
        }
    }
}
