using System.Runtime.CompilerServices;
using RimWorld.Planet;

namespace RimRunner;

/// <summary>缺省独立小图的现场：主图冻结、交付粒度、当前地图探针与家园挑图。</summary>
/// <remarks>只验缺省上独立小图的 World 级现场：主图零推进、小图按 15 tick 一批，冻结图落物探针记一行，当前地图探针记读点、另记未观测段，Find.AnyPlayerHomeMap 一族只挑本用例领到的小图，主图 pawn 不收新记忆。</remarks>
[RimRunnerEnvCheck(RimRunnerFacility.IsolatedMap)]
[RimRunnerAcknowledge(RimRunnerWarningKey.TickDeliveryGap, "ISOMAP2-MainFrozen 断言的就是冻结主图上的 pawn 整窗不经 DoTick")]
public class Case_IsolatedMapInSitu : RimRunnerRegression
{
    private const string Source = "引擎承诺：使用独立小图的用例在自己领的小图上执行，主图冻结、主图上的 pawn 不推进；"
                                  + "小图按 vanilla 非当前地图的节奏推进（每 15 tick 一批）。不成立时小图用例的推进量与主图状态都不可信";

    private const int Ticks = 600;

    public override string Id => "ISOMAP2";
    public override RimRunnerLevel Level => RimRunnerLevel.World;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        Map isolated = ctx.Map!;
        Map? main = RimRunnerIsolatedMap.Frozen;
        ctx.AssertTrue("ISOMAP2-OnIsolated",
            isolated == RimRunnerIsolatedMap.Active && !isolated.IsPocketMap && isolated.IsPlayerHome
            && main != null && main != isolated,
            "ctx.Map=" + isolated + "，引擎记的当前小图=" + RimRunnerIsolatedMap.Active + "，冻结的主图=" + main
            + "，口袋图=" + isolated.IsPocketMap + "，家园=" + isolated.IsPlayerHome
            + "（ctx.Map 应为领到的小图，为家园而非口袋图，且不是主图）",
            RimRunnerExpectationSource.Literal(Source));
        if (main == null || isolated != RimRunnerIsolatedMap.Active)
        {
            return;
        }

        Map extra = ctx.AcquireIsolatedMap();
        ctx.AssertTrue("ISOMAP2-Acquired",
            extra != isolated && extra != main && extra.IsPlayerHome && !RimRunnerIsolatedMap.IsFrozenMap(extra)
            && ctx.Map == isolated && RimRunnerIsolatedMap.LeasedMaps.SequenceEqual([isolated, extra]),
            "ctx.AcquireIsolatedMap 再领到 " + extra + "，ctx.Map=" + ctx.Map + "，本用例领到的小图："
            + string.Join("、", RimRunnerIsolatedMap.LeasedMaps.Select(map => map.ToString()))
            + "（应是另一张不冻结的小图，ctx.Map 不变）",
            RimRunnerExpectationSource.Literal(Source));

        ctx.Map = main;
        Pawn frozenPawn;
        try
        {
            frozenPawn = RimRunnerPawnGen.Subject(ctx,
                RimRunnerSubjectSpec.DisposableHumanlike(RimRunnerDisposableAgency.Pinned), "ISOMAP2-Main");
        }
        finally
        {
            ctx.Map = isolated;
        }

        int spawnNoted = RimRunnerFrozenMapSpawnProbe.Consume(Id, frozenPawn.def.defName);
        ctx.AssertTrue("ISOMAP2-SpawnProbeHit", spawnNoted == 1,
            "往冻结的主图上落一个 " + frozenPawn.def.defName + "，报告已记 " + spawnNoted + " 行（应 1）",
            RimRunnerExpectationSource.Literal(SpawnProbeSource));

        RunHomePickers(ctx, main);

        Pawn livePawn = RimRunnerPawnGen.Subject(ctx,
            RimRunnerSubjectSpec.DisposableHumanlike(RimRunnerDisposableAgency.Pinned), "ISOMAP2-Isolated");

