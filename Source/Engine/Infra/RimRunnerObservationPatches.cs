using Verse.AI;

namespace RimRunner;

[HarmonyPatch]
internal static class Patch_GenDraw_DrawRadiusRing_RimRunnerObserve
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(GenDraw)))
        {
            if (method.Name != nameof(GenDraw.DrawRadiusRing))
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length >= 2
                && parameters[0].ParameterType == typeof(IntVec3)
                && parameters[1].ParameterType == typeof(float))
            {
                yield return method;
            }
        }
    }

    private static bool Prefix(IntVec3 center, float radius)
    {
        return !RimRunnerRadiusRingCapture.Record(center, radius);
    }
}

[HarmonyPatch(typeof(MoteMaker), nameof(MoteMaker.ThrowText), typeof(Vector3), typeof(Map), typeof(string), typeof(Color), typeof(float))]
internal static class Patch_MoteMaker_RimRunnerObserve
{
    private static void Postfix(Vector3 loc, Map map, string text)
    {
        if (map != null && loc.ToIntVec3().InBounds(map))
        {
            RimRunnerObservationBuffer.RecordMote(loc, map, text);
        }
    }
}

[HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), typeof(Letter), typeof(string), typeof(int), typeof(bool))]
internal static class Patch_LetterStack_RimRunnerObserve
{
    private static void Postfix(
        Letter let, List<Letter> ___letters, List<Letter> ___letterQueue)
    {
        if (___letters.Contains(let) || ___letterQueue.Contains(let))
        {
            RimRunnerObservationBuffer.RecordLetter(let);
        }
    }
}

[HarmonyPatch(typeof(Messages), nameof(Messages.Message), typeof(Message), typeof(bool))]
internal static class Patch_Messages_RimRunnerObserve
{
    private static void Postfix(Message msg, List<Message> ___liveMessages)
    {
        if (___liveMessages.Contains(msg))
        {
            RimRunnerObservationBuffer.RecordMessage(msg);
        }
    }
}

[HarmonyPatch(typeof(JobDriver), nameof(JobDriver.Notify_Starting))]
internal static class Patch_JobDriver_NotifyStarting_RimRunnerObserve
{
    private static void Postfix(JobDriver __instance, Job ___job, Pawn ___pawn)
    {
        if (___job != null)
        {
            RimRunnerObservationBuffer.RecordJobStarted(
                __instance, ___pawn, ___job);
        }
    }
}

[HarmonyPatch(typeof(JobDriver), nameof(JobDriver.Cleanup))]
internal static class Patch_JobDriver_Cleanup_RimRunnerObserve
{
    private static void Prefix(
        JobDriver __instance, JobCondition condition, Job ___job, Pawn ___pawn)
    {
        if (___job != null)
        {
            bool suspended = ___pawn?.jobs?.jobQueue?.Contains(___job) == true;
            RimRunnerObservationBuffer.RecordJobCleaned(
                __instance, ___pawn, ___job, condition, suspended);
        }
    }
}

[HarmonyPatch(typeof(Ability), nameof(Ability.Notify_StartedCasting))]
internal static class Patch_Ability_NotifyStartedCasting_RimRunnerObserve
{
    private static void Postfix(Ability __instance)
    {
        RimRunnerObservationBuffer.RecordAbilityCastingStarted(__instance, __instance.pawn);
    }
}

[HarmonyPatch(typeof(Ability), nameof(Ability.Activate), typeof(LocalTargetInfo), typeof(LocalTargetInfo))]
internal static class Patch_Ability_Activate_RimRunnerObserve
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Ability __instance, out RimRunnerActivationMeters.Snapshot? __state)
    {
        __state = RimRunnerActivationMeters.Enter(__instance);
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Ability __instance, LocalTargetInfo target, LocalTargetInfo dest, bool __result,
        RimRunnerActivationMeters.Snapshot? __state)
    {
        RimRunnerActivationMeters.Record(__state, __instance, target, dest, __result);
    }

    private static void Finalizer()
    {
        RimRunnerActivationMeters.Exit();
    }
}

[HarmonyPatch(typeof(Psycast), nameof(Psycast.Activate), typeof(LocalTargetInfo), typeof(LocalTargetInfo))]
internal static class Patch_Psycast_Activate_RimRunnerObserve
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Psycast __instance, out RimRunnerActivationMeters.Snapshot? __state)
    {
        __state = RimRunnerActivationMeters.Enter(__instance);
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Psycast __instance, LocalTargetInfo target, LocalTargetInfo dest, bool __result,
        RimRunnerActivationMeters.Snapshot? __state)
    {
        RimRunnerActivationMeters.Record(__state, __instance, target, dest, __result);
    }

    private static void Finalizer()
    {
        RimRunnerActivationMeters.Exit();
    }
}

[HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch), typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef))]
internal static class Patch_Projectile_Launch_RimRunnerObserve
{
    private static void Postfix(
        Projectile __instance,
        Thing launcher,
        LocalTargetInfo usedTarget,
        LocalTargetInfo intendedTarget)
    {
        RimRunnerObservationBuffer.RecordProjectileLaunched(
            __instance, launcher, usedTarget, intendedTarget);
    }
}

[HarmonyPatch(typeof(Projectile), "Impact", typeof(Thing), typeof(bool))]
internal static class Patch_Projectile_Impact_RimRunnerObserve
{
    private static void Prefix(
        Projectile __instance,
        Thing? hitThing,
        Thing ___launcher,
        LocalTargetInfo ___usedTarget,
        LocalTargetInfo ___intendedTarget)
    {
        RimRunnerObservationBuffer.RecordProjectileImpact(
            __instance, hitThing, ___launcher, ___usedTarget, ___intendedTarget);
    }
}

[HarmonyPatch(typeof(Projectile_Explosive), "Impact", typeof(Thing), typeof(bool))]
internal static class Patch_ProjectileExplosive_Impact_RimRunnerObserve
{
    private static void Prefix(
        Projectile __instance,
        Thing? hitThing,
        Thing ___launcher,
        LocalTargetInfo ___usedTarget,
        LocalTargetInfo ___intendedTarget)
    {
        RimRunnerObservationBuffer.RecordProjectileImpact(
            __instance, hitThing, ___launcher, ___usedTarget, ___intendedTarget);
    }
}

[HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.PostApplyDamage))]
internal static class Patch_PawnHealth_PostApplyDamage_RimRunnerObserve
{
    private static void Postfix(Pawn ___pawn, DamageInfo dinfo, float totalDamageDealt)
    {
        if (___pawn != null)
        {
            RimRunnerDamageRecorder.RecordDealt(___pawn, totalDamageDealt);
            RimRunnerForeignDamage.Note(___pawn, dinfo, totalDamageDealt);
        }
    }
}

[HarmonyPatch(typeof(Pawn_HealthTracker), "PreApplyDamage")]
internal static class Patch_PawnHealth_PreApplyDamage_RimRunnerObserve
{
    private static bool Prefix(Pawn ___pawn, DamageInfo dinfo, ref bool absorbed)
    {
        if (___pawn != null)
        {
            RimRunnerDamageRecorder.Record(___pawn, dinfo);
            if (RimRunnerInvulnerability.IsAbsorbed(___pawn))
            {
                absorbed = true;
                return false;
            }
        }
        return true;
    }

    private static void Postfix(Pawn ___pawn, ref bool absorbed)
    {
        RimRunnerDamageRecorder.RecordAbsorbed(___pawn, absorbed);
    }
}

[HarmonyPatch(typeof(Verb_MeleeAttack), "TryCastShot")]
internal static class Patch_VerbMeleeAttack_TryCastShot_RimRunnerMeleeHit
{
    private static void Postfix(Verb_MeleeAttack __instance, bool __result)
    {
        if (__result && __instance.CasterPawn is { } attacker && __instance.CurrentTarget.Thing is Pawn victim)
        {
            RimRunnerDamageRecorder.RecordMeleeHit(attacker, victim);
        }
    }
}

[HarmonyPatch(typeof(DamageWorker_AddInjury), "FinalizeAndAddInjury",
    [typeof(Pawn), typeof(Hediff_Injury), typeof(DamageInfo), typeof(DamageWorker.DamageResult)])]
internal static class Patch_AddInjury_FinalizeAndAddInjury_RimRunnerObserve
{
    private static bool Prefix(Pawn pawn, Hediff_Injury injury, DamageInfo dinfo, DamageWorker.DamageResult result,
        ref float __result, out bool __state)
    {
        __state = RimRunnerInvulnerability.IsWoundless(pawn);
        if (!__state)
        {
            return true;
        }

        float landed = Mathf.Min(injury.Severity, pawn.health.hediffSet.GetPartHealth(injury.Part));
        result.totalDamageDealt += landed;
        result.wounded = true;
        result.AddPart(pawn, injury.Part);
        RimRunnerDamageRecorder.RecordLanded(pawn, dinfo, landed);
        __result = landed;
        return false;
    }

    private static void Postfix(Pawn pawn, DamageInfo dinfo, float __result, bool __state)
    {
        if (!__state)
        {
            RimRunnerDamageRecorder.RecordLanded(pawn, dinfo, __result);
        }
    }
}

[HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
internal static class Patch_Thing_TakeDamage_RimRunnerWoundlessApparel
{
    private static bool Prefix(Thing __instance, ref DamageWorker.DamageResult __result)
    {
        if (__instance is Apparel { Wearer: { } wearer } && RimRunnerInvulnerability.IsWoundless(wearer))
        {
            __result = new DamageWorker.DamageResult();
            return false;
        }

        return true;
    }
}
