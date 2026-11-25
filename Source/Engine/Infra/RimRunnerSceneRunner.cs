namespace RimRunner;

internal sealed class RimRunnerSceneOutcome
{
    internal string? AbortLine;
    internal RimRunnerClaimedFault.FaultKind? AbortKind;
    internal readonly List<RimRunnerAssertEntry> Warnings = [];
    internal readonly List<string> Messages = [];
}

internal static class RimRunnerSceneRunner
{
    internal static RimRunnerSceneOutcome RunGuarded(RimRunnerContext showcaseCtx, Action body)
    {
        RimRunnerResultEntry current = showcaseCtx.Current!;
        int assertMark = current.asserts.Count;
        int diagnosticMark = current.diagnostics.Count;
        var outcome = new RimRunnerSceneOutcome();
        using (showcaseCtx.EnterScene())
        {
            try
            {
                body();
            }
            catch (Exception exception)
            {
                DescribeAbort(outcome, current.id, exception);
            }
        }

        outcome.Warnings.AddRange(current.asserts.Skip(assertMark).Where(entry => !entry.pass));
        outcome.Messages.AddRange(current.diagnostics.Skip(diagnosticMark)
            .Select(entry => entry.label + "：" + entry.value));
        return outcome;
    }

    private static void DescribeAbort(RimRunnerSceneOutcome outcome, string showcaseId, Exception exception)
    {
        RimRunnerClaimedFault? fault = RimRunnerClaimedFault.Unwrap(exception);
        if (fault == null)
        {
            Log.Error(RimRunnerLog.Prefix + " 场面 " + showcaseId + " 执行时抛出异常：" + exception);
            outcome.AbortLine = "抛出异常：" + exception.GetType().Name + ": " + exception.Message;
            return;
        }

        outcome.AbortKind = fault.Kind;
        outcome.AbortLine = fault.Kind == RimRunnerClaimedFault.FaultKind.Precondition
            ? "装配前提不成立：" + fault.Message
            : "本产品侧失败：" + fault.Message;
    }
}
