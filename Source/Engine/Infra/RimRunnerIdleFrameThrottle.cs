namespace RimRunner;

internal static class RimRunnerIdleFrameThrottle
{
    internal const int IdleFrameRate = 10;
    private static bool applied;
    private static bool wroteSlot;
    private static int savedTargetFrameRate;

    internal static void Update(bool idle)
    {
        bool shouldThrottle = idle
                              && Current.ProgramState == ProgramState.Playing
                              && Find.TickManager?.CurTimeSpeed == TimeSpeed.Paused
                              && !RimRunnerScreenshotDriver.CaptureActive;
        if (shouldThrottle == applied)
        {
            return;
        }

        if (shouldThrottle)
        {
            Apply();
        }
        else
        {
            Release();
        }
    }

    private static void Apply()
    {
        savedTargetFrameRate = Waylith.EngineWindow.TargetFrameRate;
        wroteSlot = savedTargetFrameRate is <= 0 or > IdleFrameRate;
        if (wroteSlot)
        {
            Waylith.EngineWindow.TargetFrameRate = IdleFrameRate;
        }

        applied = true;
    }

    internal static void Release()
    {
        if (!applied)
        {
            return;
        }

        applied = false;
        if (wroteSlot && Waylith.EngineWindow.TargetFrameRate == IdleFrameRate)
        {
            Waylith.EngineWindow.TargetFrameRate = savedTargetFrameRate;
        }
    }
}
