namespace RimRunner;

internal sealed class RimRunnerLiveActionOutcome
{
    internal string? Abort;
    internal readonly List<RimRunnerAssertEntry> Warnings = [];
    internal readonly List<string> Messages = [];
}

internal static class RimRunnerLiveActionRunner
{
    internal static IEnumerable<RimRunnerWait> Run(RimRunnerLiveAction action, IReadOnlyDictionary<string, string> args,
        RimRunnerLiveActionOutcome outcome, RimRunnerLogCapture? breakerCapture)
    {
        RimRunnerRegressionContext ctx = RimRunnerContext.CreateScene("do " + action.Id, RimRunnerCurrentMapProbe.EngineCurrentMap());
        bool arm = breakerCapture != null && !RimRunnerSustainedErrorBreaker.Armed;
        if (arm)
        {
            RimRunnerSustainedErrorBreaker.Arm(breakerCapture!);
        }

        try
        {
            var call = new RimRunnerLiveCall("动作 " + action.Id, action.Params, args);
            IEnumerator<RimRunnerWait>? body = null;
            try
            {
                if (Guard(ctx, action, outcome, () => body = action.Run(ctx, call).GetEnumerator()))
                {
                    while (true)
                    {
                        if (RimRunnerSustainedErrorBreaker.Tripped)
                        {
                            outcome.Abort ??= RimRunnerSustainedErrorBreaker.TakeDriveTrip() ?? "持续错误级日志熔断";
                            break;
                        }

                        bool moved = false;
                        if (!Guard(ctx, action, outcome, () => moved = body!.MoveNext()) || !moved)
                        {
                            break;
                        }

                        yield return body!.Current;
                    }
                }
            }
            finally
            {
                if (body != null)
                {
                    Guard(ctx, action, outcome, body.Dispose);
                }
            }
        }
        finally
        {
            if (arm)
            {
                RimRunnerSustainedErrorBreaker.Disarm();
            }

            outcome.Warnings.AddRange(ctx.Current!.asserts.Where(entry => !entry.pass));
            outcome.Messages.AddRange(ctx.Current.diagnostics.Select(entry => entry.label + "：" + entry.value));
        }
    }

    internal static bool Guard(RimRunnerRegressionContext ctx, RimRunnerLiveAction action, RimRunnerLiveActionOutcome outcome,
        Action body) =>
        Guard(ctx, "动作 " + action.Id, reason => outcome.Abort ??= reason, body);

    internal static bool Guard(RimRunnerRegressionContext ctx, string owner, Action<string> onAbort, Action body)
    {
        using (ctx.EnterScene())
        {
            try
            {
                body();
                return true;
            }
            catch (Exception exception)
            {
                onAbort(Describe(owner, exception));
                return false;
            }
        }
    }

    internal const string ArgInvalidPrefix = "参数不合法：";
    internal const string ProductFaultPrefix = "本产品侧失败：";

    internal static string Describe(string owner, Exception exception)
    {
        if (exception is RimRunnerLiveArgException)
        {
            return ArgInvalidPrefix + exception.Message;
        }

        RimRunnerClaimedFault? fault = RimRunnerClaimedFault.Unwrap(exception);
        if (fault == null)
        {
            Log.Error(RimRunnerLog.Prefix + " " + owner + " 抛出异常：" + exception);
            return owner + " 抛出异常：" + exception.GetType().Name + ": " + exception.Message;
        }

        return fault.Kind == RimRunnerClaimedFault.FaultKind.Precondition
            ? "前提不成立：" + fault.Message
            : ProductFaultPrefix + fault.Message;
    }
}
