namespace RimRunner;

/// <summary>被钉住的 pawn 不进随机精神态：社交斗殴与心情崩溃两条随机入口按 pawn 关掉。</summary>
/// <remarks>只验斗殴与心情崩溃三处入口：被钉住的 pawn 不开打不崩溃，未钉的 pawn 会开打、会崩溃，SuppressRandomMentalStates 后全部不出态；崩溃含三种形态：进入精神态、紧张性崩溃与出走。</remarks>
[RimRunnerEnvCheck(RimRunnerFacility.AgencyPin)]
public class Case_PinnedMentalStateGate : RimRunnerRegression
{
    private const string Source = "引擎承诺：被钉住的 pawn 不卷入社交斗殴（任一方被钉即不开打），也不随机心情崩溃；"
                                  + "不成立时被钉住的 pawn 会中途卷入斗殴或崩溃，用例的行为断言在离根因很远的位置未通过";

    private const string SuppressSource = "引擎承诺：调 ctx.SuppressRandomMentalStates 的用例里，任何 pawn 都不卷入社交斗殴、"
                                          + "不随机心情崩溃；不成立时未钉的 pawn 会崩溃";

    private const int MoodCheckCalls = 4000;
    private const int MoodCheckDelta = 150;

    public override string Id => "AGP1";
    public override RimRunnerLevel Level => RimRunnerLevel.World;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        bool originalAlwaysSocialFight = DebugSettings.alwaysSocialFight;
        DebugSettings.alwaysSocialFight = true;
        ctx.TrackRestore(() => DebugSettings.alwaysSocialFight = originalAlwaysSocialFight,
            "还原 DebugSettings.alwaysSocialFight");
        RimRunnerFault.Require(DebugSettings.enableRandomMentalStates,
            "全局随机精神态开关已关，未钉住的 pawn 也不触发精神态", "DebugSettings.enableRandomMentalStates");

        Pawn controlA = Spawn(ctx, "AGP1-CtlA", RimRunnerDisposableAgency.Free);
        Pawn controlB = Spawn(ctx, "AGP1-CtlB", RimRunnerDisposableAgency.Free);
        bool started = controlA.interactions.CheckSocialFightStart(InteractionDefOf.Insult, controlB);
        ctx.WarnNoneOffending("AGP1-SocialControl", RimRunnerWarningKey.EnvCheckInconclusive,
            started && IsSocialFighting(controlA) && IsSocialFighting(controlB)
                ? null
                : new[] { "双方都未钉住且未打起来：返回=" + started + " " + Describe(controlA) + " " + Describe(controlB) },
            "本名单的人形 pawn 无法进入社交斗殴，钉住挡斗殴的三段在本名单下无法判断",
            RimRunnerExpectationSource.Literal("强制斗殴掷骰必中时，未钉的两个人形 pawn 必然进入社交斗殴"));

        Pawn pinnedRecipient = Spawn(ctx, "AGP1-PinR", RimRunnerDisposableAgency.Pinned);
        Pawn freeInitiator = Spawn(ctx, "AGP1-FreeI", RimRunnerDisposableAgency.Free);
        bool recipientStarted = pinnedRecipient.interactions.CheckSocialFightStart(InteractionDefOf.Insult, freeInitiator);
        AssertNoFight(ctx, "AGP1-PinnedRecipient", recipientStarted, pinnedRecipient, freeInitiator);

        Pawn freeRecipient = Spawn(ctx, "AGP1-FreeR", RimRunnerDisposableAgency.Free);
        Pawn pinnedInitiator = Spawn(ctx, "AGP1-PinI", RimRunnerDisposableAgency.Pinned);
        bool initiatorStarted = freeRecipient.interactions.CheckSocialFightStart(InteractionDefOf.Insult, pinnedInitiator);
        AssertNoFight(ctx, "AGP1-PinnedInitiator", initiatorStarted, pinnedInitiator, freeRecipient);

        Pawn freeStarter = Spawn(ctx, "AGP1-FreeS", RimRunnerDisposableAgency.Free);
        Pawn pinnedTarget = Spawn(ctx, "AGP1-PinS", RimRunnerDisposableAgency.Pinned);
        freeStarter.interactions.StartSocialFight(pinnedTarget);
        AssertNoFight(ctx, "AGP1-DirectStart", false, pinnedTarget, freeStarter);

        Pawn moodControl = Spawn(ctx, "AGP1-MoodCtl", RimRunnerDisposableAgency.Free);
        Faction controlFaction = moodControl.Faction;
        int controlCalls = DriveMoodChecks(moodControl, controlFaction);
        ctx.WarnNoneOffending("AGP1-MoodControl", RimRunnerWarningKey.EnvCheckInconclusive,
            HasBroken(moodControl, controlFaction)
                ? null
                : new[]
                {
                    "未钉 pawn 心情置 0 后执行 " + controlCalls + " 次崩溃判定仍未崩溃："
                    + DescribeBreak(moodControl, controlFaction) + " 能随机崩溃=" + moodControl.mindState.mentalBreaker.CanDoRandomMentalBreaks
                    + " 崩溃被挡=" + moodControl.mindState.mentalBreaker.Blocked
                },
            "本名单的人形 pawn 心情无法崩溃，钉住挡崩溃的那一段在本名单下无法判断",
            RimRunnerExpectationSource.Literal("心情 0 的未钉人形 pawn 在 " + MoodCheckCalls + " 次判定内必崩溃（精神态 / 紧张性崩溃 / 出走）"));
        RimRunnerPawns.Cleanup(moodControl);

