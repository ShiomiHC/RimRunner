using Verse.AI;

namespace RimRunner;

[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
internal static class Patch_Pawn_JobTracker_TryTakeOrderedJob_RimRunnerSentinel
{
    private static void Prefix(Pawn_JobTracker __instance, Pawn ___pawn, Job job, bool requestQueueing)
    {
        string? caseId = RimRunnerObservationBuffer.ActiveCaseId;
        if (caseId == null || requestQueueing || !KeyBindingDefOf.QueueOrder.IsDownEvent)
        {
            return;
        }

        if (__instance == null || ___pawn == null || job == null)
        {
            return;
        }

        if (__instance.curJob != null && __instance.curJob.JobIsSameAs(___pawn, job))
        {
            return;
        }

        bool canInterrupt = __instance.IsCurrentJobPlayerInterruptible()
            && !(__instance.curJob?.def?.forceCompleteBeforeNextJob ?? false);
        bool isIdle = ___pawn.mindState?.IsIdle == true
            || __instance.curJob == null
            || __instance.curJob.def.isIdle;
        if (!((canInterrupt && !isIdle) || (!canInterrupt && __instance.jobQueue.Count > 0)))
        {
            return;
        }

        RimRunnerSentinel.Warn(RimRunnerWarningKey.QueueKey, caseId + "：TryTakeOrderedJob 下令时排队键读作按下、未传 requestQueueing 且键态改变排入结果："
                                + "按着排队键会把指令并入队列而非顶掉或清空，下令结果取决于键盘态",
            (___pawn.LabelShort ?? "<null pawn>") + " ← " + (job.def?.defName ?? "<null job>")
            + "（当前 " + (__instance.curJob?.def?.defName ?? "无") + "）");
    }
}
