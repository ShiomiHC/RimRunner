namespace RimRunner;

internal sealed class RimRunnerSceneOutcome
{
    internal string? AbortLine;
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
                outcome.AbortLine = DescribeAbort(current.id, exception);
            }
        }

        outcome.Warnings.AddRange(current.asserts.Skip(assertMark).Where(entry => !entry.pass));
        outcome.Messages.AddRange(current.diagnostics.Skip(diagnosticMark)
            .Select(entry => entry.label + "：" + entry.value));
        return outcome;
    }

    private static string DescribeAbort(string showcaseId, Exception exception)
    {
        RimRunnerClaimedFault? fault = RimRunnerClaimedFault.Unwrap(exception);
        if (fault == null)
        {
            Log.Error(RimRunnerLog.Prefix + " 场面 " + showcaseId + " 执行时抛出异常：" + exception);
            return "抛出异常：" + exception.GetType().Name + ": " + exception.Message;
        }

        return fault.Kind == RimRunnerClaimedFault.FaultKind.Precondition
            ? "装配前提不成立：" + fault.Message
            : "本产品侧失败：" + fault.Message;
    }
}
