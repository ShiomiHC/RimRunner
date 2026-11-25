namespace RimRunner;

internal sealed class RimRunnerSessionDiagnosticSnapshot
{
    internal int CasesDone;
    internal int CasesTotal;
    internal string? CurrentCaseId;
    internal int CurrentPhase = -1;
    internal int PhaseCount;
    internal int AssertCount;
    internal string? FrameSpeed;
    internal int BodyTicks;
    internal int BodyFrames;
    internal bool BodyPendingFinalize;
    internal string? PendingArtifactId;
    internal string? PendingArtifactState;
}

public static partial class RimRunnerSession
{
    private static volatile RimRunnerSessionDiagnosticSnapshot publishedDiagnostics = new();

    internal static RimRunnerSessionDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        return BuildDiagnosticSnapshot();
    }

    internal static RimRunnerSessionDiagnosticSnapshot GetPublishedDiagnosticSnapshot()
    {
        return publishedDiagnostics;
    }

    private static void PublishDiagnosticSnapshot()
    {
        publishedDiagnostics = BuildDiagnosticSnapshot();
    }

    private static RimRunnerSessionDiagnosticSnapshot BuildDiagnosticSnapshot()
    {
        RimRunnerSessionDiagnosticSnapshot snapshot =
            new();
        if (continuation == null)
        {
            return snapshot;
        }

        snapshot.CasesDone = continuation.Report.EntryCount;
        snapshot.CasesTotal = continuation.TotalCaseCount;
        snapshot.CurrentCaseId = continuation.CurrentCaseId;
        snapshot.CurrentPhase = continuation.CurrentCaseId == null
            ? -1
            : continuation.NextPhase;
        snapshot.AssertCount = continuation.CurrentEntry?.asserts.Count ?? 0;
        if (continuation.CurrentCaseId != null)
        {
            RimRunnerTestCase? testCase = RimRunnerRegistry.All.FirstOrDefault(
                item => item.Id == continuation.CurrentCaseId);
            snapshot.PhaseCount = testCase is IRimRunnerPhasedCase phased
                ? phased.PhaseCount
                : 1;
        }

        RealFrameExecution? realFrame = continuation.RealFrameExecution;
        if (realFrame != null)
        {
            snapshot.FrameSpeed = realFrame.Stepper?.TicksSpeed?.ToString();
            snapshot.BodyTicks = Find.TickManager.TicksGame - realFrame.StartGameTick;
            snapshot.BodyFrames = realFrame.Stepper?.Frames ?? 0;
            snapshot.BodyPendingFinalize = realFrame.PendingFinalize;
        }

        if (continuation.SuspendedPhase?.Stepper is { } suspended)
        {
            snapshot.BodyFrames = suspended.Frames;
        }

        PendingCaseFinalization? pending = continuation.PendingCase;
        if (pending != null)
        {
            snapshot.PendingArtifactId = pending.ArtifactId;
            snapshot.PendingArtifactState =
                RimRunnerScreenshotDriver.Get(pending.ArtifactId!)?.State;
        }

        return snapshot;
    }
}
