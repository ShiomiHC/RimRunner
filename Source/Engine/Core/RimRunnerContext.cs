using System.Globalization;

namespace RimRunner;

/// <summary>用例上下文的共用部分：登记与清理、观测、诊断都在这里。</summary>
public partial class RimRunnerContext
{
    internal RimRunnerContext()
    {
    }

    /// <summary>当前测试地图；Level 装配到地图之后才有值，之前为 null。</summary>
    /// <remarks>在独立小图上执行时主图冻结。缺省小图是玩家殖民地（不刷野生动物、天气恒晴）；场景装配的对象留在主图上。</remarks>
    public Map? Map;
    /// <summary>本条用例的报告 entry。</summary>
    public RimRunnerResultEntry? Current;

    private protected RimRunnerResultEntry InCase => Current!;

    internal RimRunnerShardRun? ShardRun;
    internal RimRunnerTestCase? ShardableCase;
    internal Func<RimRunnerShardEnumeration.Outcome>? WholeCellsEnumerationForTest;
    private List<RimRunnerShardCell>? wholeCells;
    private int wholeCursor;

    /// <summary>取下一格：分片时从组队列认领，否则按 ShardCells 的顺序给出全集，取完返回 false。</summary>
    /// <remarks>开始那一格时才调，格执行完调 cell.Complete()；分片时取本片认领的部分。非分片用例调即抛；ShardCells 里 Require 不成立归 SENTINEL_FAULT，其它异常与非法格归 UNCLAIMED_FAULT。</remarks>
    /// <param name="cell">认领到的格；取完时为 null。</param>
    public bool TryTakeShardCell([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RimRunnerShardLease? cell)
    {
        if (!TryTakeShardKey(out cell)) return false;
        InCase.shardLeases.Add(cell);
        return true;
    }

    private bool TryTakeShardKey([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RimRunnerShardLease? cell)
    {
        if (ShardRun != null) return ShardRun.TryTake(out cell);
        if (WholeCellsEnumerationForTest == null && ShardableCase is not IRimRunnerShardable)
            throw new InvalidOperationException(RimRunnerLog.Prefix
                                                + " TryTakeShardCell 只给实现 IRimRunnerShardable 的用例在用例体内调");
        if (wholeCells == null)
        {
            RimRunnerShardEnumeration.Outcome outcome = WholeCellsEnumerationForTest != null
                ? WholeCellsEnumerationForTest()
                : RimRunnerShardEnumeration.Enumerate(ShardableCase!);
            foreach (KeyValuePair<string, string> warning in outcome.Warnings)
                RimRunnerSession.AddRuntimeWarning(InCase, warning.Key, warning.Value);
            if (outcome.Cells == null)
            {
                string fault = outcome.Fault!;
                RimRunnerFault.Require(!outcome.Claimed, fault);
                throw new InvalidOperationException(RimRunnerLog.Prefix + " " + fault, outcome.Exception);
            }

            wholeCells = outcome.Cells;
            InCase.wholeCellsEnumerated = true;
            PublishUntakenWholeCells();
        }

        if (wholeCursor >= wholeCells.Count)
        {
            cell = null;
            return false;
        }

        cell = new RimRunnerShardLease(wholeCells[wholeCursor++].Key, -1);
        PublishUntakenWholeCells();
        return true;
    }

    private void PublishUntakenWholeCells()
    {
        if (wholeCells == null) return;
        InCase.untakenWholeCells.Clear();
        for (int i = wholeCursor; i < wholeCells.Count; i++)
            InCase.untakenWholeCells.Add(wholeCells[i].Key);
    }

    /// <summary>本组的计划片数：分片时是被分片那条用例所在组的片数，不分片时与其余用例相同，为 1。</summary>
    /// <remarks>是开始执行前定的数；实际正在执行的片数不超过它。</remarks>
    public int ShardCount => ShardRun?.Request!.Count ?? 1;

    internal static RimRunnerContext? Active { get; private set; }
    internal bool TearingDown { get; private set; }
    private readonly Dictionary<string, Pawn> pawns = [];
    private readonly Dictionary<string, Thing> things = [];
    private Dictionary<string, string>? checkpointState;
    private RimRunnerCleanupScope? cleanupScope;

    internal void SetCheckpointState(Dictionary<string, string>? state)
    {
        checkpointState = state;
    }

    internal const string PhaseEndTickStateKey = "__phaseEndTick";

    internal const string PhaseEndMapTickStateKey = "__phaseEndMapTick";

    /// <summary>距上一 phase 结束 ctx.Map 实推的拍数，独立小图上恒为 1。</summary>
    /// <remarks>ctx.Map 为 null 时取 TicksGame 之差；phase 0 读即抛。按 TicksGame 判到期的状态读 TicksGame，预算写 CheckpointClockBudget。</remarks>
    public int TicksSincePreviousPhase
    {
        get
        {
            if (Map != null && TryReadCheckpointState(PhaseEndMapTickStateKey, out string? mapRaw))
            {
                return RimRunnerMapTicks.Of(Map) - int.Parse(mapRaw, CultureInfo.InvariantCulture);
            }

            if (!TryReadCheckpointState(PhaseEndTickStateKey, out string? raw))
            {
                throw new InvalidOperationException(
                    RimRunnerLog.Prefix + " 当前 phase 无上一 phase 的终点戳；"
                                        + "phase 0 不存在「上一 phase」，无法读取 TicksSincePreviousPhase");
            }

            return (Find.TickManager?.TicksGame ?? 0)
                   - int.Parse(raw, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>往 checkpoint 写一个键值，跨存读档带到下一 phase。</summary>
    public void WriteCheckpointState(string key, string value)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("checkpoint key 不能为空", nameof(key));
        }

        if (key.StartsWith("__", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "checkpoint key 的 __ 前缀是框架保留段：" + key, nameof(key));
        }

        RequireCheckpointState()[key] = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>读上一 phase 写的 checkpoint 值，缺键即抛。</summary>
    public string ReadCheckpointState(string key)
    {
        if (!RequireCheckpointState().TryGetValue(key, out string? value))
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " checkpoint 状态缺少键：" + key);
        }

        return value;
    }

    /// <summary>读上一 phase 写的 checkpoint 值，缺键返 false。</summary>
    public bool TryReadCheckpointState(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        return RequireCheckpointState().TryGetValue(key, out value);
    }

    private Dictionary<string, string> RequireCheckpointState()
    {
        if (checkpointState == null)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 当前 session 未提供 checkpoint 状态包");
        }

        return checkpointState;
    }

    /// <summary>作用域内解除正午天光，天色按 vanilla 钟点走；作用域内交的截图照真实钟点出天色。</summary>
    /// <remarks>缺省：用例期内各图的 SkyManager.CurSkyGlow 恒为 1.0，含用例期新建的图。</remarks>
    public IDisposable AllowNightSky() => RimRunnerDaylightPin.Suspend();

    /// <summary>用例期内把 tick 分档率钉在 rate，子弹一并钉住，收尾自动卸。</summary>
    /// <remarks>作用于全部地图上按相机判档的物，子弹钉 1–5 取 1、钉 15 取 15，自带固定档的物不变；小图上不钉时 vanilla 判 15。只在用例体内可调。已钉住时、推 tick 期间调用，或 rate 既不是 1–5 也不是 15，即抛。</remarks>
    /// <param name="rate">分档率：1–5 是视野内（玩家正看着），15 是离屏（玩家未观看）。</param>
    public RimRunnerTickRatePin PinTickRate(int rate)
    {
        RequireBody("PinTickRate");
        RejectInSceneUnwoundAtCaseEnd("PinTickRate");

        return RimRunnerTickRatePin.Pin(rate);
    }

    private void RejectInSceneUnwoundAtCaseEnd(string entryPoint)
    {
        if (Phase == RimRunnerPhase.Scene)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 场面态不支持 " + entryPoint);
        }
    }

