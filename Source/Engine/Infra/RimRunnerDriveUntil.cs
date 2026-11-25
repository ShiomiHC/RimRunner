using System.Globalization;

namespace RimRunner;

internal sealed class RimRunnerDriveUntil : RimRunnerDriveVerb
{
    internal static readonly RimRunnerDriveUntil Instance = new();

    private const string Within = "within";
    private const string Speed = "speed";

    private const int LetterTextChars = 80;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal override RimRunnerDriveTable.Verb? Spec => null;

    internal override string? CheckArgs(IReadOnlyDictionary<string, string> args)
    {
        if (!args.ContainsKey(Within))
        {
            RimRunnerDriveTable.Param within = RimRunnerDriveTable.Until.Params.First(param => param.Name == Within);
            return "缺少必填参数：" + Within + "（" + within.Meaning + "）";
        }

        return RimRunnerDriveTable.Until.ArgError!(args);
    }

    internal override string? Refusal(RimRunnerDriveOp op)
    {
        if (RimRunnerCurrentMapProbe.EngineCurrentMap() == null)
        {
            return "没有当前地图";
        }

        Dictionary<string, string> conditionArgs = ConditionArgs(op);
        string? refusal;
        if (op.Name == RimRunnerDriveTable.UntilLetter)
        {
            refusal = conditionArgs.Count > 0
                ? RimRunnerDriveTable.UntilLetter + " 不带参数，收到：" + string.Join("、", conditionArgs.Keys.OrderBy(key => key, StringComparer.Ordinal))
                : IsNow(op)
                    ? RimRunnerDriveTable.UntilLetter + " 以调用时为基线，--within=0 必然不成立"
                    : null;
        }
        else if (RimRunnerLiveScripts.Instantiate<RimRunnerLiveCondition>(op.Image!, op.Name, out string? fault) is not { } author)
        {
            refusal = "脚本 " + op.Name + " 无法装载：" + fault;
        }
        else
        {
            op.Condition = author;
            refusal = RimRunnerDriveAuthor.ArgRefusal("条件 " + op.Name, () => author.Params, conditionArgs);
        }

        return refusal ?? (WallSeconds(op) != null ? RimRunnerDriveTime.ForcePauseRefusal() : null);
    }

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        var judge = new Judge(op.Name, op.Condition, ConditionArgs(op));
        IEnumerable<RimRunnerWait> waits = IsNow(op) ? []
            : WallSeconds(op) is { } seconds ? RunWall(op, judge, seconds)
            : RunTicks(op, judge, RimRunnerDriveTable.ParseGameTicks(op.Arg(Within)!)!.Value);
        foreach (RimRunnerWait wait in waits)
        {
            yield return wait;
        }

        bool met = judge.Met;
        if (judge.Fault != null)
        {
            op.Abort(judge.Fault);
        }

        if (op.AbortReason != null)
        {
            yield break;
        }

        op.Met = met;
        if (IsNow(op))
        {
            op.Line("条件 " + op.Name + (met ? " 此刻成立" : " 此刻不成立"));
        }

