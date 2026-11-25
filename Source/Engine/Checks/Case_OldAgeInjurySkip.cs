namespace RimRunner;

/// <summary>老年病跳过：跳过时 vanilla 生成期老年病与旧伤一条不长，隔离生成返回后即停止。</summary>
/// <remarks>只验无派系的 vanilla Colonist：GenerateIsolatedPawn 生成期间跳过生效，跳过时一条不长，嵌套时内层退出不关外层，GenerateIsolatedPawn 返回后不再跳过。</remarks>
[RimRunnerEnvCheck(RimRunnerFacility.OldAgeInjurySkip)]
public class Case_OldAgeInjurySkip : RimRunnerRegression
{
    private const string Source =
        "引擎承诺：经引擎生成的 pawn 不带生成期的老年病与旧伤；不成立时 pawn 带着背病、白内障、旧伤，"
        + "动作慢、做事半途而废，未通过的位置落在离根因很远的被测机制上";

    private const int SeedTries = 20;

    public override string Id => "AGEINJ1";

    public override RimRunnerLevel Level => RimRunnerLevel.World;

    [RimRunnerDirectGeneration("测的就是隔离生成入口的老年病跳过作用域")]
    public override void Run(RimRunnerRegressionContext ctx)
    {
        PawnKindDef kind = PawnKindDefOf.Colonist;
        int skippedBefore = RimRunnerOldAgeInjurySkip.SkippedCalls;
        int passedBefore = RimRunnerOldAgeInjurySkip.PassedCalls;
        Pawn pawn = ctx.TrackPawn(RimRunnerPawns.GenerateIsolatedPawn(kind, null));
        int skippedDuring = RimRunnerOldAgeInjurySkip.SkippedCalls - skippedBefore;
        int passedDuring = RimRunnerOldAgeInjurySkip.PassedCalls - passedBefore;
        if (skippedDuring + passedDuring == 0)
        {
            ctx.WarnNoneOffending("AGEINJ1-EntrySkipped", RimRunnerWarningKey.EnvCheckInconclusive,
                ["RimRunnerPawns.GenerateIsolatedPawn 生成期间一次都没走到老年病生成"],
                "无法判断入口是否进作用域",
                RimRunnerExpectationSource.Literal(Source));
        }
        else
        {
            ctx.AssertTrue("AGEINJ1-EntrySkipped", passedDuring == 0,
                "RimRunnerPawns.GenerateIsolatedPawn 生成期间走到老年病生成 " + (skippedDuring + passedDuring)
                + " 次，其中跳过 " + skippedDuring + " 次，仍生成 " + passedDuring + " 次（应全部跳过）",
                RimRunnerExpectationSource.Literal(Source));
        }
        ctx.AssertTrue("AGEINJ1-EntryClosed", !RimRunnerOldAgeInjurySkip.Active,
            "RimRunnerPawns.GenerateIsolatedPawn 返回后，跳过老年病的效果仍在：之后别处生成的 pawn 也不带老年病",
            RimRunnerExpectationSource.Literal(Source));

        float lifeExpectancy = pawn.RaceProps.lifeExpectancy;
        pawn.ageTracker.AgeBiologicalTicks = (long)(lifeExpectancy * 1.2f * GenDate.TicksPerYear);
        pawn.health.RemoveAllHediffs();
        ctx.WarnNoneOffending("AGEINJ1-Staged", RimRunnerWarningKey.EnvCheckInconclusive,
            kind.allowOldAgeInjuries && pawn.ageTracker.AgeBiologicalYears >= lifeExpectancy
                ? null
                : new[]
                {
                    "kind " + kind.defName + " 允许老年伤=" + kind.allowOldAgeInjuries
                    + "，年龄 " + pawn.ageTracker.AgeBiologicalYears + " / 寿命 " + lifeExpectancy
                },
            "场景未搭起，「没长出来」与「被挡住」无法区分",
            RimRunnerExpectationSource.Literal(Source));

        int seed = -1;
        int grown = 0;
        for (int candidate = 1; candidate <= SeedTries && seed < 0; candidate++)
        {
            pawn.health.RemoveAllHediffs();
            GenerateOldAge(pawn, candidate);
            grown = pawn.health.hediffSet.hediffs.Count;
            if (grown > 0) seed = candidate;
        }

        ctx.WarnNoneOffending("AGEINJ1-Sensitivity", RimRunnerWarningKey.EnvCheckInconclusive,
            seed > 0
                ? null
                : new[] { "不跳过时 " + SeedTries + " 个随机种子都没长出老年病或旧伤（kind " + kind.defName + "，种族 " + pawn.def.defName + "）" },
            "AGEINJ1-Skipped 在本名单下无法判断跳过是否生效",
            RimRunnerExpectationSource.Literal(Source));

        if (seed > 0)
        {
            pawn.health.RemoveAllHediffs();
            using (RimRunnerOldAgeInjurySkip.Enter())
            {
                GenerateOldAge(pawn, seed);
            }

            int skipped = pawn.health.hediffSet.hediffs.Count;
            ctx.AssertTrue("AGEINJ1-Skipped", skipped == 0,
                "同一随机种子 " + seed + "：不跳过时长出 " + grown + " 条老年病与旧伤，跳过时长出 " + skipped + " 条（应 0）",
                RimRunnerExpectationSource.Literal(Source));
            pawn.health.RemoveAllHediffs();
        }

        IDisposable outer = RimRunnerOldAgeInjurySkip.Enter();
        IDisposable inner = RimRunnerOldAgeInjurySkip.Enter();
        inner.Dispose();
        bool outerKept = RimRunnerOldAgeInjurySkip.Active;
        inner.Dispose();
        bool doubleDisposeKept = RimRunnerOldAgeInjurySkip.Active;
        outer.Dispose();
        bool closed = !RimRunnerOldAgeInjurySkip.Active;
        ctx.AssertTrue("AGEINJ1-Nested", outerKept && doubleDisposeKept && closed,
            "跳过效果嵌套两层：内层退出后仍生效=" + outerKept + "，内层重复退出后仍生效=" + doubleDisposeKept
            + "，外层退出后失效=" + closed + "（应全为 True）",
            RimRunnerExpectationSource.Literal(Source));
    }

    private static void GenerateOldAge(Pawn pawn, int seed)
    {
        Rand.PushState(seed);
        try
        {
            AgeInjuryUtility.GenerateRandomOldAgeInjuries(pawn, tryNotToKillPawn: true);
        }
        finally
        {
            Rand.PopState();
        }
    }
}