    private Map? currentMapAtStart;
    private bool mentalSwitchAtStart;

    internal const string MentalSwitchLeakReason = "收尾后 DebugSettings.enableRandomMentalStates 未还原";

    /// <summary>作用域内把 map 设为当前地图，期间推 tick 即抛；不可嵌套。</summary>
    /// <remarks>切换与玩家切图同形（图已是当前地图时不切）：出作用域切回原地图并清掉作用域里的选中；到用例收尾未收即切回并记清理错误。</remarks>
    public IDisposable AsCurrentMap(Map map) => RimRunnerCurrentMap.Lend(map);

    /// <summary>map 此刻是否为当前地图。</summary>
    public bool IsCurrentMap(Map map) => map != null && RimRunnerCurrentMapProbe.EngineCurrentMap() == map;

    /// <summary>把当前地图设回开始执行时那张（在独立小图上执行即冻结的主图，场面即立场时那张）。</summary>
    public void ResetCurrentMap()
    {
        if (currentMapAtStart == null || !Find.Maps.Contains(currentMapAtStart))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " ResetCurrentMap：开始执行时的当前地图（" + (currentMapAtStart?.ToString() ?? "无") + "）已不在本局");
        }

        if (RimRunnerCurrentMapProbe.EngineCurrentMap() != currentMapAtStart)
        {
            Verse.Current.Game.CurrentMap = currentMapAtStart;
        }
    }

    private RimRunnerMapRestore? mainMapRestore;
    private bool mainMapRestoreAtLastPhase;
    private RimRunnerMapRestore? batchMainMapRestore;

    internal bool MainMapUnrestorable { get; private set; }

    internal bool MainMapRestoreClosed { get; private set; }

    internal void ArmMainMapRestore(RimRunnerMapRestore restore, bool lastPhase)
    {
        mainMapRestore = restore;
        mainMapRestoreAtLastPhase = lastPhase;
    }

    internal static int SegmentSerial { get; private set; }

    internal void BeginCase(RimRunnerResultEntry entry, bool observesRuntime)
    {
        SegmentSerial++;
        Current = entry;
        Active = this;
        currentMapAtStart = RimRunnerCurrentMapProbe.EngineCurrentMap();
        mentalSwitchAtStart = DebugSettings.enableRandomMentalStates;
        RimRunnerLiveServer.BeginCasePulse(entry.id, entry.samples);
        RimRunnerExpectationLedger.BeginScope(entry);
        cleanupScope = new RimRunnerCleanupScope();
        AdoptCarriedCleanups();
        fixtureSnapshot = RimRunnerFixtureState.Capture();
        RimRunnerRetentionProbe.BeginCase();
        RimRunnerHediffBreakBlock.BeginCase();
        RimRunnerNeedsFreeze.BeginSegment(entry.id, ContinuesPreviousPhase);
        RimRunnerIlScan.BeginCase();
        RimRunnerDummyDefAudit.BeginCase();
        RimRunnerObservationBuffer.BeginCase(entry.id, pawns.Values, observesRuntime);
        ReplayCarriedAllJobs();
        AdoptCarriedSpecTargets();
        ReplayCarriedExcusedDeaths();
        RegisterCarriedGeneratedMaps();
        RimRunnerWindowWatch.BeginCase(entry.id);
        RimRunnerDaylightPin.BeginCase();
    }

    internal void EndCase()
    {
        RimRunnerResultEntry current = InCase;
        TearingDown = true;
        try
        {
            AppendRuntimeObservationEvidence();
            AppendLiveFingerprints();
            AppendBlockedBreakDiagnostic();
        }
        finally
        {
            try
            {
                if (cleanupScope != null)
                {
                    TakeHandedOffCleanups();
                    TakeHandedOffSpecTargets();
                    current.cleanupErrors.AddRange(cleanupScope.Cleanup());
                    current.endedByClaimedFault |= cleanupScope.EndedByClaimedFault;
                }

                RunScenarioRestores(current, TakeScenarioRestores());
                if (mainMapRestore != null && (mainMapRestoreAtLastPhase || current.endedByClaimedFault)
                                           && mainMapRestore.Members.Count <= 1)
                {
                    RunScenarioRestores(current, mainMapRestore.ScenarioRestores);
                }

                ReleaseHandoffIfClaimed();
                string? unclosedBorrow = RimRunnerCurrentMap.EndCase();
                if (unclosedBorrow != null)
                {
                    current.cleanupErrors.Add(unclosedBorrow);
                }

                ReleaseFixtureState();

                if (DebugSettings.enableRandomMentalStates != mentalSwitchAtStart)
                {
                    bool leaked = DebugSettings.enableRandomMentalStates;
                    DebugSettings.enableRandomMentalStates = mentalSwitchAtStart;
                    RimRunnerSession.AddRuntimeWarning(current, RimRunnerWarningKey.MentalSwitchWrite,
                        MentalSwitchLeakReason + "（" + mentalSwitchAtStart + " → " + leaked
                        + "），引擎已复位");
                }

                RimRunnerRetentionProbe.EndCase(this);
                try
                {
                    AppendResidueFingerprints();
                }
                catch (Exception exception)
                {
                    current.cleanupErrors.Add("收尾残留判定失败：" + exception);
                }

                AppendWindowFingerprints();
            }
            finally
            {
                if (mainMapRestore != null && (mainMapRestoreAtLastPhase || current.endedByClaimedFault))
                {
                    if (mainMapRestore.Members.Count > 1)
                    {
                        batchMainMapRestore = mainMapRestore;
                    }
                    else
                    {
                        MainMapRestoreClosed = true;
                        CloseMainMapRestore(mainMapRestore, "");
                    }
                }

                mainMapRestore = null;

                try
                {
                    List<string> componentNotes = [];
                    List<RimRunnerMapRestore> worldRestores = [];
                    bool park = CarriesRegisteredToNextPhase && current.exception == null;
                    current.onParallelSite |= RimRunnerIsolatedMap.AnyLeasedOnParallelSite;
                    foreach (string drift in RimRunnerIsolatedMap.Release(IsHeldForCheckpoint, componentNotes, park, worldRestores))
                    {
                        AddResidueDiagnostic(drift);
                    }

                    AddComponentNotes(componentNotes);
                    foreach (RimRunnerMapRestore restore in worldRestores)
                    {
                        AddWorldNotes(restore.WorldNotes, "");
                        RimRunnerSession.WorldTally?.NoteClosed(restore, [current.id], "收尾时独立小图已不在");
                    }
                }
                catch (Exception exception)
                {
                    current.cleanupErrors.Add("独立小图收尾失败：" + exception);
                    RimRunnerSession.WorldTally?.NoteMissed([current.id], "独立小图收尾失败");
                }

                try
                {
                    AppendLateTeardownFingerprints(current.id);
                }
                catch (Exception exception)
                {
                    current.cleanupErrors.Add("收尾末次残留判定失败：" + exception);
                }

                cleanupScope = null;
                try
                {
                    RunTeardownStep(current, "窗口监视", RimRunnerWindowWatch.EndCase);
                    RunTeardownStep(current, "天光钉", RimRunnerDaylightPin.Reset);
                    RunTeardownStep(current, "tick 分档钉", RimRunnerTickRatePin.EndCase);
                    RunTeardownStep(current, "相机夹具", RimRunnerCameraFixture.EndPhase);
                    RunTeardownStep(current, "开销计量器", RimRunnerMapCostMeter.EndCase);
                    RunTeardownStep(current, "tick 交付", RimRunnerTickDelivery.Reset);
                    RunTeardownStep(current, "半径环记录", RimRunnerRadiusRingCapture.ResetForCaseEnd);
                    RunTeardownStep(current, "观测缓冲", RimRunnerObservationBuffer.EndCase);
                }
                finally
                {
                    Active = null;
                }
            }
        }
    }

    private static void RunTeardownStep(RimRunnerResultEntry entry, string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            entry.cleanupErrors.Add(step + " 收尾失败：" + exception);
        }
    }

    private static void RunScenarioRestores(RimRunnerResultEntry entry, RimRunnerCleanupScope? restores)
    {
        if (restores == null)
        {
            return;
        }

        entry.cleanupErrors.AddRange(restores.Cleanup());
        entry.endedByClaimedFault |= restores.EndedByClaimedFault;
    }

    private void CloseMainMapRestore(RimRunnerMapRestore restore, string batch)
    {
        try
        {
            foreach (RimRunnerMapRestore.Unrestorable unres in restore.Close(IsHeldForCheckpoint))
            {
                MainMapUnrestorable = true;
                AddResidueDiagnostic(batch + "主图无法还原：" + unres.Text,
                    "已排定回到主队列时回载基线档");
            }

            AddComponentNotes(restore.ComponentNotes);
            AddWorldNotes(restore.WorldNotes, batch);
            RimRunnerSession.WorldTally?.NoteClosed(restore, restore.Members, "收尾时主图已不在");
        }
        catch (Exception exception)
        {
            MainMapUnrestorable = true;
            InCase.cleanupErrors.Add((batch.Length > 0 ? batch + "主图还原失败：" : "主图收尾还原失败：") + exception);
            RimRunnerSession.WorldTally?.NoteMissed(restore.Members, "主图收尾还原失败");
        }
    }

    internal void CloseBatchMainMapRestore()
    {
        RimRunnerMapRestore? restore = batchMainMapRestore;
        if (restore == null)
        {
            return;
        }

        batchMainMapRestore = null;
        MainMapRestoreClosed = true;
        string batch = restore.BatchLabel + "收尾时";
        try
        {
            using (restore.EnterGap())
            {
                RunScenarioRestores(InCase, restore.ScenarioRestores);
                CloseMainMapRestore(restore, batch);
                try
                {
                    AppendLateTeardownFingerprints(restore.BatchLabel);
                }
                catch (Exception exception)
                {
                    InCase.cleanupErrors.Add(batch + "主图还原后的残留判定失败：" + exception);
                }
            }
        }
        catch (Exception exception)
        {
            MainMapUnrestorable = true;
            InCase.cleanupErrors.Add(batch + "主图还原失败：" + exception);
            RimRunnerSession.WorldTally?.NoteMissed(restore.Members, "主图收尾还原失败");
        }
    }

    /// <summary>再领一张独立小图，用例收尾时引擎收回；不给档位与首张同尺寸。</summary>
    /// <remarks>只在独立小图上执行的用例体或场面步骤内可调；领到的图可能接自池中前一条用例，领到那刻已有的对象算基线；地面恒与首张同，ctx.Map 仍是首张。</remarks>
    public Map AcquireIsolatedMap(RimRunnerMapTier? tier = null) =>
        AcquireIsolatedMapOf(tier is { } given ? RimRunnerIsolatedMap.SizeOf(given) : null);

    /// <summary>再领一张给定宽高的独立小图，用例收尾时引擎收回。</summary>
    /// <remarks>调用面与收回同不给尺寸的那一版；宽高非正即抛。</remarks>
    /// <param name="width">宽，x 方向格数。</param>
    /// <param name="height">高，z 方向格数。</param>
    public Map AcquireIsolatedMap(int width, int height)
    {
        IntVec2 size = new(width, height);
        if (RimRunnerIsolatedMap.SizeFault(size) is { } fault)
        {
            throw new ArgumentOutOfRangeException(nameof(width), RimRunnerLog.Prefix + " AcquireIsolatedMap：" + fault);
        }

        return AcquireIsolatedMapOf(size);
    }

    private Map AcquireIsolatedMapOf(IntVec2? size)
    {
        RequireBody("AcquireIsolatedMap");

        Map map = RimRunnerIsolatedMap.Acquire(size);
        CaptureAcquiredMapBaseline(map, RimRunnerIsolatedMap.LabelOf(RimRunnerIsolatedMap.LeasedMaps.Count - 1));
        return map;
    }

    /// <summary>按键登记测试对象 pawn，跨 phase 由 GetPawn 取回。</summary>
    /// <remarks>名下的收尾（钉住、需求冻结、回收）随交棒走到末 phase；末 phase 登记的仍归本用例回收。</remarks>
    public void RegisterPawn(string key, Pawn pawn)
    {
        pawns[key] = pawn;
        RimRunnerObservationBuffer.ObservePawn(pawn);
    }

    /// <summary>按键登记测试对象物品（地上的或图上容器里的），跨 phase 由 GetThing 取回。</summary>
    /// <remarks>名下的 TrackThing 与 DeferUntilRecycled 随交棒带到末 phase。末 phase 登记的归本用例回收。读档后地上与容器里都找。pawn 走 RegisterPawn，死者登记 Corpse。</remarks>
    /// <exception cref="ArgumentException">thing 是 pawn（含死者本身）时抛。</exception>
    public void RegisterThing(string key, Thing thing)
    {
        if (thing == null)
        {
            throw new ArgumentNullException(nameof(thing));
        }

        if (thing is Pawn)
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " RegisterThing 不收 pawn（含死者本身）：" + key, nameof(thing));
        }

        things[key] = thing;
    }

    internal Thing? RegisteredOrNull(string key)
    {
        if (pawns.TryGetValue(key, out Pawn? pawn))
        {
            return pawn;
        }

        return things.TryGetValue(key, out Thing? thing) ? thing : null;
    }

    internal bool CarriesRegisteredToNextPhase;

    internal void ReleaseHandoffIfClaimed()
    {
        if (Current != null && Current.endedByClaimedFault)
        {
            CarriesRegisteredToNextPhase = false;
        }
    }

    /// <summary>pawn 是否在 checkpoint 交棒名单上（本 phase 待交棒或同批成员交棒中）。</summary>
    public bool IsHeldForCheckpoint(Pawn pawn)
    {
        if (pawn == null)
        {
            return false;
        }

        if (CarriesRegisteredToNextPhase && IsRegistered(pawn))
        {
            return true;
        }

        return RimRunnerSession.IsHeldByCheckpointBatch(pawn.ThingID);
    }

    /// <summary>物品是否在 checkpoint 交棒名单上（本 phase 待交棒或同批成员交棒中）。</summary>
    public bool IsHeldForCheckpoint(Thing thing)
    {
        if (thing == null)
        {
            return false;
        }

        if (thing is Pawn pawn)
        {
            return IsHeldForCheckpoint(pawn);
        }

        if (CarriesRegisteredToNextPhase && IsRegisteredThing(thing))
        {
            return true;
        }

        return RimRunnerSession.IsHeldByCheckpointBatch(thing.ThingID);
    }

    private bool IsRegisteredThing(Thing thing)
    {
        foreach (Thing registered in things.Values)
        {
            if (ReferenceEquals(registered, thing))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsRegistered(Pawn pawn)
    {
        foreach (Pawn registered in pawns.Values)
        {
            if (ReferenceEquals(registered, pawn))
            {
                return true;
            }
        }

        return false;
    }

    internal const string RegisteredPawnStateKeyPrefix = "__pawn:";
    internal const string RegisteredThingStateKeyPrefix = "__thing:";

    internal const string AllJobsStateKeyPrefix = "__allJobs:";

    internal const string ExcusedDeathStateKeyPrefix = "__excused:";

    internal void SnapshotRegistered()
    {
        if (checkpointState == null)
        {
            return;
        }

        foreach (KeyValuePair<string, Pawn> pair in pawns)
        {
            if (pair.Value?.ThingID == null)
            {
                continue;
            }

            checkpointState[RegisteredPawnStateKeyPrefix + pair.Key] = pair.Value.ThingID;
        }

        foreach (KeyValuePair<string, Thing> pair in things)
        {
            if (pair.Value?.ThingID == null)
            {
                continue;
            }

            checkpointState[RegisteredThingStateKeyPrefix + pair.Key] = pair.Value.ThingID;
        }

        foreach (Pawn pawn in pawns.Values)
        {
            if (pawn?.ThingID != null && expectedDeaths.Contains(pawn))
            {
                checkpointState[ExcusedDeathStateKeyPrefix + pawn.ThingID] = "1";
            }
        }
    }

    internal void ResolveRegisteredFromCheckpoint()
    {
        if (checkpointState == null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> pair in checkpointState)
        {
            if (pair.Key.StartsWith(RegisteredPawnStateKeyPrefix, StringComparison.Ordinal))
            {
                Pawn? pawn = FindSpawnedPawnById(pair.Value);
                if (pawn != null)
                {
                    pawns[pair.Key[RegisteredPawnStateKeyPrefix.Length..]] = pawn;
                }
            }
            else if (pair.Key.StartsWith(RegisteredThingStateKeyPrefix, StringComparison.Ordinal))
            {
                Thing? thing = FindThingOnMapsById(pair.Value);
                if (thing != null)
                {
                    things[pair.Key[RegisteredThingStateKeyPrefix.Length..]] = thing;
                }
            }
        }
    }

    private static readonly List<Thing> heldScratch = [];

    internal static Thing? FindThingOnMapsById(string thingId)
    {
        Thing? spawned = FindSpawnedThingById(thingId);
        if (spawned != null)
        {
            return spawned;
        }

        List<Map> maps = Find.Maps;
        if (maps == null)
        {
            return null;
        }

        List<Thing> scratch = heldScratch;
        for (int i = 0; i < maps.Count; i++)
        {
            List<Thing> all = maps[i].listerThings.AllThings;
            for (int j = 0; j < all.Count; j++)
            {
                if (all[j] is not IThingHolder holder)
                {
                    continue;
                }

                scratch.Clear();
                try
                {
                    ThingOwnerUtility.GetAllThingsRecursively(holder, scratch, allowUnreal: true);
                    for (int k = 0; k < scratch.Count; k++)
                    {
                        if (!scratch[k].Destroyed && string.Equals(scratch[k].ThingID, thingId, StringComparison.Ordinal))
                        {
                            return scratch[k];
                        }
                    }
                }
                finally
                {
                    scratch.Clear();
                }
            }
        }

        return null;
    }

    internal static Thing? FindSpawnedThingById(string thingId)
    {
        List<Map> maps = Find.Maps;
        if (maps == null)
        {
            return null;
        }

        for (int i = 0; i < maps.Count; i++)
        {
            List<Thing> all = maps[i].listerThings.AllThings;
            for (int j = 0; j < all.Count; j++)
            {
                if (all[j].Spawned && string.Equals(all[j].ThingID, thingId, StringComparison.Ordinal))
                {
                    return all[j];
                }
            }
        }

        return null;
    }

    internal static Pawn? FindSpawnedPawnById(string thingId)
    {
        List<Map> maps = Find.Maps;
        if (maps == null)
        {
            return null;
        }

        for (int i = 0; i < maps.Count; i++)
        {
            IReadOnlyList<Pawn> spawned = maps[i].mapPawns.AllPawnsSpawned;
            for (int j = 0; j < spawned.Count; j++)
            {
                if (string.Equals(spawned[j].ThingID, thingId, StringComparison.Ordinal))
                {
                    return spawned[j];
                }
            }
        }

        return null;
    }

    internal bool ContinuesPreviousPhase;

    /// <summary>按 RegisterPawn 的键取 pawn，没取到按阶认领为 Fail / Require。</summary>
    /// <remarks>phase 0 取未登记的键归 Require；已死、离图、交棒后解不回与续段取未交棒的键归 Fail。续段的观测册与敌对靶册按登记名册重起。</remarks>
    public Pawn GetPawn(string key) => GetPawn(key, claimInto: null);

    internal Pawn GetPawn(string key, RimRunnerResultEntry? claimInto)
    {
        if (!TryGetPawn(key, out Pawn? pawn, claimInto))
        {
            if (ContinuesPreviousPhase)
            {
                Claim(claimInto, "测试对象既未登记、上一 phase 也未交棒",
                    key);
            }

            ClaimPrecondition(claimInto, "指定的测试对象未登记", key);
        }

        return pawn;
    }

    /// <summary>按 RegisterPawn 的键取 pawn，未登记未交棒返 false，其余没取到归 Fail。</summary>
    public bool TryGetPawn(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Pawn? pawn)
        => TryGetPawn(key, out pawn, claimInto: null);

    internal bool TryGetPawn(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Pawn? pawn,
        RimRunnerResultEntry? claimInto)
    {
        if (!pawns.TryGetValue(key, out pawn))
        {
            if (checkpointState != null
                && checkpointState.TryGetValue(RegisteredPawnStateKeyPrefix + key, out string? thingId))
            {
                Claim(claimInto, "上一 phase 交棒的测试对象读档后在全部地图上解不回",
                    key + " · ThingID " + thingId);
            }

            return false;
        }

        if (pawn == null || pawn.Dead || !pawn.Spawned)
        {
            Claim(claimInto, "登记的测试对象已死亡或未生成",
                key + " · ThingID " + pawn?.ThingID);
        }

        return true;
    }

    /// <summary>按 RegisterThing 的键取物品，没取到按阶认领为 Fail / Require。</summary>
    /// <remarks>phase 0 取未登记的键归 Require；已销毁、不在任何地图上、交棒后解不回与续段取未交棒的键归 Fail。类型不匹配即抛，不认领。</remarks>
    public T GetThing<T>(string key) where T : Thing => GetThing<T>(key, claimInto: null);

    internal T GetThing<T>(string key, RimRunnerResultEntry? claimInto) where T : Thing
    {
        if (!TryGetThing(key, out T? thing, claimInto))
        {
            if (ContinuesPreviousPhase)
            {
                Claim(claimInto, "测试物品既未登记、上一 phase 也未交棒",
                    key);
            }

            ClaimPrecondition(claimInto, "指定的测试物品未登记", key);
        }

        return thing;
    }

    /// <summary>按 RegisterThing 的键取物品，从未登记也未交棒返 false，其余没取到归 Fail。</summary>
    /// <exception cref="InvalidOperationException">登记物品不是 T 时抛；这条不按 Fail 认领。</exception>
    public bool TryGetThing<T>(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? thing)
        where T : Thing => TryGetThing(key, out thing, claimInto: null);

    internal bool TryGetThing<T>(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? thing,
        RimRunnerResultEntry? claimInto) where T : Thing
    {
        thing = null;
        if (!things.TryGetValue(key, out Thing? registered))
        {
            if (checkpointState != null
                && checkpointState.TryGetValue(RegisteredThingStateKeyPrefix + key, out string? thingId))
            {
                Claim(claimInto, "上一 phase 交棒的测试物品读档后在全部地图上解不回",
                    key + " · ThingID " + thingId);
            }

            return false;
        }

        if (registered == null || registered.Destroyed || registered.MapHeld == null)
        {
            Claim(claimInto, "登记的测试物品已销毁或已不在任何地图上",
                key + " · ThingID " + registered?.ThingID);
        }

        if (registered is not T typed)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 测试物品 " + key + " 是 "
                                                + registered.GetType().Name + "，不是 " + typeof(T).Name);
        }

        thing = typed;
        return true;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Claim(RimRunnerResultEntry? claimInto, string reason, string subject)
    {
        if (claimInto == null)
        {
            RimRunnerFault.Fail(reason, subject);
        }

        throw RimRunnerFault.RecordFailure(claimInto!, reason, subject);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ClaimPrecondition(RimRunnerResultEntry? claimInto, string reason, string subject)
    {
        if (claimInto == null)
        {
            RimRunnerFault.Require(false, reason, subject);
        }

        throw new RimRunnerClaimedFault(RimRunnerClaimedFault.FaultKind.Precondition,
            RimRunnerSentinel.Compose(reason, subject));
    }
}