        judge.WriteLetters(op);
    }

    private static IEnumerable<RimRunnerWait> RunTicks(RimRunnerDriveOp op, Judge judge, int cap)
    {
        RimRunnerOp<RimRunnerWaitUntilResult> wait = RimRunnerTimeDriver.WaitUntil(cap, () => judge.Met);
        RimRunnerSustainedErrorBreaker.Arm(op.Capture!);
        try
        {
            yield return wait;
            if (RimRunnerSustainedErrorBreaker.Tripped)
            {
                op.Abort(RimRunnerSustainedErrorBreaker.TakeDriveTrip() ?? "持续错误级日志熔断");
            }
        }
        finally
        {
            RimRunnerSustainedErrorBreaker.Disarm();
        }

        RimRunnerWaitUntilResult result = wait.Result;
        op.Line("条件 " + op.Name + (result.Met ? " 成立，推进 " + result.Ticks + " tick" : " 不成立，推满 " + result.Ticks + " tick")
                + "（上限 " + cap + "）");
    }

    private static IEnumerable<RimRunnerWait> RunWall(RimRunnerDriveOp op, Judge judge, float requested)
    {
        string speedName = op.Arg(Speed) ?? RimRunnerDriveTable.SpeedNormal;
        TimeSpeed target = RimRunnerDriveTime.ToTimeSpeed(speedName);
        int startTick = Find.TickManager.TicksGame;
        float elapsed = 0f;
        if (!judge.Met)
        {
            RimRunnerTimeDriver.BeginVanillaSpeed(target, out bool boost, out bool neverForceNormal);
            float start = RimRunnerRealtime.Seconds;
            RimRunnerSustainedErrorBreaker.Arm(op.Capture!);
            try
            {
                while (true)
                {
                    yield return RimRunnerWait.Frames(1);
                    elapsed = RimRunnerRealtime.Seconds - start;
                    if (RimRunnerSustainedErrorBreaker.Tripped)
                    {
                        op.Abort(RimRunnerSustainedErrorBreaker.TakeDriveTrip() ?? "持续错误级日志熔断");
                        break;
                    }

                    if (judge.Met || elapsed >= requested)
                    {
                        break;
                    }

                    if (RimRunnerDriveTime.ForcePauseRefusal() is { } forced)
                    {
                        op.Abort(forced);
                        break;
                    }

                    if (Find.TickManager.CurTimeSpeed != target)
                    {
                        RimRunnerTimeDriver.EnsureVanillaSpeed(target);
                    }
                }
            }
            finally
            {
                RimRunnerSustainedErrorBreaker.Disarm();
                RimRunnerTimeDriver.RestoreVanillaSpeed(TimeSpeed.Paused, boost, neverForceNormal);
            }
        }

        bool met = judge.Met;
        op.Line("条件 " + op.Name + (met ? " 成立" : " 不成立") + "，按 " + speedName + " 运行 " + elapsed.ToString("0.#", Invariant)
                + " / " + requested.ToString("0.#", Invariant) + " 秒（" + (Find.TickManager.TicksGame - startTick) + " tick），已暂停");
    }

    private static bool IsNow(RimRunnerDriveOp op) => op.Arg(Within)!.Trim() == "0";

    private static float? WallSeconds(RimRunnerDriveOp op) =>
        IsNow(op) ? null : RimRunnerDriveTable.ParseWallSeconds(op.Arg(Within)!);

    private static Dictionary<string, string> ConditionArgs(RimRunnerDriveOp op) =>
        op.Args.Where(pair => pair.Key is not (Within or Speed)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private sealed class Judge
    {
        private readonly HashSet<Letter> baseline = [];
        private readonly List<Letter> arrived = [];
        private readonly RimRunnerLiveCondition? author;
        private readonly RimRunnerRegressionContext? ctx;
        private bool authorMet;

        internal Judge(string name, RimRunnerLiveCondition? condition, IReadOnlyDictionary<string, string> args)
        {
            if (name == RimRunnerDriveTable.UntilLetter)
            {
                baseline = [.. Find.LetterStack.LettersListForReading];
                return;
            }

            author = condition!;
            ctx = RimRunnerContext.CreateScene("until " + author.Id, RimRunnerCurrentMapProbe.EngineCurrentMap());
            var call = new RimRunnerLiveCall("条件 " + author.Id, author.Params, args);
            RimRunnerLiveActionRunner.Guard(ctx, "条件 " + author.Id, reason => Fault ??= reason, () => author.Begin(ctx, call));
        }

        internal string? Fault { get; private set; }

        internal bool Met
        {
            get
            {
                if (Fault != null)
                {
                    return true;
                }

                if (author != null)
                {
                    if (!authorMet)
                    {
                        RimRunnerLiveActionRunner.Guard(ctx!, "条件 " + author.Id, reason => Fault ??= reason,
                            () => authorMet = author.Met(ctx!));
                    }

                    return authorMet || Fault != null;
                }

                foreach (Letter letter in Find.LetterStack.LettersListForReading)
                {
                    if (!baseline.Contains(letter) && !arrived.Contains(letter))
                    {
                        arrived.Add(letter);
                    }
                }

                return arrived.Count > 0;
            }
        }

        internal void WriteLetters(RimRunnerDriveOp op)
        {
            foreach (Letter letter in arrived)
            {
                string text = letter is ChoiceLetter choice ? choice.Text.Resolve().StripTags().Split('\n')[0] : "";
                if (text.Length > LetterTextChars)
                {
                    text = text[..LetterTextChars] + "…";
                }

                op.Line("新信 " + letter.Label.Resolve().StripTags() + "（" + letter.def?.defName + "，tick " + letter.arrivalTick + "）"
                        + (text.Length > 0 ? "：" + text : ""));
            }
        }
    }
}