        Pawn moodPinned = Spawn(ctx, "AGP1-MoodPin", RimRunnerDisposableAgency.Pinned);
        Faction pinnedFaction = moodPinned.Faction;
        int pinnedCalls = DriveMoodChecks(moodPinned, pinnedFaction);
        ctx.AssertTrue("AGP1-MoodPinned", !HasBroken(moodPinned, pinnedFaction),
            "被钉住的 pawn 在 " + pinnedCalls + " 次心情判定后已崩溃：" + DescribeBreak(moodPinned, pinnedFaction),
            RimRunnerExpectationSource.Literal(Source));

        ctx.SuppressRandomMentalStates();
        ctx.AssertTrue("AGP1-SuppressKeepsVanillaSwitch", DebugSettings.enableRandomMentalStates,
            "ctx.SuppressRandomMentalStates 已关闭 vanilla 开关 DebugSettings.enableRandomMentalStates", RimRunnerExpectationSource.Literal(SuppressSource));

        Pawn suppressedA = Spawn(ctx, "AGP1-SupA", RimRunnerDisposableAgency.Free);
        Pawn suppressedB = Spawn(ctx, "AGP1-SupB", RimRunnerDisposableAgency.Free);
        bool suppressedStarted = suppressedA.interactions.CheckSocialFightStart(InteractionDefOf.Insult, suppressedB);
        AssertNoSuppressedFight(ctx, "AGP1-SuppressSocial", suppressedStarted, suppressedA, suppressedB);

        Pawn suppressedStarter = Spawn(ctx, "AGP1-SupS", RimRunnerDisposableAgency.Free);
        Pawn suppressedTarget = Spawn(ctx, "AGP1-SupT", RimRunnerDisposableAgency.Free);
        suppressedStarter.interactions.StartSocialFight(suppressedTarget);
        AssertNoSuppressedFight(ctx, "AGP1-SuppressDirectStart", false, suppressedStarter, suppressedTarget);

        Pawn suppressedMood = Spawn(ctx, "AGP1-SupMood", RimRunnerDisposableAgency.Free);
        Faction suppressedFaction = suppressedMood.Faction;
        int suppressedCalls = DriveMoodChecks(suppressedMood, suppressedFaction);
        ctx.AssertTrue("AGP1-SuppressMood", !HasBroken(suppressedMood, suppressedFaction),
            "ctx.SuppressRandomMentalStates 生效时，未钉 pawn 在 " + suppressedCalls + " 次心情判定后已崩溃：" + DescribeBreak(suppressedMood, suppressedFaction),
            RimRunnerExpectationSource.Literal(SuppressSource));
    }

    private static void AssertNoSuppressedFight(RimRunnerRegressionContext ctx, string label, bool started, Pawn a, Pawn b)
    {
        ctx.AssertTrue(label, !started && !a.InMentalState && !b.InMentalState,
            "ctx.SuppressRandomMentalStates 生效时，未钉 pawn 仍发生社交斗殴：返回=" + started + " " + Describe(a) + " " + Describe(b),
            RimRunnerExpectationSource.Literal(SuppressSource));
    }

    private static Pawn Spawn(RimRunnerRegressionContext ctx, string nick, RimRunnerDisposableAgency agency)
    {
        Pawn pawn = RimRunnerPawnGen.Subject(ctx, RimRunnerSubjectSpec.DisposableHumanlike(agency), nick);
        RimRunnerFault.Require(pawn.needs?.mood != null && pawn.interactions != null,
            "一次性人形 pawn 没有心情需求或互动 tracker：随机斗殴与随机心情崩溃两条入口在 vanilla 侧即提前返回", pawn.LabelShort);
        return pawn;
    }

    private static int DriveMoodChecks(Pawn pawn, Faction originalFaction)
    {
        pawn.needs.mood.CurLevel = 0f;
        int calls = 0;
        while (calls < MoodCheckCalls && !HasBroken(pawn, originalFaction))
        {
            pawn.mindState.mentalBreaker.MentalBreakerTickInterval(MoodCheckDelta);
            calls++;
        }

        return calls;
    }

    private static void AssertNoFight(RimRunnerRegressionContext ctx, string label, bool started, Pawn pinned, Pawn other)
    {
        ctx.AssertTrue(label, !started && !pinned.InMentalState && !other.InMentalState,
            "被钉住的 pawn 卷入社交斗殴：返回=" + started + " 钉=" + Describe(pinned) + " 对侧=" + Describe(other),
            RimRunnerExpectationSource.Literal(Source));
    }

    private static bool HasBroken(Pawn pawn, Faction originalFaction)
    {
        return pawn.InMentalState
               || pawn.health.hediffSet.HasHediff(HediffDefOf.CatatonicBreakdown)
               || pawn.Faction != originalFaction;
    }

    private static string DescribeBreak(Pawn pawn, Faction originalFaction)
    {
        return Describe(pawn) + " 紧张性崩溃=" + pawn.health.hediffSet.HasHediff(HediffDefOf.CatatonicBreakdown)
               + " 派系=" + (pawn.Faction?.Name ?? "无") + "（原 " + (originalFaction?.Name ?? "无") + "）";
    }

    private static bool IsSocialFighting(Pawn pawn)
    {
        return pawn.MentalStateDef == MentalStateDefOf.SocialFighting;
    }

    private static string Describe(Pawn pawn)
    {
        return pawn.LabelShort + "[" + (pawn.MentalStateDef?.defName ?? "无精神态") + "]";
    }
}
