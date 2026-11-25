namespace RimRunnerCli;

internal static class DetachedTask
{
    internal static Task Run(Func<Task> work)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(work);
        using AsyncFlowControl suppressed = ExecutionContext.SuppressFlow();
        return Task.Run(work);
    }

    internal static Task Run(Action work)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(work);
        using AsyncFlowControl suppressed = ExecutionContext.SuppressFlow();
        return Task.Run(work);
    }
}
