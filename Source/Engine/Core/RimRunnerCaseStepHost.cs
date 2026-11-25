namespace RimRunner;

internal sealed class RimRunnerCaseStepHost(RimRunnerContext context, string caseId, bool realFrame) : IRimRunnerStepHost
{
    public string? Refusal(RimRunnerWait wait) =>
        wait is RimRunnerVanillaSpeedWait && !realFrame
            ? "用例 " + caseId + " 未声明 IRimRunnerRealFrameCase，无法按原版速度推 tick（RimRunnerWait.Ticks / RealSeconds）"
            : null;

    public void TrackRestore(Action restore, string description) => context.TrackRestore(restore, description);
}
