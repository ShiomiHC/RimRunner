namespace RimRunner;

internal static class RimRunnerTooltipState
{
    private static bool resolved;
    private static AccessTools.FieldRef<Dictionary<int, ActiveTip>>? activeTips;
    private static AccessTools.FieldRef<int>? frame;

    private static bool Resolve()
    {
        if (!resolved)
        {
            resolved = true;
            FieldInfo? tips = AccessTools.Field(typeof(TooltipHandler), "activeTips");
            FieldInfo? counter = AccessTools.Field(typeof(TooltipHandler), "frame");
            if (tips == null || counter == null)
            {
                RimRunnerEngineFault.Raise("无法解析 TooltipHandler 的 activeTips / frame：悬停的 tip 清空与未出现检测不生效");
            }
            else
            {
                activeTips = AccessTools.StaticFieldRefAccess<Dictionary<int, ActiveTip>>(tips);
                frame = AccessTools.StaticFieldRefAccess<int>(counter);
            }
        }

        return activeTips != null;
    }

    internal static void Clear()
    {
        if (Resolve())
        {
            activeTips!().Clear();
        }
    }

    internal static double? PendingSeconds()
    {
        if (!Resolve())
        {
            return null;
        }

        double now = Time.realtimeSinceStartup;
        int currentFrame = frame!();
        double? longest = null;
        foreach (ActiveTip tip in activeTips!().Values)
        {
            if (tip.lastTriggerFrame != currentFrame)
            {
                continue;
            }

            double due = tip.firstTriggerTime + tip.signal.delay;
            if (now > due)
            {
                continue;
            }

            double remaining = due - now;
            if (longest == null || remaining > longest.Value)
            {
                longest = remaining;
            }
        }

        return longest;
    }
}
