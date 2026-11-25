using System.Diagnostics;
using System.Threading;

namespace RimRunner;

internal static class RimRunnerActivity
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long lastActivityMs;

    internal static void Touch() => Volatile.Write(ref lastActivityMs, Clock.ElapsedMilliseconds);

    internal static long IdleSeconds => (Clock.ElapsedMilliseconds - Volatile.Read(ref lastActivityMs)) / 1000;

    internal static void NoteRunState()
    {
        if (RimRunnerRunCoordinator.GetSnapshot().State != RimRunnerRunState.Idle)
        {
            Touch();
        }
    }

    internal static void SampleInput()
    {
        if (Waylith.Input.InputState.EventsThisFrame.Count > 0)
        {
            Touch();
        }
    }
}

[HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
internal static class Patch_RootPlay_Update_RimRunnerActivity
{
    private static bool Prepare() => GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);

    private static void Postfix() => RimRunnerActivity.SampleInput();
}
