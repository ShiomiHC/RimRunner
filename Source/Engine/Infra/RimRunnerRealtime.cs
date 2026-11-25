namespace RimRunner;

internal static class RimRunnerRealtime
{
    internal static float Seconds => (float)PreciseSeconds;

    internal static double PreciseSeconds => Waylith.Time.RealtimeSinceStartupAsDouble;
}
