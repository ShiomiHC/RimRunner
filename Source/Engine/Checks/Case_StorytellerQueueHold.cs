namespace RimRunner;

/// <summary>叙事者停用态：用例期间到期事件扣住不发，清出时点明去向，并清空遗留队列。</summary>
/// <remarks>只验排入的当拍到期 RaidEnemy 与远期事件：推 3 tick 后仍在队、未试发；清出前的去向；收尾清出的记录（事件名、入队窗与入队方）；下一条用例开始执行时清空遗留并写明在哪条用例开始执行时清除。</remarks>
[RimRunnerEnvCheck]
public class Case_StorytellerQueueHold : RimRunnerRegression
{
    private const string Source = "引擎承诺：用例期间叙事者排队的事件不发，每条用例开始执行前清空并在残留段写明由谁排入；"
                                  + "不成立时事件在用例体内仍会发出，留下的 pawn 与信件记在不相干的用例名下";

    public override string Id => "STQ1";
    public override RimRunnerLevel Level => RimRunnerLevel.World;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        RimRunnerFault.Require(RimRunnerStorytellerQuiesce.StillInEffect, "[engine] 用例期间叙事者没有被引擎接管，扣发无从验证",
            RimRunnerStorytellerQuiesce.DescribeState());
        IncidentQueue queue = Find.Storyteller.incidentQueue;
        RimRunnerFault.Require(queue.Count == 0, "[engine] 开始执行时叙事者队列里已有事件", queue.DebugQueueReadout);

        QueuedIncident held = Unfireable();
        queue.Add(held);
        RimRunnerTimeDriver.AdvanceBurst(3);
        ctx.AssertTrue("STQ1-Held", Contains(queue, held) && !held.TriedToFire,
            "排进一条当拍到期的袭击，推 3 tick 后：仍在队列=" + Contains(queue, held) + "，已试发=" + held.TriedToFire
            + "（应仍在队列、未试发）",
            RimRunnerExpectationSource.Literal(Source));

        QueuedIncident later = Unfireable(Find.TickManager.TicksGame + GenDate.TicksPerDay);
        queue.Add(later);
        string heldFate = RimRunnerStorytellerQuiesce.Fate(held);
        string laterFate = RimRunnerStorytellerQuiesce.Fate(later);
        ctx.AssertTrue("STQ1-Fate", heldFate == "被扣住未发" && laterFate == "清出时未到期",
            "到期事件=" + heldFate + "（应被扣住未发），远期事件=" + laterFate + "（应清出时未到期）",
            RimRunnerExpectationSource.Literal(Source));
        List<string> drained = RimRunnerStorytellerQuiesce.DrainQueued("STQ1-probe");
        string line = string.Join("|", drained);
        ctx.AssertTrue("STQ1-Drained", drained.Count == 2
                                       && drained.All(entry => entry.StartsWith("RaidEnemy 于STQ1 体内入队←Case_StorytellerQueueHold."))
                                       && queue.Count == 0,
            "清出的记录=[" + line + "]（应恰两条，都写明本用例体内入队），清后队列剩 " + queue.Count + " 条",
            RimRunnerExpectationSource.Literal(Source));

        queue.Add(Unfireable());
        RimRunnerStorytellerQuiesce.Ensure("STQ1-next");
        var between = new List<string>();
        RimRunnerStorytellerQuiesce.DrainBetween(between);
        string betweenLine = string.Join("|", between);
        ctx.AssertTrue("STQ1-EnsureClear", queue.Count == 0 && between.Count == 1
                                           && betweenLine.Contains("RaidEnemy 于STQ1 体内入队←Case_StorytellerQueueHold.")
                                           && betweenLine.Contains("STQ1-next 开始执行时清除"),
            "下一条用例开始执行前清队列后剩 " + queue.Count + " 条，残留记录=[" + betweenLine + "]（应恰一条，写明在哪条用例开始执行时清除）",
            RimRunnerExpectationSource.Literal(Source));
    }

    private static QueuedIncident Unfireable() => Unfireable(Find.TickManager.TicksGame);

    private static QueuedIncident Unfireable(int fireTick) =>
        new(new FiringIncident(IncidentDefOf.RaidEnemy, null, new IncidentParms { target = Find.World }),
            fireTick, 0);

    private static bool Contains(IncidentQueue queue, QueuedIncident target)
    {
        foreach (QueuedIncident queued in queue)
        {
            if (ReferenceEquals(queued, target))
            {
                return true;
            }
        }

        return false;
    }
}