        ThoughtDef memory = DefDatabase<ThoughtDef>.GetNamed("DebugGood");
        int frozenBefore = frozenPawn.needs.mood.thoughts.memories.NumMemoriesOfDef(memory);
        int liveBefore = livePawn.needs.mood.thoughts.memories.NumMemoriesOfDef(memory);
        frozenPawn.needs.mood.thoughts.memories.TryGainMemory(memory);
        livePawn.needs.mood.thoughts.memories.TryGainMemory(memory);
        int frozenGained = frozenPawn.needs.mood.thoughts.memories.NumMemoriesOfDef(memory) - frozenBefore;
        int liveGained = livePawn.needs.mood.thoughts.memories.NumMemoriesOfDef(memory) - liveBefore;
        ctx.AssertTrue("ISOMAP2-MainNoMemory", frozenGained == 0 && liveGained == 1,
            "给两个 pawn 各记一条 " + memory.defName + "：主图上的新增 " + frozenGained + "（应 0），小图上的新增 " + liveGained + "（应 1）",
            RimRunnerExpectationSource.Literal("引擎承诺：冻结的主图上的 pawn 不收新记忆，小图上的 pawn 仍收"));

        RimRunnerTickDelivery.Arm(frozenPawn);
        RimRunnerTickDelivery.Arm(livePawn);
        RimRunnerTimeDriver.AdvanceBurst(Ticks);
        RimRunnerTickDelivery.Ledger frozenLedger = RimRunnerTickDelivery.Disarm(frozenPawn);
        RimRunnerTickDelivery.Ledger liveLedger = RimRunnerTickDelivery.Disarm(livePawn);

        ctx.AssertTrue("ISOMAP2-MainFrozen", frozenLedger.Delivered == 0 && frozenLedger.Chunks == 0,
            "推 " + Ticks + " tick 后主图上的 pawn 被推进 " + frozenLedger + "（应为零）", RimRunnerExpectationSource.Literal(Source));
        ctx.AssertTrue("ISOMAP2-IsolatedTicks",
            Math.Abs(liveLedger.Delivered - Ticks) <= RimRunnerTickDelivery.VanillaMaxChunkTicks,
            "推 " + Ticks + " tick 后小图上的 pawn 被推进 " + liveLedger + "（应约 " + Ticks + "）", RimRunnerExpectationSource.Literal(Source));
        ctx.AssertTrue("ISOMAP2-IsolatedChunk15",
            liveLedger.Chunks <= Ticks / RimRunnerTickDelivery.VanillaMaxChunkTicks + 1,
            "推 " + Ticks + " tick 后小图上的 pawn 被推进 " + liveLedger + "（应每批 " + RimRunnerTickDelivery.VanillaMaxChunkTicks + " tick）",
            RimRunnerExpectationSource.Literal(Source));

