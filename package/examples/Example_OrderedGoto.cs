using System.Linq;
using RimRunner;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimRunnerExamples;

/// <summary>下令走到 5–8 格外的一格：推进到站上那一格，job 观测里有这条 Goto 的开始。</summary>
/// <remarks>只验玩家下令的 Goto 在 600 tick 内到达，以及 ObservedJobs 收到它的 Started。</remarks>
[RimRunnerGroup("examples")]
public sealed class Example_OrderedGoto : RimRunnerRegression
{
    private const int Cap = 600;

    public override string Id => "EXAMPLE-GOTO";
    public override RimRunnerLevel Level => RimRunnerLevel.World;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        Pawn pawn = RimRunnerPawnGen.Subject(ctx, RimRunnerSubjectSpec.DisposableHumanlike(RimRunnerDisposableAgency.Free));
        // 饥饿与困倦会打断 Goto；用例末引擎解除冻结并还原需求值
        ctx.SuspendNeeds(pawn);

        Map map = pawn.Map;
        IntVec3 start = pawn.Position;
        bool found = CellFinder.TryFindRandomCellNear(start, map, 8,
            c => c.Standable(map) && c.DistanceTo(start) >= 5f && pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly),
            out IntVec3 dest);
        RimRunnerFault.Require(found, "8 格内没有可达的可站立格", start.ToString());

        pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, dest), JobTag.Misc);
        // 逐 tick 判定：到达后自主 AI 会接着开始别的 job，推进粒度粗时可能已先离开该格
        int used = RimRunnerTimeDriver.AdvanceUntil(Cap, () => pawn.Position == dest, 1);

        ctx.AssertTrue("EXAMPLE-GOTO-Arrived", pawn.Position == dest,
            "推 " + used + " tick，停在 " + pawn.Position + "，目标 " + dest,
            RimRunnerExpectationSource.Literal("8 格内的可达格，步行不足 600 tick"));
        bool started = ctx.ObservedJobs().Any(o =>
            o.Subject == pawn.ThingID && o.DefName == JobDefOf.Goto.defName && o.Stage == "Started");
        ctx.AssertTrue("EXAMPLE-GOTO-Observed", started,
            "ObservedJobs 里 " + pawn.ThingID + " 的记录：" + string.Join("；", ctx.ObservedJobs().Where(o => o.Subject == pawn.ThingID).Select(o => o.Detail)),
            RimRunnerExpectationSource.Literal("玩家下令的 job 缺省进 ObservedJobs"));
    }
}
