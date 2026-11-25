using Verse.AI;
using Verse.AI.Group;

namespace RimRunner;

public partial class RimRunnerContext
{
    /// <summary>登记一段清理动作，在本 phase 末执行：单阶段用例即用例末，多阶段用例即存档之前。</summary>
    /// <remarks>回调与闭包保留到执行那一刻。与 TrackRestore、DeferUntilRecycled 同一个清理栈，后登记先执行。</remarks>
    public void Defer(Action cleanup, string description)
    {
        RequireCleanupScope().Defer(cleanup, description);
    }

    private RimRunnerCleanupScope? scenarioRestores;

    /// <summary>装配器用：登记一段整局状态的写回，在本用例的组件比对之前执行；多阶段用例在整个屏障批收尾时执行。</summary>
    /// <remarks>只能在 RimRunnerScenario.Ensure 里调，别处调用即抛；可能在屏障读档之后才执行，回调只捕获值，整局对象执行时现取。</remarks>
    /// <exception cref="InvalidOperationException">不在 RimRunnerScenario.Ensure 里调用时抛。</exception>
    public void DeferScenarioRestore(Action restore, string description)
    {
        if (restore == null)
        {
            throw new ArgumentNullException(nameof(restore));
        }

        if (!RimRunnerScenarioManager.InEnsure)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                                                + " DeferScenarioRestore 只能在 RimRunnerScenario.Ensure 里调：" + description);
        }

        if (IsScene)
        {
            Defer(restore, description);
            return;
        }

        (scenarioRestores ??= new RimRunnerCleanupScope()).Defer(restore, description);
    }

    internal RimRunnerCleanupScope? TakeScenarioRestores()
    {
        RimRunnerCleanupScope? taken = scenarioRestores;
        scenarioRestores = null;
        return taken;
    }

    private readonly Dictionary<Thing, List<OwnedCleanup>> ownedCleanups = [];
    private int ownedSweepAt = OwnedSweepFloor;
    private const int OwnedSweepFloor = 64;

    /// <summary>登记一段绑在对象上的收尾动作，回调参数是执行那一刻的对象。</summary>
    /// <remarks>pawn 回收即撤销，非 pawn 已销毁即不执行；与 Defer 同栈、后登记先执行；交棒随对象移到下一 phase，解不回即丢弃；null 同 Defer。参数是执行时的 owner：跨读档后对象已换新，闭包里别的引用须从参数现取。</remarks>
    public void DeferUntilRecycled<T>(T owner, Action<T> cleanup, string description) where T : Thing
    {
        if (cleanup == null)
        {
            throw new ArgumentNullException(nameof(cleanup));
        }

        AddOwned(owner, new OwnedCleanup(owner, current => cleanup((T)current), description));
    }

    private int ownedSequence;

    private void AddOwned(Thing owner, OwnedCleanup owned)
    {
        owned.Entry = RequireCleanupScope().Defer(owned.Run, owned.Description);
        owned.Sequence = ownedSequence++;
        if (owner == null)
        {
            return;
        }

        if (!ownedCleanups.TryGetValue(owner, out List<OwnedCleanup>? entries))
        {
            entries = new List<OwnedCleanup>(1);
            ownedCleanups[owner] = entries;
        }

        entries.Add(owned);
        if (ownedCleanups.Count >= ownedSweepAt)
        {
            ReleaseDestroyedOwners();
        }
    }

    private sealed class OwnedCleanup
    {
        internal OwnedCleanup(Thing owner, Action<Thing> action, string description)
        {
            Owner = owner;
            Action = action;
            Description = description;
        }

        internal Thing Owner;
        internal readonly Action<Thing> Action;
        internal readonly string Description;
        internal RimRunnerCleanupScope.CleanupAction? Entry;
        internal int Sequence;

        internal void Run()
        {
            if (Owner is not Pawn and { Destroyed: true }) return;
            Action(Owner);
        }
    }

    internal sealed class CarriedCleanup
    {
        internal string? ThingId;
        internal bool IsPawn;
        internal Action<Thing>? Action;
        internal required string Description;
        internal RimRunnerMapTeardown.GeneratedMap? GeneratedMap;
    }

    private sealed class GeneratedMapEntry
    {
        internal required RimRunnerMapTeardown.GeneratedMap Map;
        internal required RimRunnerCleanupScope.CleanupAction Entry;
        internal int Sequence;
    }

    private readonly List<GeneratedMapEntry> generatedMaps = [];

    private void AddGenerated(RimRunnerMapTeardown.GeneratedMap generated)
    {
        generatedMaps.Add(new GeneratedMapEntry
        {
            Map = generated,
            Entry = RequireCleanupScope().Defer(generated.Finish, generated.Description),
            Sequence = ownedSequence++
        });
    }

    private void RegisterCarriedGeneratedMaps()
    {
        foreach (GeneratedMapEntry entry in generatedMaps)
        {
            RimRunnerMapLifecyclePatches.Register(entry.Map.Map);
        }
    }

    private List<CarriedCleanup>? carriedCleanups;

    internal void SetCarriedCleanups(List<CarriedCleanup>? carried)
    {
        carriedCleanups = carried;
    }

    private void AdoptCarriedCleanups()
    {
        if (carriedCleanups == null || carriedCleanups.Count == 0)
        {
            return;
        }

        foreach (CarriedCleanup carried in carriedCleanups)
        {
            if (carried.GeneratedMap != null)
            {
                carried.GeneratedMap.Rebind(this);
                AddGenerated(carried.GeneratedMap);
                continue;
            }

            Thing? owner = carried.IsPawn
                ? FindSpawnedPawnById(carried.ThingId!)
                : FindThingOnMapsById(carried.ThingId!);
            if (owner != null)
            {
                AddOwned(owner, new OwnedCleanup(owner, carried.Action!, carried.Description));
            }
        }

        carriedCleanups.Clear();
    }

    private void TakeHandedOffCleanups()
    {
        handsOffRegistered = false;
        if (!CarriesRegisteredToNextPhase || carriedCleanups == null || InCase.endedByClaimedFault)
        {
            return;
        }

        handsOffRegistered = true;

        List<Thing> owners = ownedCleanups.Keys
            .Where(owner => owner is Pawn pawn ? IsRegistered(pawn) : IsRegisteredThing(owner))
            .ToList();
        List<GeneratedMapEntry> maps = InCase.exception != null
            ? []
            : generatedMaps.Where(entry => !entry.Entry.Released && entry.Map.Live).ToList();
        if (owners.Count == 0 && maps.Count == 0)
        {
            return;
        }

        List<(int Sequence, CarriedCleanup Carried)> handed = owners
            .SelectMany(owner => ownedCleanups[owner])
            .Where(owned => !owned.Entry!.Released)
            .Select(owned => (owned.Sequence, new CarriedCleanup
            {
                ThingId = owned.Owner.ThingID,
                IsPawn = owned.Owner is Pawn,
                Action = owned.Action,
                Description = owned.Description
            }))
            .Concat(maps.Select(entry => (entry.Sequence, new CarriedCleanup
            {
                GeneratedMap = entry.Map,
                Description = entry.Map.Description
            })))
            .OrderBy(item => item.Sequence)
            .ToList();
        foreach (Thing owner in owners)
        {
            ReleaseOwned(owner);
        }

        bool onIsolatedMap = RimRunnerIsolatedMap.IsPoolMap(Map);
        foreach (GeneratedMapEntry entry in maps)
        {
            cleanupScope!.Release(entry.Entry);
            generatedMaps.Remove(entry);
            if (onIsolatedMap)
            {
                RimRunnerIsolatedMap.HoldGenerated(InCase.id, entry.Map.Map);
            }
        }

        carriedCleanups.AddRange(handed.Select(item => item.Carried));
    }

    internal static List<string> RunStrandedCarriedCleanups(List<CarriedCleanup>? carried, out bool endedByClaimedFault)
    {
        endedByClaimedFault = false;
        if (carried == null || carried.Count == 0)
        {
            return [];
        }

        RimRunnerCleanupScope scope = new();
        foreach (CarriedCleanup entry in carried)
        {
            if (entry.GeneratedMap != null)
            {
                RimRunnerMapTeardown.GeneratedMap generated = entry.GeneratedMap;
                generated.Rebind(null);
                scope.Defer(generated.Finish, entry.Description);
                continue;
            }

            Thing? owner = entry.IsPawn
                ? FindSpawnedPawnById(entry.ThingId!)
                : FindThingOnMapsById(entry.ThingId!);
            if (owner != null)
            {
                Action<Thing> action = entry.Action!;
                scope.Defer(() => action(owner), entry.Description);
            }
        }

        carried.Clear();
        List<string> errors = scope.Cleanup();
        endedByClaimedFault = scope.EndedByClaimedFault;
        return errors;
    }

    private bool handsOffRegistered;

    private object?[]? fixtureSnapshot;

    private void ReleaseFixtureState()
    {
        try
        {
            foreach (string line in RimRunnerFixtureState.Release(InCase, fixtureSnapshot,
                         pawn => handsOffRegistered && pawns.Values.Contains(pawn)))
            {
                RimRunnerSession.AddRuntimeWarning(InCase, RimRunnerWarningKey.FixtureResidue, line);
            }
        }
        catch (Exception exception)
        {
            InCase.cleanupErrors.Add("夹具状态核对失败：" + exception);
        }
        finally
        {
            fixtureSnapshot = null;
            handsOffRegistered = false;
        }
    }

    private void ReleaseDestroyedOwners()
    {
        List<Thing>? gone = null;
        foreach (Thing owner in ownedCleanups.Keys)
        {
            if (owner is not Pawn && owner.Destroyed)
            {
                (gone ??= []).Add(owner);
            }
        }

        if (gone != null)
        {
            foreach (Thing owner in gone)
            {
                ReleaseOwned(owner);
            }
        }

        ownedSweepAt = Math.Max(OwnedSweepFloor, ownedCleanups.Count * 2);
    }

    private void ReleaseOwned(Thing owner)
    {
        if (!ownedCleanups.TryGetValue(owner, out List<OwnedCleanup>? entries))
        {
            return;
        }

        ownedCleanups.Remove(owner);
        foreach (OwnedCleanup owned in entries)
        {
            cleanupScope?.Release(owned.Entry!);
        }
    }

    internal int PendingCleanupCount => cleanupScope?.PendingCount ?? 0;

    internal int OwnedCleanupCount(Thing owner)
        => ownedCleanups.TryGetValue(owner, out List<OwnedCleanup>? entries) ? entries.Count : 0;

    internal bool IsDeathExcused(Pawn pawn) => expectedDeaths.Contains(pawn);

    internal void NotifyRecycled(Pawn pawn)
    {
        ReleaseOwned(pawn);
        ReleaseWatchedPawn(pawn);
        ReleaseDestroyedOwners();
    }

    private static readonly Action<Thing> RecyclePawn = current => RimRunnerPawns.Cleanup((Pawn)current);

    /// <summary>登记一次性 pawn：回收与观测都归引擎，返回原 pawn；重复登记保持原登记。</summary>
    public Pawn TrackPawn(Pawn pawn)
    {
        if (TracksForRecycle(pawn))
        {
            return pawn;
        }

        RimRunnerObservationBuffer.ObservePawn(pawn);
        RimRunnerCaseWatch.Register(pawn);
        AddOwned(pawn, new OwnedCleanup(pawn, RecyclePawn, "回收 pawn " + (pawn?.ThingID ?? "<null>")));
        return pawn!;
    }

    internal bool TracksForRecycle(Pawn pawn) =>
        pawn != null && ownedCleanups.TryGetValue(pawn, out List<OwnedCleanup>? entries)
                     && entries.Any(e => e.Action == RecyclePawn);

    /// <summary>登记作用域内探针 pawn：只入观察与靶册，回收归调用方。</summary>
    public void WatchScopedPawn(Pawn pawn)
    {
        RimRunnerObservationBuffer.ObservePawn(pawn);
        RimRunnerCaseWatch.Register(pawn);
    }

    /// <summary>登记一次性 Thing，用例末销毁。</summary>
    /// <remarks>用例末该对象在地上、pawn 手上或容器里都销毁，已销毁的跳过；被打包成 MinifiedThing 的连打包壳一起销毁。pawn 按 TrackPawn 的回收处理，不入观测。</remarks>
    public T TrackThing<T>(T thing) where T : Thing
    {
        if (thing is Pawn pawn)
        {
            if (!TracksForRecycle(pawn))
            {
                AddOwned(pawn, new OwnedCleanup(pawn, RecyclePawn, "回收 pawn " + pawn.ThingID));
            }

            return thing;
        }

        DeferUntilRecycled(thing, current =>
        {
            if (current == null || current.Destroyed)
            {
                return;
            }

            if (current.ParentHolder is MinifiedThing { Destroyed: false } shell)
            {
                shell.Destroy();
                return;
            }

            current.Destroy();
        }, "销毁 Thing " + (thing?.ThingID ?? "<null>"));
        return thing!;
    }

    /// <summary>登记借来的 vanilla 参照物，用例末只 DeSpawn。</summary>
    public T TrackBorrowed<T>(T thing) where T : Thing
    {
        Defer(() =>
        {
            if (thing != null && thing.Spawned)
            {
                thing.DeSpawn();
            }
        }, "DeSpawn 借物 " + (thing?.ThingID ?? "<null>"));
        return thing!;
    }

    /// <summary>生成用例地图：调 generate，返回 generate 生成的图，收尾拆图。</summary>
    /// <remarks>收尾回收图上 pawn、拆图，丢弃本调用以来新进世界池的 pawn 与孤儿，generate 抛出或返回 null 也仍执行；多阶段用例拆在末 phase。图的 MapParent 的销毁登记在本调用之前，多阶段登记在末 phase。</remarks>
    public Map GenerateMap(Func<Map> generate, string description)
    {
        if (generate == null)
        {
            throw new ArgumentNullException(nameof(generate));
        }

        var generated = new RimRunnerMapTeardown.GeneratedMap(this, description);
        AddGenerated(generated);
        generated.Map = generate();
        RimRunnerMapLifecyclePatches.Register(generated.Map);
        return generated.Map;
    }

    /// <summary>登记用例挂上的 hediff，用例末移除。</summary>
    /// <remarks>按 loadID 认：交棒读档后 pawn 身上是另一个 Hediff 实例，仍按 loadID 找到并移除。</remarks>
    public Hediff TrackHediff(Pawn pawn, Hediff hediff)
    {
        int loadId = hediff?.loadID ?? -1;
        DeferUntilRecycled(pawn, current =>
        {
            Hediff? live = current?.health?.hediffSet.hediffs.FirstOrDefault(h => h.loadID == loadId);
            if (live != null)
            {
                current!.health.RemoveHediff(live);
            }
        }, "移除 hediff " + (hediff?.def?.defName ?? "<null>"));
        return hediff!;
    }

    /// <summary>登记用例赋予的能力，用例末摘除。</summary>
    public void AutoRemoveAbility(Pawn pawn, AbilityDef abilityDef)
    {
        DeferUntilRecycled(pawn, current =>
        {
            if (current?.abilities?.GetAbility(abilityDef) != null)
            {
                current.abilities.RemoveAbility(abilityDef);
            }
        }, "移除 ability " + (abilityDef?.defName ?? "<null>"));
    }

    /// <summary>登记一段还原动作，在本 phase 末执行（单阶段用例即用例末），用于改过的全局态与设置。</summary>
    /// <remarks>与 Defer 同一个清理栈，后登记先执行。</remarks>
    public void TrackRestore(Action restore, string description)
    {
        Defer(restore, description);
    }

    /// <summary>冻结 pawn 全部需求的自然推进（默认先填满），用例末撤冻并还原原值。</summary>
    /// <remarks>停住需求的自然推进、营养不良增减与心情向即时心情的收敛；需求增删与显式写 CurLevel 照常生效；精神崩溃照按停住的心情值掷骰。冻结期间读需求、心情或 thought 出 NEEDS-FROZEN-READ；验冻结读 IsFrozen。</remarks>
    /// <param name="pawn">冻结它的需求。</param>
    /// <param name="fillToMax">为真时先把各需求填到 MaxLevel 再冻结；为假时冻在当前值。</param>
    public void SuspendNeeds(Pawn pawn, bool fillToMax = true)
    {
        if (pawn?.needs == null)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " SuspendNeeds 的 pawn 没有 needs");
        }

        RimRunnerNeedsFreeze.Add(pawn);
        DeferUntilRecycled(pawn, RimRunnerNeedsFreeze.Remove, "撤下需求冻结 " + (pawn.ThingID ?? "<null>"));
        if (!fillToMax)
        {
            return;
        }

        List<(NeedDef def, float before)> saved = pawn.needs.AllNeeds
            .Select(need => (need.def, need.CurLevel))
            .ToList();
        foreach (Need need in pawn.needs.AllNeeds)
        {
            need.CurLevel = need.MaxLevel;
        }

        DeferUntilRecycled(pawn, current =>
        {
            foreach ((NeedDef def, float before) in saved)
            {
                Need? need = current?.needs?.TryGetNeed(def);
                need?.CurLevel = before;
            }
        }, "还原 needs " + (pawn.ThingID ?? "<null>"));
    }

    /// <summary>钉住行动：自发 job 改为无期限 Wait、敌对反应置 Ignore，用例末撤销钉住并还原。</summary>
    /// <remarks>连带挡心情崩溃、特性精神态、社交斗殴（全图版 SuppressRandomMentalStates）与 Wait 的自动出手（贴身近战、征召待命瞄准、扑火）；hediff 崩溃归 BlockHediffBreaks；再钉即重起 Wait。</remarks>
    public void PinAgency(Pawn pawn)
    {
        bool already = RimRunnerAgencyPin.IsRegistered(pawn);
        RimRunnerAgencyPin.Add(pawn);
        if (already) return;
        DeferUntilRecycled(pawn, RimRunnerAgencyPin.Remove, "解除钉住 " + (pawn.ThingID ?? "<null>"));
        if (pawn.playerSettings != null)
        {
            RimRunnerAgencyPin.SaveHostility(pawn, pawn.playerSettings.hostilityResponse);
            pawn.playerSettings.hostilityResponse = HostilityResponseMode.Ignore;
            DeferUntilRecycled(pawn, RimRunnerAgencyPin.RestoreHostility, "还原 hostilityResponse " + (pawn.ThingID ?? "<null>"));
        }
    }

    /// <summary>放开 PinAgency 钉住的 pawn：还原敌对反应、结束钉住时起的 Wait；未钉时无操作。</summary>
    /// <remarks>按 pawn 寻址，交棒读档后仍能放开；放开后可再钉。</remarks>
    public void ReleaseAgency(Pawn pawn)
    {
        if (!RimRunnerAgencyPin.IsRegistered(pawn)) return;
        RimRunnerAgencyPin.Remove(pawn);
        RimRunnerAgencyPin.RestoreHostility(pawn);
        if (pawn.Spawned && !pawn.Dead && pawn.CurJobDef == JobDefOf.Wait)
        {
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }
    }

    /// <summary>挡该 pawn 的 hediff 阶段崩溃，用例末撤。</summary>
    /// <remarks>只挡 hediff 阶段按 mentalBreakMtbDays 掷中的精神崩溃，掷骰照付；心情崩溃、特性精神态与社交斗殴归 PinAgency（按 pawn）与 SuppressRandomMentalStates（全图）。</remarks>
    public void BlockHediffBreaks(Pawn pawn)
    {
        RimRunnerHediffBreakBlock.Add(pawn);
        DeferUntilRecycled(pawn, RimRunnerHediffBreakBlock.Remove, "撤下 hediff 崩溃屏蔽 " + pawn.ThingID);
    }

    /// <summary>整场挡 hediff 阶段崩溃，含之后生成的 pawn，用例末撤。</summary>
    /// <remarks>作用对象同 BlockHediffBreaks，作用于本用例的全部 pawn；心情崩溃、特性精神态与社交斗殴的全图版是 SuppressRandomMentalStates。</remarks>
    public void BlockHediffBreaksForCase()
    {
        if (RimRunnerHediffBreakBlock.CaseWide) return;
        RimRunnerHediffBreakBlock.SetCaseWide(true);
        Defer(() => RimRunnerHediffBreakBlock.SetCaseWide(false), "撤下整场 hediff 崩溃屏蔽");
    }

    /// <summary>本用例全图挡随机精神态与社交斗殴，含之后生成的 pawn，用例末撤。</summary>
    /// <remarks>只挡心情崩溃、特性给予的精神态与社交斗殴（含劝化失败一路）；可重调，只登记一次撤销；hediff 阶段掷中的崩溃归 BlockHediffBreaksForCase。</remarks>
    public void SuppressRandomMentalStates()
    {
        RimRunnerEnvChecks.NoteUse(RimRunnerFacility.AgencyPin);
        if (RimRunnerAgencyPin.RandomMentalStatesSuppressed) return;
        RimRunnerAgencyPin.SetRandomMentalStatesSuppressed(true);
        Defer(() => RimRunnerAgencyPin.SetRandomMentalStatesSuppressed(false), "撤下全图随机精神态屏蔽");
    }

    /// <summary>解除威胁：殖民者的敌意响应、炮塔与 AI 搜敌都不再以该 pawn 为目标。</summary>
    /// <remarks>带 lord 的 pawn 调即抛。keepApplied 为真时清空 duty 后补写，推 tick 重算威胁出 THREAT-DISABLE-LORD。回收还原，交棒置空。</remarks>
    public void DisableThreat(Pawn pawn, bool keepApplied = true)
    {
        if (pawn?.mindState == null)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " DisableThreat 的 pawn 没有 mindState");
        }

        if (pawn.GetLord() != null)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " DisableThreat 的 pawn 有 lord（" + pawn.LabelShort
                + "）：lord 会按 toil 覆写 duty，且 lord 的 duty 节点按 def 索引取子节点，运行期 def 不在表内");
        }

        if (RimRunnerThreatDisable.Registered.Add(pawn))
        {
            Pawn registeredAs = pawn;
            PawnDuty before = pawn.mindState.duty;
            DeferUntilRecycled(pawn, current =>
            {
                RimRunnerThreatDisable.Forget(current);
                if (current.mindState?.duty?.def != RimRunnerThreatDisable.Def) return;
                current.mindState.duty = ReferenceEquals(current, registeredAs) ? before : null;
            }, "还原 duty " + (pawn.ThingID ?? "<null>"));
        }

        if (keepApplied) RimRunnerThreatDisable.Kept.Add(pawn);
        else RimRunnerThreatDisable.Kept.Remove(pawn);
        if (pawn.mindState.duty?.def != RimRunnerThreatDisable.Def)
            pawn.mindState.duty = new PawnDuty(RimRunnerThreatDisable.Def);
    }

    private RimRunnerCleanupScope RequireCleanupScope()
    {
        if (cleanupScope == null)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 当前不在用例清理栈内");
        }

        return cleanupScope;
    }
}
