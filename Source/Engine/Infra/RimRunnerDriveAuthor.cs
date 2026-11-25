namespace RimRunner;

internal sealed class RimRunnerDriveAuthor : RimRunnerDriveVerb
{
    internal static readonly RimRunnerDriveAuthor Instance = new();

    internal override RimRunnerDriveTable.Verb? Spec => null;

    internal override string? Refusal(RimRunnerDriveOp op)
    {
        RimRunnerLiveAction? action = RimRunnerLiveScripts.Instantiate<RimRunnerLiveAction>(op.Image!, op.Name, out string? fault);
        if (action == null)
        {
            return "脚本 " + op.Name + " 无法装载：" + fault;
        }

        if (RimRunnerCurrentMapProbe.EngineCurrentMap() == null)
        {
            return "没有当前地图";
        }

        op.Action = action;
        return ArgRefusal("动作 " + op.Name, () => action.Params, op.Args);
    }

    internal static string? ArgRefusal(string owner, Func<IReadOnlyList<RimRunnerLiveParam>> parameters,
        IReadOnlyDictionary<string, string> args)
    {
        try
        {
            return RimRunnerLiveCall.ArgRefusal(owner, parameters(), args);
        }
        catch (Exception exception)
        {
            return owner + " 的参数表抛出 " + RimRunnerLiveScripts.Describe(exception);
        }
    }

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        var outcome = new RimRunnerLiveActionOutcome();
        foreach (RimRunnerWait wait in RimRunnerLiveActionRunner.Run(op.Action!, op.Args, outcome, op.Capture))
        {
            yield return wait;
        }

        if (outcome.Abort != null)
        {
            op.Abort(outcome.Abort);
        }

        foreach (RimRunnerAssertEntry entry in outcome.Warnings)
        {
            op.Line("[" + RimRunnerLiveContract.SceneFinding.Tag(entry.sceneKind) + "] " + entry.label + "：" + entry.actual);
        }

        foreach (string message in outcome.Messages)
        {
            op.Line(message);
        }
    }
}
