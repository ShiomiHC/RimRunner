using System.Diagnostics;
using Verse.AI;

namespace RimRunner;

/// <summary>把登记 pawn 的 AI 自发 job 压成无期限 Wait 并挡随机精神态与社交斗殴。</summary>
/// <remarks>思维树挑出的 job 改成 Wait，排队的照常；随机精神态按 pawn 等效关掉 enableRandomMentalStates。绕过思维树起的 job（受击逃跑、力竭昏倒等）照常；hediff 触发与强制的精神态照常进入。</remarks>
public static class RimRunnerAgencyPin
{
    private static readonly RimRunnerPawnFixtureSet Pinned = new(
        "RimRunnerAgencyPin.Add", ReleaseLeaked);

    private static readonly Dictionary<Pawn, HostilityResponseMode> HostilityBefore = [];

    internal static void Add(Pawn pawn)
    {
        if (pawn == null) throw new ArgumentNullException(nameof(pawn));
        if (pawn.jobs == null) throw new ArgumentException("被钉住的 pawn 没有 job tracker", nameof(pawn));
        RimRunnerEnvChecks.NoteUse(RimRunnerFacility.AgencyPin);
        Pinned.Add(pawn);
        pawn.jobs.StopAll();
        pawn.pather?.StopDead();
        if (pawn.Spawned && !pawn.Dead)
        {
            pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait), JobCondition.InterruptForced);
        }
    }

    internal static void Remove(Pawn? pawn)
    {
        if (pawn != null) Pinned.Remove(pawn);
    }

    internal static bool IsRegistered(Pawn? pawn) => pawn != null && Pinned.Contains(pawn);

    internal static void SaveHostility(Pawn pawn, HostilityResponseMode before)
    {
        if (!HostilityBefore.ContainsKey(pawn)) HostilityBefore[pawn] = before;
    }

    internal static void RestoreHostility(Pawn? pawn)
    {
        if (pawn == null || !HostilityBefore.TryGetValue(pawn, out HostilityResponseMode before)) return;
        HostilityBefore.Remove(pawn);
        pawn.playerSettings?.hostilityResponse = before;
    }

    internal static bool RandomMentalStatesSuppressed { get; private set; }

    private static int engineMentalSwitchDepth;

    internal static bool InEngineMentalSwitchScope => engineMentalSwitchDepth > 0;

    internal static void EnterEngineMentalSwitchScope() => engineMentalSwitchDepth++;

    internal static void ExitEngineMentalSwitchScope() => engineMentalSwitchDepth--;

    internal static void SetRandomMentalStatesSuppressed(bool on) => RandomMentalStatesSuppressed = on;

    internal static bool BlocksRandomMentalStates(Pawn? pawn)
        => pawn != null && (RandomMentalStatesSuppressed || IsPinned(pawn));

    private static void ReleaseLeaked(Pawn pawn)
    {
        RestoreHostility(pawn);
        if (pawn.Spawned && !pawn.Dead && pawn.CurJobDef == JobDefOf.Wait)
        {
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }
    }

    internal static void ClearForNewWorld()
    {
        Pinned.Clear();
        HostilityBefore.Clear();
        RandomMentalStatesSuppressed = false;
    }

    /// <summary>钉住查询：该 pawn 是否在钉住登记表内且在图上。</summary>
    /// <remarks>离图期（飞行体容器内、商队、搬运中）钉住不生效。</remarks>
    public static bool IsPinned(Pawn? pawn)
    {
        return pawn != null && pawn.Spawned && Pinned.Contains(pawn);
    }
}

/// <summary>被测体生成时的 AI 档：钉住，或保留自主 AI。</summary>
public enum RimRunnerDisposableAgency
{
    /// <summary>生成后钉住：生成后登记钉住并把 hostilityResponse 置 Ignore。</summary>
    Pinned,

    /// <summary>保留自主 AI：找饭、反击、自取 job 一类的行为照常。</summary>
    /// <remarks>本档保留随机精神态与社交斗殴。</remarks>
    Free
}

[HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextConstantThinkTreeJob")]
internal static class Patch_Pawn_JobTracker_DetermineNextConstantThinkTreeJob_RimRunnerPin
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(Pawn_JobTracker __instance, Pawn ___pawn, ref ThinkResult __result)
    {
        if (__result.Job != null && RimRunnerAgencyPin.IsPinned(___pawn))
        {
            __result = ThinkResult.NoJob;
        }
    }
}

[HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextJob")]
internal static class Patch_Pawn_JobTracker_DetermineNextJob_RimRunnerPin
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(Pawn_JobTracker __instance, Pawn ___pawn, ref ThinkResult __result)
    {
        if (__result.FromQueue || !RimRunnerAgencyPin.IsPinned(___pawn)) return;
        __result = __instance.curJob == null
            ? new ThinkResult(JobMaker.MakeJob(JobDefOf.Wait), null)
            : ThinkResult.NoJob;
    }
}

[HarmonyPatch(typeof(JobDriver_Wait), "CheckForAutoAttack")]
internal static class Patch_JobDriver_Wait_CheckForAutoAttack_RimRunnerPin
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static bool Prefix(JobDriver_Wait __instance)
    {
        return !RimRunnerAgencyPin.IsPinned(__instance.pawn);
    }
}

[HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.CheckSocialFightStart))]
internal static class Patch_Pawn_InteractionsTracker_CheckSocialFightStart_RimRunnerPin
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static bool Prefix(Pawn ___pawn, Pawn initiator, ref bool __result)
    {
        if (!RimRunnerAgencyPin.BlocksRandomMentalStates(___pawn) && !RimRunnerAgencyPin.BlocksRandomMentalStates(initiator)) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.StartSocialFight))]
internal static class Patch_Pawn_InteractionsTracker_StartSocialFight_RimRunnerPin
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static bool Prefix(Pawn ___pawn, Pawn otherPawn)
    {
        return !RimRunnerAgencyPin.BlocksRandomMentalStates(___pawn) && !RimRunnerAgencyPin.BlocksRandomMentalStates(otherPawn);
    }
}

[HarmonyPatch(typeof(MentalBreaker), nameof(MentalBreaker.MentalBreakerTickInterval))]
internal static class Patch_MentalBreaker_MentalBreakerTickInterval_RimRunnerPin
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Prefix(Pawn ___pawn, out bool __state)
    {
        __state = DebugSettings.enableRandomMentalStates && RimRunnerAgencyPin.BlocksRandomMentalStates(___pawn);
        if (__state)
        {
            RimRunnerAgencyPin.EnterEngineMentalSwitchScope();
            DebugSettings.enableRandomMentalStates = false;
        }
    }

    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state)
        {
            DebugSettings.enableRandomMentalStates = true;
            RimRunnerAgencyPin.ExitEngineMentalSwitchScope();
        }
        return __exception;
    }
}

[HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
internal static class Patch_MentalStateHandler_TryStartMentalState_RimRunnerPin
{
    private const int CallerFrames = 3;

    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(bool __result, Pawn ___pawn, MentalStateDef stateDef, bool forced)
    {
        RimRunnerContext? owner = RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Case => RimRunnerContext.Active,
            RimRunnerPhase.Scene or RimRunnerPhase.Enumeration or RimRunnerPhase.None => null
        };
        if (owner == null || !__result)
        {
            return;
        }

        if (!forced && !DebugSettings.enableRandomMentalStates
            && !RimRunnerAgencyPin.BlocksRandomMentalStates(___pawn)
            && !RimRunnerAgencyPin.InEngineMentalSwitchScope)
        {
            RimRunnerSentinel.Warn(RimRunnerWarningKey.MentalSwitchWrite,
                "DebugSettings.enableRandomMentalStates 为 false，但未钉住的 pawn 仍进入非强制精神态："
                + "全局开关挡不住劝化失败直调的社交斗殴与 hediff 精神态",
                ___pawn.LabelShort + "（" + ___pawn.ThingID + "）进入 " + stateDef.defName);
        }

        if (!RimRunnerAgencyPin.IsPinned(___pawn))
        {
            return;
        }

        string? caller = RimRunnerCallSite.Describe(new StackTrace(1, false), CallerFrames,
            type => type == typeof(MentalStateHandler) || type == typeof(Patch_MentalStateHandler_TryStartMentalState_RimRunnerPin));
        owner.AddFailureFingerprint(RimRunnerFindingKey.AgencyPinMentalState, ___pawn.LabelShort + "（" + ___pawn.ThingID + "）",
            "被钉住的 pawn 于 tick " + Find.TickManager.TicksGame + " 进入精神态 " + stateDef.defName + "（forced=" + forced + "）←"
            + (caller ?? "<来路?>"));
    }
}
