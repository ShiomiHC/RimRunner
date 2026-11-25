namespace RimRunner;

internal static class RimRunnerStepClock
{
    private static bool? pausedBefore;

    internal static bool Taken => pausedBefore != null;

    internal static string? Begin()
    {
        if (pausedBefore != null)
        {
            return null;
        }

        TickManager ticks = Find.TickManager;
        if (ticks.CurTimeSpeed != TimeSpeed.Paused)
        {
            pausedBefore = false;
            return null;
        }

        ticks.CurTimeSpeed = TimeSpeed.Normal;
        if (ticks.CurTimeSpeed != TimeSpeed.Normal)
        {
            return "解除暂停被拒（游戏此刻不受玩家控制）";
        }

        pausedBefore = true;
        return null;
    }

    internal static void End()
    {
        if (pausedBefore == true)
        {
            Find.TickManager?.Pause();
        }

        pausedBefore = null;
    }
}
