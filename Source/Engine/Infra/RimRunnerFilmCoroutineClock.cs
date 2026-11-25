using System.Collections;

namespace RimRunner;

internal static class RimRunnerFilmCoroutineClock
{
    private static FieldInfo? handlesField;
    private static FieldInfo? suspendKindField;
    private static FieldInfo? resumeTimeField;
    private static object? waitSecondsKind;

    internal static void Install(Harmony harmony)
    {
        if (!RimRunnerProcessMode.Film)
        {
            return;
        }

        Type? scheduler = AccessTools.TypeByName("UnityEngine.CoroutineScheduler");
        Type? handle = AccessTools.TypeByName("UnityEngine.CoroutineHandle");
        Type? kind = AccessTools.TypeByName("UnityEngine.CoroutineSuspendKind");
        MethodInfo? step = scheduler == null
            ? null
            : AccessTools.Method(scheduler, "Step", [typeof(bool), typeof(float), typeof(bool)]);
        FieldInfo? handles = scheduler == null ? null : AccessTools.Field(scheduler, "_handles");
        FieldInfo? suspendKind = handle == null ? null : AccessTools.Field(handle, "SuspendKind");
        FieldInfo? resumeTime = handle == null ? null : AccessTools.Field(handle, "ResumeTime");
        object? waitSeconds = kind != null && Enum.IsDefined(kind, "WaitSeconds") ? Enum.Parse(kind, "WaitSeconds") : null;
        if (step == null || handles == null || suspendKind == null || resumeTime?.FieldType != typeof(float)
            || waitSeconds == null)
        {
            RimRunnerStartupErrors.Raise("拍摄期协程计时：UnityStubs 里无法解析协程调度器的 Step(bool, float, bool)、_handles，"
                                         + "或句柄的 SuspendKind、ResumeTime，以及 CoroutineSuspendKind.WaitSeconds："
                                         + "钉钟期间 WaitForSeconds 不恢复");
            return;
        }

        handlesField = handles;
        suspendKindField = suspendKind;
        resumeTimeField = resumeTime;
        waitSecondsKind = waitSeconds;
        harmony.Patch(step, prefix: new HarmonyMethod(AccessTools.Method(typeof(RimRunnerFilmCoroutineClock), nameof(Prefix))));
    }

    private static void Prefix(ref float elapsed)
    {
        if (RimRunnerFilmClock.TryPinnedRealtime(out float pinned))
        {
            elapsed = pinned;
        }
    }

    internal static float Now => RimRunnerFilmClock.TryPinnedRealtime(out float pinned) ? pinned : Waylith.Time.Elapsed;

    internal static bool WaitDue(float now)
    {
        if (handlesField == null)
        {
            return false;
        }

        foreach (object handle in (IEnumerable)handlesField.GetValue(null)!)
        {
            if (Equals(suspendKindField!.GetValue(handle), waitSecondsKind) && (float)resumeTimeField!.GetValue(handle)! <= now)
            {
                return true;
            }
        }

        return false;
    }

    internal static void Shift(float delta)
    {
        if (handlesField == null || delta == 0f)
        {
            return;
        }

        foreach (object handle in (IEnumerable)handlesField.GetValue(null)!)
        {
            if (Equals(suspendKindField!.GetValue(handle), waitSecondsKind))
            {
                resumeTimeField!.SetValue(handle, (float)resumeTimeField.GetValue(handle)! + delta);
            }
        }
    }
}
