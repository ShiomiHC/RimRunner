namespace RimRunner;

internal static class RimRunnerFrame
{
    internal static int Current => Waylith.UnityCompat.UnityRuntime.Current!.FrameCount;
}