        RunCurrentMapProbe(ctx, isolated);
    }

    private void RunHomePickers(RimRunnerRegressionContext ctx, Map main)
    {
        ctx.AcquireIsolatedMap();
        Map spare = RimRunnerIsolatedMap.ReturnLastLeaseForSelfTest();
        List<Map> mine = RimRunnerIsolatedMap.LeasedMaps;
        int idle = RimRunnerIsolatedMap.PooledCount - mine.Count;
        Map any = Find.AnyPlayerHomeMap;
        List<Map> homes = Current.Game.PlayerHomeMaps.ToList();
        List<Map> randomPicks = Enumerable.Range(0, 20).Select(_ => Find.RandomPlayerHomeMap).ToList();
        ctx.AssertTrue("ISOMAP2-HomePickers",
            idle > 0 && spare.IsPlayerHome
            && mine.Contains(any) && homes.Count == mine.Count && homes.All(mine.Contains) && randomPicks.All(mine.Contains)
            && main.IsPlayerHome,
            "本用例领到 " + string.Join("、", mine.Select(map => map.ToString())) + "，池里另有 " + idle + " 张空闲（含刚交回的 " + spare
            + "，挑图之外是家园=" + spare.IsPlayerHome + "）；"
            + "Find.AnyPlayerHomeMap=" + (any?.ToString() ?? "null") + "，PlayerHomeMaps=" + string.Join("、", homes.Select(map => map.ToString()))
            + "，RandomPlayerHomeMap 20 次挑到 " + string.Join("、", randomPicks.Distinct().Select(map => map?.ToString() ?? "null"))
            + "，挑图之外主图是家园=" + main.IsPlayerHome
            + "（应只挑到本用例领到的小图，挑图之外主图与空闲的图仍为家园）",
            RimRunnerExpectationSource.Literal(HomePickerSource));
    }

    private const string HomePickerSource = "引擎承诺：小图用例体内 vanilla 挑家园图的入口（Find.AnyPlayerHomeMap 一族、任务挑图）只挑本用例领到的小图";

    private const string SpawnProbeSource = "引擎承诺：往冻结的主图上落物时报告记一行（落在那里的东西不推进，记这一行供核对用例是否落错图）";

    private void RunCurrentMapProbe(RimRunnerRegressionContext ctx, Map isolated)
    {
        Designator designator = new Designator_AreaBuildRoof();
        using (ctx.AsCurrentMap(isolated))
        {
            _ = designator.Map;
        }

        List<string> borrowed = RimRunnerCurrentMapProbe.Consume(Id, "Designator.get_Map");
        _ = designator.Map;
        List<string> bare = RimRunnerCurrentMapProbe.Consume(Id, "Designator.get_Map");

        using (ctx.AsCurrentMap(isolated))
        {
            _ = GenView.RandomPositionOnOrNearScreen();
        }

        List<string> viewBorrowed = RimRunnerCurrentMapProbe.Consume(Id, nameof(GenView.RandomPositionOnOrNearScreen));
        _ = GenView.RandomPositionOnOrNearScreen();
        List<string> viewBare = RimRunnerCurrentMapProbe.Consume(Id, nameof(GenView.RandomPositionOnOrNearScreen));
        _ = ThinRead();
        List<string> thin = RimRunnerCurrentMapProbe.Consume(Id, nameof(ThinRead));
        List<string> view = viewBare.Where(line => line.StartsWith(RimRunnerOrigin.Lead(RimRunnerOrigin.Vanilla) + RimRunnerCurrentMapProbe.ViewGetter, StringComparison.Ordinal)
                                                  && line.Contains(nameof(GenView.RandomPositionOnOrNearScreen), StringComparison.Ordinal)).ToList();
        ctx.AssertTrue("ISOMAP2-ProbeBorrowedSilent", borrowed.Count == 0,
            "ctx.AsCurrentMap 作用域内读当前地图，报告已记 " + borrowed.Count + " 行（应 0）", RimRunnerExpectationSource.Literal(ProbeSource));
        ctx.AssertTrue("ISOMAP2-ProbeHit", bare.Count == 1,
            "作用域外经 Designator.Map 读当前地图，报告已记 " + bare.Count + " 行（应 1）",
            RimRunnerExpectationSource.Literal(ProbeSource));
        ctx.AssertTrue("ISOMAP2-ProbeOriginVanilla",
            bare.Count == 1 && bare[0].StartsWith(RimRunnerOrigin.Lead(RimRunnerOrigin.Vanilla) + "Designator.get_Map", StringComparison.Ordinal),
            "读者为 vanilla 的行未标 [vanilla]：" + string.Join(" / ", bare),
            RimRunnerExpectationSource.Literal(ProbeSource));
        ctx.AssertTrue("ISOMAP2-ProbeViewBorrowedSilent", viewBorrowed.Count == 0,
            "ctx.AsCurrentMap 作用域内经 GenView 读视野矩形与当前地图，报告已记 " + viewBorrowed.Count + " 行（应 0）",
            RimRunnerExpectationSource.Literal(ProbeSource));
        ctx.AssertTrue("ISOMAP2-ProbeViewHit",
            view.Count == 1 && view[0].Contains(RimRunnerCurrentMapProbe.ViewTail, StringComparison.Ordinal),
            "作用域外经 GenView 读视野矩形，标 " + RimRunnerCurrentMapProbe.ViewGetter + "、带「" + RimRunnerCurrentMapProbe.ViewTail
            + "」的行有 " + view.Count + " 行（应 1）：" + string.Join(" / ", viewBare),
            RimRunnerExpectationSource.Literal(ProbeSource));
        ctx.AssertTrue("ISOMAP2-ProbeTailCallKept",
            thin.Count == 1 && thin[0].StartsWith(RimRunnerOrigin.Lead(RimRunnerOrigin.Case) + nameof(Case_IsolatedMapInSitu) + "." + nameof(ThinRead), StringComparison.Ordinal),
            "作用域外经一个以读当前地图收尾、按优化档编译的方法读当前地图，读者是该方法的行有 " + thin.Count + " 行（应 1）："
            + string.Join(" / ", thin),
            RimRunnerExpectationSource.Literal(ProbeSource));

        RimRunnerTimeDriver.AdvanceBurst(1, () =>
        {
            _ = ThinRead();
            return false;
        });
        List<string> burstCallback = RimRunnerCurrentMapProbe.Consume(Id, nameof(ThinRead));
        ctx.AssertTrue("ISOMAP2-ProbeBurstCallback",
            burstCallback.Count == 1 && burstCallback[0].StartsWith(RimRunnerOrigin.Lead(RimRunnerOrigin.Case) + nameof(Case_IsolatedMapInSitu) + "." + nameof(ThinRead), StringComparison.Ordinal),
            "AdvanceBurst 回调内读当前地图，读者是 ThinRead 的行有 " + burstCallback.Count + " 行（应 1）："
            + string.Join(" / ", burstCallback),
            RimRunnerExpectationSource.Literal(ProbeSource));

        RunBlindProbe(ctx);
    }

    private void RunBlindProbe(RimRunnerRegressionContext ctx)
    {
        Designator designator = new Designator_AreaBuildRoof();
        RimRunnerCurrentMapProbe.ConsumeBlind(Id, RimRunnerCurrentMapProbe.BlindReason.Generating);
        RimRunnerIsolatedMap.RunAsGeneratingForSelfTest(() =>
        {
            _ = designator.Map;
            _ = designator.Map;
        });
        List<string> genRead = RimRunnerCurrentMapProbe.Consume(Id, "Designator.get_Map");
        List<(string Text, int Count)> genBlind =
            RimRunnerCurrentMapProbe.ConsumeBlind(Id, RimRunnerCurrentMapProbe.BlindReason.Generating);
        ctx.AssertTrue("ISOMAP2-ProbeBlindGen",
            genRead.Count == 0 && genBlind.Count == 1 && genBlind[0].Count == 2,
            "生图窗口里作用域外读两次当前地图：读点 " + genRead.Count + " 行（应 0），未观测 " + genBlind.Count
            + " 行（应 1，次数 2）：" + string.Join(" / ", genBlind.Select(entry => entry.Text + " ×" + entry.Count)),
            RimRunnerExpectationSource.Literal(BlindSource));

        for (int i = 0; i < BlindReads; i++)
        {
            _ = designator.Map;
        }

        RimRunnerCurrentMapProbe.Consume(Id, "Designator.get_Map");
        List<(string Text, int Count)> sameReaderBlind =
            RimRunnerCurrentMapProbe.ConsumeBlind(Id, RimRunnerCurrentMapProbe.BlindReason.CaptureLimit);
        ctx.AssertTrue("ISOMAP2-ProbeSameReaderNoBlind", sameReaderBlind.Count == 0,
            "同一读者连读 " + BlindReads + " 次不应触顶：" + string.Join(" / ", sameReaderBlind.Select(entry => entry.Text)),
            RimRunnerExpectationSource.Literal(BlindSource));

        RimRunnerCurrentMapProbe.ClearSeenReadersForSelfTest();
        int previousLimit = RimRunnerCurrentMapProbe.SwapCaptureLimitForSelfTest(1);
        try
        {
            _ = ThinRead();
            _ = designator.Map;
        }
        finally
        {
            RimRunnerCurrentMapProbe.SwapCaptureLimitForSelfTest(previousLimit);
            RimRunnerCurrentMapProbe.ClearSeenReadersForSelfTest();
        }

        RimRunnerCurrentMapProbe.Consume(Id, nameof(ThinRead));
        RimRunnerCurrentMapProbe.Consume(Id, "Designator.get_Map");
        List<(string Text, int Count)> capBlind =
            RimRunnerCurrentMapProbe.ConsumeBlind(Id, RimRunnerCurrentMapProbe.BlindReason.CaptureLimit);
        ctx.AssertTrue("ISOMAP2-ProbeBlindCap", capBlind.Count == 1,
            "不同读者帧超过配额，触顶后记下的未观测 " + capBlind.Count + " 行（应 1）："
            + string.Join(" / ", capBlind.Select(entry => entry.Text)),
            RimRunnerExpectationSource.Literal(BlindSource));
    }

    private const int BlindReads = RimRunnerCurrentMapProbe.CaptureLimitPerCase + 1;

    private const string BlindSource = "引擎承诺：当前地图探针截栈配额按不同读者帧计（同读者重复读与不记的读不耗配额）；"
                                       + "未观测的段（生成独立小图期间、截栈达上限之后）另起一键记一行并带次数，不混进读点；"
                                       + "报告里两键皆无才等于这条用例未读当前地图";

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Map ThinRead() => Find.CurrentMap;

    private const string ProbeSource = "引擎承诺：小图用例经 vanilla 读当前地图或视野矩形时（读到的取自主图）报告记一行并标读者归属；"
                                       + "ctx.AsCurrentMap 作用域内的读不记";
}
