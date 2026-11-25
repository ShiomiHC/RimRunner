using System.Runtime.CompilerServices;

namespace RimRunner;

/// <summary>禁直调的 vanilla 方法表：测试程序集直调即违例（警告 FORBIDDEN-CALLEE）。</summary>
/// <remarks>只扫引擎与测试程序集 IL 对 TradeDropSpot、QueueCastingJob 两参重载、LoadGame 两重载与 CheckVersionAndLoadGame 的调用；引擎正路入口除外。</remarks>
public class Case_ForbiddenCalleeGate : RimRunnerRegression
{
    public override string Id => "CALL1";
    public override RimRunnerLevel Level => RimRunnerLevel.Assembly;

    private const string QueueCastMessage =
        "未清除冷却与充能即排入施法 job：有界重试对同一能力连续施放时，冷却未清、后续尝试无法排入 job";

    private const string QueueCastReason = "引擎正路：排队前清冷却并补满充能";

    private const string LoadGameMessage =
        "载档就地销毁当前 Game 与地图，执行中的用例随即失去所在地图";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IntVec3 SentinelTradeDrop(Map map) =>
        DropCellFinder.TradeDropSpot(map);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IntVec3 ControlTradeDrop(Map map) =>
        RimRunnerPawns.AnchoredDropSpot(map);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SentinelQueueCastLocal(Ability ability, LocalTargetInfo target) =>
        ability.QueueCastingJob(target, LocalTargetInfo.Invalid);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ControlQueueCast(Ability ability, LocalTargetInfo target) =>
        RimRunnerAbilities.QueueCast(ability, target);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SentinelLoadGame(string saveName) =>
        GameDataSaveLoader.LoadGame(saveName);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ControlLoadGame(string saveName) =>
        RimRunnerGameComponent.QueueLoadGameAfterCurrentUpdate(saveName, _ => { });

    public override void Run(RimRunnerRegressionContext ctx)
    {
        Type self = typeof(Case_ForbiddenCalleeGate);
        MethodBase Probe(string name) => AccessTools.Method(self, name);
        MethodBase queueCast = AccessTools.Method(typeof(RimRunnerAbilities), nameof(RimRunnerAbilities.QueueCast));

        RimRunnerCallSiteGuard.RunWarned(ctx, "CALL1", RimRunnerScanSurface.Gauge, RimRunnerWarningKey.ForbiddenCallee,
            RimRunnerCallSiteRule.Forbid("TradeDrop",
                    RimRunnerCalleeMatch.Exact(AccessTools.Method(typeof(DropCellFinder),
                        nameof(DropCellFinder.TradeDropSpot), [typeof(Map)])),
                    "裸测试图无殖民地建筑时 TradeDropSpot 取全图随机格，落点取自 Rand、未经 Seeded 包裹",
                    sentinel: Probe(nameof(SentinelTradeDrop)), control: Probe(nameof(ControlTradeDrop))),
            RimRunnerCallSiteRule.Forbid("QueueCastLocal",
                    RimRunnerCalleeMatch.Exact(AccessTools.Method(typeof(Ability), nameof(Ability.QueueCastingJob),
                        [typeof(LocalTargetInfo), typeof(LocalTargetInfo)])),
                    QueueCastMessage,
                    sentinel: Probe(nameof(SentinelQueueCastLocal)), control: Probe(nameof(ControlQueueCast)))
                .AllowCaller(queueCast, QueueCastReason),
            RimRunnerCallSiteRule.Forbid("LoadGame",
                    RimRunnerCalleeMatch.Exact(
                        AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.LoadGame), [typeof(string)]),
                        AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.LoadGame), [typeof(System.IO.FileInfo)]),
                        AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.CheckVersionAndLoadGame), [typeof(string)])),
                    LoadGameMessage,
                    sentinel: Probe(nameof(SentinelLoadGame)), control: Probe(nameof(ControlLoadGame)))
                .AllowCaller(AccessTools.Method(typeof(RimRunnerGameComponent), nameof(RimRunnerGameComponent.QueueLoadGameAfterCurrentUpdate)),
                    "引擎正路：载档排在当前 update 之后的长事件里，届时已脱离当前帧执行栈")
                .AllowCaller(AccessTools.Method(typeof(Patch_UIRootEntry_RimRunnerAutoLoad), nameof(Patch_UIRootEntry_RimRunnerAutoLoad.Postfix)),
                    "启动期自动载档：主菜单刚初始化，栈上没有当前 Game 与地图，同 vanilla Root_Entry 的直调"));
    }
}
