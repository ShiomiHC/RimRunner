using Verse.AI.Group;

namespace RimRunner;

internal static class RimRunnerCaseWatch
{
    internal sealed class WatchedTarget
    {
        internal Pawn? Pawn;
        internal bool EverDowned;
    }

    private sealed class AwakeWatch
    {
        internal Pawn? Pawn;
        internal required Hediff Pin;
    }

    private static readonly List<AwakeWatch> awakeWatched = [];
    private static readonly List<string> awakeLost = [];
    private static readonly HashSet<Pawn> threatDisableWarned = [];

    internal static IReadOnlyList<string> AwakeLost => awakeLost;

    private static readonly List<WatchedTarget> watched = [];
    private static readonly List<Pawn> candidatePawns = [];
    private static readonly List<Hediff> injuryBuffer = [];
    private static readonly Dictionary<Hediff, float> injuryPeaks = [];

    internal static IReadOnlyList<WatchedTarget> All => watched;

    internal static IEnumerable<KeyValuePair<Hediff, float>> InjuryPeaks => injuryPeaks;

    internal static void BeginCase()
    {
        Patch_HediffSet_GetRandomNotMissingPart_RimRunnerDummy.BeginCase();
        awakeWatched.Clear();
        awakeLost.Clear();
        threatDisableWarned.Clear();
        watched.Clear();
        candidatePawns.Clear();
        injuryPeaks.Clear();
    }

    internal static void Register(Pawn pawn)
    {
        if (pawn == null || pawn.Faction == null || !pawn.HostileTo(Faction.OfPlayer))
        {
            return;
        }

        for (int i = 0; i < watched.Count; i++)
        {
            if (watched[i].Pawn == pawn)
            {
                return;
            }
        }

        watched.Add(new WatchedTarget { Pawn = pawn, EverDowned = pawn.Downed });
    }

    internal static void Release(Pawn pawn, List<WatchedTarget> releasedTargets,
        List<KeyValuePair<Hediff, float>> releasedPeaks)
    {
        for (int i = watched.Count - 1; i >= 0; i--)
        {
            if (watched[i].Pawn == pawn)
            {
                releasedTargets.Add(watched[i]);
                watched.RemoveAt(i);
            }
        }

        candidatePawns.RemoveAll(candidate => candidate == pawn);
        awakeWatched.RemoveAll(entry => entry.Pawn == pawn);
        threatDisableWarned.Remove(pawn);
        List<Hediff>? gone = null;
        foreach (KeyValuePair<Hediff, float> pair in injuryPeaks)
        {
            if (pair.Key?.pawn == pawn)
            {
                releasedPeaks.Add(pair);
                (gone ??= []).Add(pair.Key);
            }
        }

        if (gone != null)
        {
            foreach (Hediff hediff in gone)
            {
                injuryPeaks.Remove(hediff);
            }
        }
    }

    internal static void RegisterCandidatePawn(Pawn pawn)
    {
        candidatePawns.Add(pawn);
        SampleTick();
    }

    internal static void RegisterAwakeImmobile(Pawn pawn, Hediff pin)
        => awakeWatched.Add(new AwakeWatch { Pawn = pawn, Pin = pin });

    internal static void SampleTick()
    {
        for (int i = awakeWatched.Count - 1; i >= 0; i--)
        {
            AwakeWatch entry = awakeWatched[i];
            Pawn? pawn = entry.Pawn;
            if (pawn == null || pawn.Dead || pawn.Destroyed || !pawn.health.hediffSet.hediffs.Contains(entry.Pin))
            {
                awakeWatched.RemoveAt(i);
                continue;
            }

            if (!pawn.Awake())
            {
                awakeLost.Add(pawn.LabelShort + "（" + pawn.ThingID + "，kind=" + pawn.kindDef.defName + "）于 tick "
                              + Find.TickManager.TicksGame + " 不再清醒，当拍意识 "
                              + pawn.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness).ToString("0.##")
                              + "、疼痛 " + pawn.health.hediffSet.PainTotal.ToString("0.##"));
                awakeWatched.RemoveAt(i);
            }
        }

        if (RimRunnerThreatDisable.Kept.Count > 0
            && RimRunnerContext.AmbientPhase == RimRunnerPhase.Case
            && RimRunnerContext.Active?.Current is { } currentOwner)
        {
            foreach (Pawn pawn in RimRunnerThreatDisable.Kept)
            {
                if (pawn == null || pawn.Dead || pawn.Destroyed || !pawn.Spawned
                    || !RimRunnerThreatDisable.Keeps(pawn)
                    || !RimRunnerThreatDisable.Registered.IsOwnedBy(pawn, currentOwner)
                    || threatDisableWarned.Contains(pawn))
                {
                    continue;
                }

                if (!pawn.ThreatDisabled(null))
                {
                    threatDisableWarned.Add(pawn);
                    Lord? lord = pawn.GetLord();
                    string duty = pawn.mindState?.duty?.def?.defName ?? "<无>";
                    RimRunnerSentinel.Warn(RimRunnerWarningKey.ThreatDisableLord,
                        "DisableThreat 的持有者重新算威胁（ThreatDisabled 为假）",
                        pawn.LabelShort + "（" + pawn.ThingID + "）于 tick " + Find.TickManager.TicksGame
                        + "，duty=" + duty
                        + (lord != null ? "，lord=" + (lord.LordJob?.GetType().Name ?? "<无 LordJob>") : ""));
                }
            }
        }

        for (int i = 0; i < watched.Count; i++)
        {
            WatchedTarget target = watched[i];
            if (!target.EverDowned && target.Pawn != null && !target.Pawn.Dead
                && target.Pawn.Downed)
            {
                target.EverDowned = true;
            }
        }

        for (int i = 0; i < candidatePawns.Count; i++)
        {
            Pawn pawn = candidatePawns[i];
            if (pawn?.health == null)
            {
                continue;
            }

            bool healing = false;
            injuryBuffer.Clear();
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int j = 0; j < hediffs.Count; j++)
            {
                Hediff hediff = hediffs[j];
                if (hediff is Hediff_Injury)
                {
                    injuryBuffer.Add(hediff);
                }
                else if (!healing && HasHealingComp(hediff))
                {
                    healing = true;
                }
            }

            if (!healing)
            {
                continue;
            }

            for (int j = 0; j < injuryBuffer.Count; j++)
            {
                Hediff injury = injuryBuffer[j];
                if (!injuryPeaks.TryGetValue(injury, out float peak)
                    || injury.Severity > peak)
                {
                    injuryPeaks[injury] = injury.Severity;
                }
            }
        }
    }

    private static Type[]? healingCompTypes;

    private static bool HasHealingComp(Hediff hediff)
    {
        if (hediff is not HediffWithComps withComps || withComps.comps == null)
        {
            return false;
        }

        healingCompTypes ??= RimRunnerProduct.Capabilities.HealingCompTypes.ToArray();

        for (int i = 0; i < withComps.comps.Count; i++)
        {
            HediffComp comp = withComps.comps[i];
            for (int j = 0; j < healingCompTypes.Length; j++)
            {
                if (healingCompTypes[j].IsInstanceOfType(comp))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

internal sealed class RimRunnerScenarioPawnRoster
{
    internal readonly HashSet<string> Awake = new(StringComparer.Ordinal);
    internal readonly HashSet<string> Dormant = new(StringComparer.Ordinal);
}

public partial class RimRunnerContext
{
    private sealed class MapResidueBaseline
    {
        internal required Map Map;
        internal required string Label;
        internal required HashSet<Pawn> Pawns;
        internal required HashSet<Pawn> Dormant;
        internal required HashSet<Thing> Items;
        internal required HashSet<Thing> Buildings;
    }

    internal const string MainMapLabel = "主图";

    internal const string PawnResidueTitle = "本用例新增的 pawn";
    internal const string WokenResidueTitle = "基线时休眠";
    internal const string ItemResidueTitle = "自建物品";
    internal const string BuildingResidueTitle = "座建筑";
    internal const string FireResidueTitle = "处火";
    internal const string ContinuationPrefix = "续段起点图上 ";
    internal const string ContinuationLeakTitle = "非本用例 spawned pawn";
    internal const string ContinuationWokenTitle = "装配时休眠";
    internal const string QueueResidueTitle = "叙事者队列";
    internal const string QueueDrainedNote = "叙事者停用，这些事件未发，已清空";
    internal const string TargetStandUpTitle = "站起";
    internal const string MobilityHintPrefix = "规格 mobility=";
    private readonly List<MapResidueBaseline> residueBaselines = [];
    private HashSet<Map>? baselineMaps;
    private HashSet<Thing>? baselineIndestructible;
    private int baselineTick;

    private readonly HashSet<Pawn> expectedDeaths = [];
    private readonly HashSet<Pawn> expectedHealingDrift = [];

    /// <summary>豁免死亡：该 pawn 在用例期间死亡不记现场指纹，规格靶不出 TARGET-DEATH。</summary>
    /// <remarks>只豁免死亡，倒地后站起仍上报；用例收尾或回收该 pawn 之前声明即生效；reason 为空或全是空白出 EMPTY-REASON 警告。</remarks>
    public void ExcuseDeath(Pawn pawn, string reason)
    {
        RequireDeclaration(pawn, reason, nameof(ExcuseDeath));
        expectedDeaths.Add(pawn);
    }

    /// <summary>豁免愈合：该 pawn 的伤势在本 phase 内下滑不记现场指纹。</summary>
    /// <remarks>用例收尾或回收该 pawn 之前声明即生效；reason 为空或全是空白出 EMPTY-REASON 警告。</remarks>
    public void ExcuseHealing(Pawn pawn, string reason)
    {
        RequireDeclaration(pawn, reason, nameof(ExcuseHealing));
        expectedHealingDrift.Add(pawn);
    }

    private static void RequireDeclaration(Pawn pawn, string? reason, string entry)
    {
        if (pawn == null)
        {
            throw new ArgumentNullException(nameof(pawn),
                RimRunnerLog.Prefix + " " + entry + " 须给 pawn");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            RimRunnerWarnings.Warn(RimRunnerWarningKey.EmptyReason, entry + "（" + pawn.LabelShort + "）未写 reason");
        }
    }

    private readonly List<RimRunnerFindingEntry> pendingLiveFingerprints = [];

    /// <summary>往本条的现场指纹追加一行，结果为 PASS 或 DONE 之外时才进报告。</summary>
    /// <remarks>须在 phase 返回前调；Defer 执行期间追加的行收不进本次报告。</remarks>
    public void AddFailureFingerprint(string line)
    {
        RejectInEnumeration("AddFailureFingerprint");
        pendingLiveFingerprints.Add(RimRunnerFindingEntry.Of(null, null, line));
    }

    internal void AddFailureFingerprint(string key, string? subject, string text)
    {
        RejectInEnumeration("AddFailureFingerprint");
        pendingLiveFingerprints.Add(RimRunnerFindingEntry.Of(key, subject, text));
    }

    internal IReadOnlyList<string> PendingLiveFingerprints => [.. pendingLiveFingerprints.Select(finding => finding.Line)];

    internal IReadOnlyList<RimRunnerFindingEntry> PendingLiveFingerprintEntries => pendingLiveFingerprints;

    internal static bool ShouldPublishFingerprints(string? status)
    {
        return status is not (RimRunnerOutcomes.Pass or RimRunnerOutcomes.Done);
    }

    private void PushFingerprint(RimRunnerFindingEntry finding)
    {
        InCase.pendingFingerprints.Add(finding);
    }

    private void PushFingerprint(string key, string? subject, string text) =>
        PushFingerprint(RimRunnerFindingEntry.Of(key, subject, text));

    internal RimRunnerScenarioPawnRoster SnapshotScenarioPawnRoster()
    {
        RimRunnerScenarioPawnRoster roster = new();
        HashSet<Pawn> dormant = [.. DormantPawnsOf(Map)];
        foreach (Pawn pawn in OnMapPawnsOf(Map))
        {
            if (pawn == null || IsAmbientWildlife(pawn) || pawn.ThingID == null)
            {
                continue;
            }

            (dormant.Contains(pawn) ? roster.Dormant : roster.Awake).Add(pawn.ThingID);
        }

        return roster;
    }

    private static readonly Dictionary<string, string> residuePawnsReported = new(StringComparer.Ordinal);

    internal static void ResetResiduePawnsForRun() => residuePawnsReported.Clear();

    internal static void OnGameLoaded(bool checkpointLoad)
    {
        if (!checkpointLoad)
        {
            residuePawnsReported.Clear();
        }
    }

    private void NoteResiduePawns(IEnumerable<Pawn> pawns)
    {
        foreach (Pawn pawn in pawns)
        {
            NoteResiduePawn(pawn, InCase.id);
        }
    }

    internal static void NoteResiduePawn(Pawn pawn, string caseId)
    {
        if (pawn.ThingID != null && !residuePawnsReported.ContainsKey(pawn.ThingID))
        {
            residuePawnsReported[pawn.ThingID] = caseId;
        }
    }

    internal void FingerprintContinuationPawnLeaks(RimRunnerScenarioPawnRoster? roster)
    {
        if (roster == null || roster.Awake.Count == 0 || Map == null)
        {
            return;
        }

        HashSet<Pawn> dormantNow = [.. DormantPawnsOf(Map)];
        List<Pawn> leaks = [];
        List<Pawn> woken = [];
        foreach (Pawn pawn in OnMapPawnsOf(Map))
        {
            if (pawn == null || IsAmbientWildlife(pawn))
            {
                continue;
            }

            if (pawn.ThingID != null && roster.Awake.Contains(pawn.ThingID))
            {
                continue;
            }

            if (IsRegistered(pawn) || IsHeldForCheckpoint(pawn))
            {
                continue;
            }

            if (pawn.ThingID != null && roster.Dormant.Contains(pawn.ThingID))
            {
                if (!dormantNow.Contains(pawn))
                {
                    woken.Add(pawn);
                }

                continue;
            }

            leaks.Add(pawn);
        }

        leaks = DropReportedElsewhere(leaks);
        woken = DropReportedElsewhere(woken);
        if (leaks.Count == 0 && woken.Count == 0)
        {
            return;
        }

        Dictionary<Pawn, Thing> spawners = PawnSpawnersOf(Map, leaks.Concat(woken).ToList());
        if (leaks.Count > 0)
        {
            AddResidueDiagnostic(ContinuationPrefix + leaks.Count + " 个" + ContinuationLeakTitle + "（"
                                 + Describe(leaks.Select(pawn => DescribeOrigin(pawn, spawners))) + "）",
                "漏清的 pawn 计入续段基线");
        }

        if (woken.Count > 0)
        {
            AddResidueDiagnostic(ContinuationPrefix + woken.Count + " 个" + ContinuationWokenTitle + "、此刻已醒的 pawn（"
                                 + Describe(woken.Select(pawn => DescribeOrigin(pawn, spawners))) + "）",
                DormantNote + "，已醒的 pawn 计入续段基线");
        }

        NoteResiduePawns(leaks.Concat(woken));
    }

    private List<Pawn> DropReportedElsewhere(List<Pawn> pawns)
    {
        List<Pawn> kept = [];
        List<string> elsewhere = [];
        foreach (Pawn pawn in pawns)
        {
            if (pawn.ThingID != null && residuePawnsReported.TryGetValue(pawn.ThingID, out string? owner)
                && owner != InCase.id)
            {
                elsewhere.Add(pawn.LabelShort + "[" + pawn.ThingID + "] 已在 " + owner + " 名下报残留");
                continue;
            }

            kept.Add(pawn);
        }

        if (elsewhere.Count > 0)
        {
            RimRunnerEngineLog.Info(InCase.id + " 续段起点图上仍有别的用例留下的 pawn："
                                    + string.Join("；", elsewhere));
        }

        return kept;
    }

    internal bool IsBaselineItem(Thing thing) => residueBaselines.Any(baseline => baseline.Items.Contains(thing));

    internal bool? PresentAtCaseStart(Map map, Thing thing)
    {
        MapResidueBaseline? baseline = residueBaselines.FirstOrDefault(entry => entry.Map == map);
        if (baseline == null)
        {
            return null;
        }

        if (thing is Pawn pawn)
        {
            return baseline.Pawns.Contains(pawn) || baseline.Dormant.Contains(pawn);
        }

        if (ThingRequestGroup.HaulableEver.Includes(thing.def))
        {
            return baseline.Items.Contains(thing);
        }

        if (ThingRequestGroup.BuildingArtificial.Includes(thing.def) && thing.def.destroyable)
        {
            return baseline.Buildings.Contains(thing);
        }

        return null;
    }

    internal string? BaselineLabelOf(Map map) => residueBaselines.FirstOrDefault(entry => entry.Map == map)?.Label;

    internal void BeginPhaseBaseline()
    {
        RimRunnerDamageRecorder.ResetPropagationTally();
        RimRunnerCaseWatch.BeginCase();
        CaptureResidueBaseline();
    }

    internal void CaptureResidueBaseline()
    {
        baselineTick = Find.TickManager?.TicksGame ?? 0;
        residueBaselines.Clear();
        if (Map != null)
        {
            CaptureMapResidueBaseline(Map, Map == RimRunnerIsolatedMap.Active ? RimRunnerIsolatedMap.LabelOf(0) : MainMapLabel);
        }

        baselineMaps = [];
        baselineIndestructible = [];
        foreach (Map map in Find.Maps)
        {
            CaptureIndestructibleBaseline(map);
        }
        foreach (Pawn pawn in pawns.Values)
        {
            if (pawn?.health == null)
            {
                continue;
            }

            RimRunnerCaseWatch.Register(pawn);
            RimRunnerCaseWatch.RegisterCandidatePawn(pawn);
        }
    }

    internal void ArmForeignDamage(bool firstPhase)
        => RimRunnerForeignDamage.BeginPhase(InCase.id, firstPhase, residueBaselines.SelectMany(ForeignDamageSources));

    private static IEnumerable<Thing> ForeignDamageSources(MapResidueBaseline baseline)
        => baseline.Pawns.Cast<Thing>().Concat(baseline.Dormant).Concat(baseline.Buildings);

    private void CaptureMapResidueBaseline(Map map, string label)
    {
        residueBaselines.Add(new MapResidueBaseline
        {
            Map = map,
            Label = label,
            Pawns = [.. AllPawnsOf(map)],
            Dormant = [.. DormantPawnsOf(map)],
            Items = [.. SpawnedItemsOf(map)],
            Buildings = [.. BuildingsOf(map)]
        });
    }

    private void CaptureIndestructibleBaseline(Map map)
    {
        if (map == null || map.Disposed || !baselineMaps!.Add(map))
        {
            return;
        }

        foreach (Thing thing in map.listerThings.AllThings)
        {
            if (thing?.def != null && !thing.def.destroyable)
            {
                baselineIndestructible!.Add(thing);
            }
        }
    }

    private void CaptureAcquiredMapBaseline(Map map, string label)
    {
        CaptureMapResidueBaseline(map, label);
        RimRunnerForeignDamage.AddPreCase(ForeignDamageSources(residueBaselines[^1]));
        CaptureIndestructibleBaseline(map);
    }

    internal void AppendLiveFingerprints()
    {
        foreach (RimRunnerFindingEntry finding in pendingLiveFingerprints)
        {
            PushFingerprint(finding);
        }

        AppendDamagePropagationFingerprint();
        AppendTargetSurvivalFingerprint();
        IReadOnlyCollection<string> readAfterBlock = RimRunnerNeedsFreeze.ReadAfterBlockThisCase;
        if (RimRunnerContext.AmbientPhase == RimRunnerPhase.Case && readAfterBlock.Count > 0)
        {
            List<string> reads = RimRunnerContractGates.NeedReadsOf(InCase.id);
            if (reads.Count > 0)
            {
                IEnumerable<string> names = RimRunnerNeedsFreeze.FrozenPawnsThisCase
                    .Where(pawn => readAfterBlock.Contains(pawn.ThingID)).Select(pawn => pawn.LabelShort)
                    .DefaultIfEmpty(string.Join("、", readAfterBlock));
                RimRunnerWarnings.Warn(RimRunnerWarningKey.NeedsFrozenRead,
                    "用例 " + InCase.id + " 冻结的需求已拦下过自然推进，之后又读需求值（" + string.Join("、", names)
                    + "），闭包读需求 / 心情值：" + string.Join("、", reads)
                    + "。冻结期内这些值不随 tick 变，拿来断涨落恒真");
            }
        }
        foreach (RimRunnerPawns.SpecTarget target in RimRunnerPawns.SpecTargetsThisCase) JudgeSpecTargetDeath(target);
        foreach (string line in RimRunnerCaseWatch.AwakeLost)
        {
            RimRunnerSentinel.Warn(RimRunnerWarningKey.TargetAwakeLost,
                "行动档 AwakeImmobile 的靶在仍被钉住时失去清醒；清醒只在施加时核验，之后意识随伤与疼痛降", line);
        }
        foreach (string line in RimRunnerForeignDamage.Lines)
        {
            RimRunnerSentinel.Warn(RimRunnerWarningKey.TargetForeignDamage,
                "规格靶受本用例首段开始执行前已在图上的 pawn 或建筑所伤，靶上的读数混进现场的伤", line);
        }
        foreach (RimRunnerIlScan.Stats stats in RimRunnerIlScan.TakeRegisteredThisCase())
        {
            if (stats.MethodsFailed > 0 && !stats.ReadGated)
            {
                RimRunnerSentinel.Warn(RimRunnerWarningKey.IlScanUngated,
                    "用例读 IL 有读取失败，这份扫描统计未过 RimRunnerIlScanGate。"
                    + "据这份统计下结论前先过 AssertReadClean 或 AssertScanIntegrity",
                    "扫描 " + stats.MethodsSeen + " 个方法、有体 " + stats.MethodsWithBody + "、读取失败 " + stats.MethodsFailed);
            }
        }
        AppendHealingDriftFingerprint();
        WriteLiveFingerprints(PushFingerprint);
    }

    internal IReadOnlyList<RimRunnerFindingEntry> PeekLiveFingerprints()
    {
        List<RimRunnerFindingEntry> list = [];
        WriteLiveFingerprints(list.Add);
        return list;
    }

    private void WriteLiveFingerprints(Action<RimRunnerFindingEntry> write)
    {
        AppendUnnormalizedPawnFingerprint(write);
        foreach (RimRunnerFindingEntry finding in RimRunnerPawns.RolledXenotypeFindingsThisCase) write(finding);
        AppendProductFingerprints(write);
    }

    private Pawn? FindSpawnedPawn(string id)
        => SpawnedPawnsOf(Map).Concat(RimRunnerIsolatedMap.LeasedMaps.Where(map => map != Map).SelectMany(SpawnedPawnsOf))
            .FirstOrDefault(pawn => pawn.ThingID == id);

    private void AppendUnnormalizedPawnFingerprint(Action<RimRunnerFindingEntry> write)
    {
        foreach (string id in RimRunnerPawns.GeneratedThisCase)
        {
            Pawn? pawn = FindSpawnedPawn(id);
            if (pawn == null || RimRunnerPawns.IsRaidMember(pawn)) continue;
            if (RimRunnerPawns.TryGetSpecTarget(pawn, out RimRunnerPawns.SpecTarget? target))
            {
                AppendSpecTargetDrift(pawn, target, write);
                continue;
            }
            List<string> parts = [];
            string[] randomTraits = pawn.story?.traits?.allTraits.Where(t => t.sourceGene == null).Select(t => t.def.defName).ToArray() ?? [];
            if (randomTraits.Length > 0) parts.Add("trait " + string.Join(",", randomTraits));
            if (!RimRunnerPawns.TryFixedAgeTicks(pawn.kindDef, pawn.def, out long fixedAge, out _)
                || Math.Abs(pawn.ageTracker.AgeBiologicalTicks - fixedAge) > GenDate.TicksPerQuadrum)
                parts.Add("年龄 " + pawn.ageTracker.AgeBiologicalYearsFloat.ToString("0.0"));
            int worn = pawn.apparel?.WornApparelCount ?? 0;
            if (worn > 0) parts.Add("装备 " + worn + " 件");
            if (parts.Count == 0) continue;
            write(RimRunnerFindingEntry.Of(RimRunnerFindingKey.PawnUnnormalized, pawn.LabelShort,
                "pawn 未归一化：" + string.Join(" · ", parts)));
        }
    }

    private static void AppendSpecTargetDrift(Pawn pawn, RimRunnerPawns.SpecTarget target, Action<RimRunnerFindingEntry> write)
    {
        if (target.Spec is not RimRunnerTargetSpec spec) return;
        List<string> parts = [];
        foreach (SkillDef skill in new[] { SkillDefOf.Melee, SkillDefOf.Shooting })
        {
            SkillRecord? record = pawn.skills?.GetSkill(skill);
            if (record != null && !record.TotallyDisabled) parts.Add(skill.defName + " " + record.Level + "（violence=" + spec.Violence + "）");
        }
        if (target.GeneratedHediffs.Length > 0)
            parts.Add("生成期 hediff " + string.Join(",", target.GeneratedHediffs) + "（generation 未含 ClearHediffs）");
        if (!pawn.RaceProps.Humanlike && (spec.Generation & RimRunnerTargetGeneration.FixedAge) == 0
            && pawn.ageTracker.CurLifeStage != pawn.RaceProps.lifeStageAges[^1].def)
            parts.Add("生命阶段 " + pawn.ageTracker.CurLifeStage.defName + "（generation 未含 FixedAge）");
        if (parts.Count == 0) return;
        write(RimRunnerFindingEntry.Of(RimRunnerFindingKey.SpecTargetDrift, pawn.LabelShort,
            StanceLabel(spec.Stance) + "带生成随机：" + string.Join(" · ", parts)));
    }

    private static string StanceLabel(RimRunnerTargetStance stance)
    {
        return stance switch
        {
            RimRunnerTargetStance.Hostile => "敌对靶",
            RimRunnerTargetStance.Neutral => "中立靶",
            _ => "玩家方靶",
        };
    }

    private void AppendProductFingerprints(Action<RimRunnerFindingEntry> write)
    {
        foreach (RimRunnerFindingEntry finding in RimRunnerProduct.Capabilities.LiveFingerprintFindings(this)) write(finding);
    }

    private void AppendTargetSurvivalFingerprint()
    {
        foreach (RimRunnerCaseWatch.WatchedTarget target
                 in RimRunnerCaseWatch.All)
        {
            if (TargetSurvivalFingerprint(target) is { } finding)
            {
                PushFingerprint(finding);
            }
        }
    }

    private RimRunnerFindingEntry? TargetSurvivalFingerprint(RimRunnerCaseWatch.WatchedTarget target)
    {
        Pawn? pawn = target.Pawn;
        if (pawn == null)
        {
            return null;
        }

        bool died = pawn.Dead;
        bool stoodUp = target.EverDowned && !pawn.Dead && !pawn.Downed;
        if (!died && !stoodUp)
        {
            return null;
        }

        if (died && expectedDeaths.Contains(pawn))
        {
            return null;
        }

        string head = "靶在用例期间" + (died ? "死亡" : TargetStandUpTitle);
        if (RimRunnerPawns.TryGetSpecTarget(pawn, out RimRunnerPawns.SpecTarget? specTarget)
            && specTarget.Spec is RimRunnerTargetSpecBase spec)
        {
            return died ? null : RimRunnerFindingEntry.Of(RimRunnerFindingKey.TargetSurvival, pawn.LabelShort,
                head + "；" + SpecSurvivalHint(spec, died: false));
        }

        return RimRunnerFindingEntry.Of(RimRunnerFindingKey.TargetSurvival, pawn.LabelShort, head);
    }

    private void JudgeSpecTargetDeath(RimRunnerPawns.SpecTarget target)
    {
        if (target.DeathJudged || target.Pawn == null) return;
        target.DeathJudged = true;
        if (target.Spec.Death.Excused || !target.Pawn.Dead || expectedDeaths.Contains(target.Pawn)) return;
        RimRunnerSentinel.Warn(RimRunnerWarningKey.TargetDeath,
            "靶规格写的 death 是 Forbidden，靶在用例期间死亡",
            target.Pawn.LabelShort + "（" + SpecSurvivalHint(target.Spec, died: true) + "）");
    }

    private List<RimRunnerPawns.CarriedSpecTarget>? carriedSpecTargets;

    internal void SetCarriedSpecTargets(List<RimRunnerPawns.CarriedSpecTarget>? carried)
    {
        carriedSpecTargets = carried;
    }

    private void AdoptCarriedSpecTargets()
    {
        if (carriedSpecTargets == null || carriedSpecTargets.Count == 0)
        {
            return;
        }

        foreach (RimRunnerPawns.CarriedSpecTarget carried in carriedSpecTargets)
        {
            Pawn? pawn = pawns.Values.FirstOrDefault(p => p?.ThingID == carried.ThingId);
            if (pawn == null) continue;
            RimRunnerPawns.RestoreSpecTarget(pawn, carried.Spec, carried.GeneratedHediffs);
            if (carried.Spec.Death.Excused) expectedDeaths.Add(pawn);
        }

        carriedSpecTargets.Clear();
    }

    private void ReplayCarriedExcusedDeaths()
    {
        if (checkpointState == null)
        {
            return;
        }

        foreach (Pawn pawn in pawns.Values)
        {
            if (pawn?.ThingID != null && checkpointState.ContainsKey(ExcusedDeathStateKeyPrefix + pawn.ThingID))
            {
                expectedDeaths.Add(pawn);
            }
        }
    }

    private void TakeHandedOffSpecTargets()
    {
        if (!CarriesRegisteredToNextPhase || carriedSpecTargets == null || InCase.endedByClaimedFault)
        {
            return;
        }

        foreach (RimRunnerPawns.SpecTarget target in RimRunnerPawns.SpecTargetsThisCase)
        {
            if (target.Pawn?.ThingID == null || target.Pawn.Dead || !IsRegistered(target.Pawn)) continue;
            carriedSpecTargets.Add(new RimRunnerPawns.CarriedSpecTarget
            {
                ThingId = target.Pawn.ThingID,
                Spec = target.Spec,
                GeneratedHediffs = target.GeneratedHediffs
            });
        }
    }

    private static string SpecSurvivalHint(RimRunnerTargetSpecBase spec, bool died)
    {
        if (!died)
        {
            return MobilityHintPrefix + spec.Mobility;
        }

        if (spec is RimRunnerDummySpec dummy)
        {
            return "规格 kind=" + dummy.Kind.defName + " healthScale=" + dummy.HealthScale.ToString("0.##")
                   + " damage=" + dummy.Damage + " deathOnDowned=" + dummy.DeathOnDowned;
        }

        return "规格 kind=" + spec.Kind.defName + "（baseHealthScale " + (spec.Kind.RaceProps?.baseHealthScale ?? 1f).ToString("0.##") + "）"
               + " damage=" + spec.Damage + " deathOnDowned=" + spec.DeathOnDowned;
    }

    private void AppendHealingDriftFingerprint()
    {
        foreach (KeyValuePair<Hediff, float> pair in RimRunnerCaseWatch.InjuryPeaks)
        {
            if (HealingDriftFingerprint(pair) is { } finding)
            {
                PushFingerprint(finding);
            }
        }
    }

    private RimRunnerFindingEntry? HealingDriftFingerprint(KeyValuePair<Hediff, float> pair)
    {
        Hediff hediff = pair.Key;
        if (hediff?.pawn == null || expectedHealingDrift.Contains(hediff.pawn)
                                 || hediff.Severity >= pair.Value)
        {
            return null;
        }

        int elapsed = (Find.TickManager?.TicksGame ?? baselineTick) - baselineTick;
        return RimRunnerFindingEntry.Of(RimRunnerFindingKey.HealingDrift, hediff.pawn.LabelShort,
            (hediff.Part?.LabelCap ?? "全身") + " severity 在本 phase 内 "
            + pair.Value.ToString("0.###") + "→" + hediff.Severity.ToString("0.###")
            + "（" + elapsed + " tick）");
    }

    private void ReleaseWatchedPawn(Pawn pawn)
    {
        List<RimRunnerCaseWatch.WatchedTarget> targets = [];
        List<KeyValuePair<Hediff, float>> peaks = [];
        RimRunnerCaseWatch.Release(pawn, targets, peaks);
        if (RimRunnerPawns.TryGetSpecTarget(pawn, out RimRunnerPawns.SpecTarget? specTarget))
        {
            JudgeSpecTargetDeath(specTarget);
            specTarget.Pawn = null;
        }

        foreach (RimRunnerCaseWatch.WatchedTarget target in targets)
        {
            if (TargetSurvivalFingerprint(target) is { } finding)
            {
                pendingLiveFingerprints.Add(finding);
            }
        }

        foreach (KeyValuePair<Hediff, float> pair in peaks)
        {
            if (HealingDriftFingerprint(pair) is { } finding)
            {
                pendingLiveFingerprints.Add(finding);
            }
        }

        expectedDeaths.Remove(pawn);
        expectedHealingDrift.Remove(pawn);
    }

    private void AppendDamagePropagationFingerprint()
    {
        if (!RimRunnerDamageRecorder.HasWindow)
        {
            return;
        }

        foreach (KeyValuePair<Pawn, int> pair in RimRunnerDamageRecorder.PropagatedHits)
        {
            PushFingerprint(RimRunnerFindingKey.DamagePropagation, pair.Key?.LabelShort ?? "<null>",
                "本用例对该 pawn 落的 " + pair.Value + " 记伤害未关内传播");
        }
    }

    internal void AppendResidueFingerprints()
    {
        foreach (MapResidueBaseline baseline in residueBaselines)
        {
            AppendMapResidue(baseline);
        }

        AppendQueuedIncidentResidue(InCase.id);

        AppendOrphanFingerprints(InCase.id);

        MapPortal generating = PocketMapUtility.currentlyGeneratingPortal;
        if (generating != null)
        {
            RimRunnerSentinel.Warn(RimRunnerWarningKey.PortalResidue, InCase.id + " 收尾后 PocketMapUtility.currentlyGeneratingPortal 未还原："
                      + "引擎已置空",
                generating.LabelShort);
            PocketMapUtility.currentlyGeneratingPortal = null;
        }

        List<Thing> leftoverIndestructible = baselineMaps == null
            ? []
            : Find.Maps
                .Where(map => map != null && !map.Disposed && baselineMaps.Contains(map))
                .SelectMany(map => map.listerThings.AllThings)
                .Where(thing => thing != null && thing.Spawned && thing.def != null && !thing.def.destroyable
                                && !baselineIndestructible!.Contains(thing))
                .Where(thing => !IsHeldForCheckpoint(thing))
                .ToList();
        if (leftoverIndestructible.Count > 0)
        {
            for (int i = 0; i < leftoverIndestructible.Count; i++)
            {
                Thing thing = leftoverIndestructible[i];
                RimRunnerSentinel.Warn(RimRunnerWarningKey.IndestructibleResidue, InCase.id + " 收尾后仍有不可摧毁物留在图上："
                          + "Destroy 对它们只输出错误级日志、对象仍留在图上，引擎已移出图面",
                    thing.LabelShort);
                if (thing.Spawned)
                {
                    thing.DeSpawn();
                }
            }
        }

        RimRunnerLetterProbe.ReportDangling(InCase.id);
    }

    private void AppendMapResidue(MapResidueBaseline baseline)
    {
        List<Pawn> offBaseline = OnMapPawnsOf(baseline.Map)
            .Where(pawn => !baseline.Pawns.Contains(pawn))
            .Where(pawn => !IsAmbientWildlife(pawn))
            .Where(pawn => !IsHeldForCheckpoint(pawn)).ToList();
        HashSet<Pawn> dormantNow = [.. DormantPawnsOf(baseline.Map)];
        List<Pawn> mapResidue = offBaseline.Where(pawn => !baseline.Dormant.Contains(pawn)).ToList();
        List<Pawn> wokenResidue = offBaseline
            .Where(pawn => baseline.Dormant.Contains(pawn) && !dormantNow.Contains(pawn)).ToList();
        if (mapResidue.Count > 0 || wokenResidue.Count > 0)
        {
            Dictionary<Pawn, Thing> spawners = PawnSpawnersOf(baseline.Map, mapResidue.Concat(wokenResidue).ToList());
            if (mapResidue.Count > 0)
            {
                AddResidueDiagnostic(baseline.Label + " " + mapResidue.Count + " 个" + PawnResidueTitle + "（"
                                     + Describe(mapResidue.Select(pawn => DescribeOrigin(pawn, spawners))) + "）");
            }

            if (wokenResidue.Count > 0)
            {
                AddResidueDiagnostic(baseline.Label + " " + wokenResidue.Count + " 个" + WokenResidueTitle + "、本用例期间醒来的 pawn（"
                                     + Describe(wokenResidue.Select(pawn => DescribeOrigin(pawn, spawners))) + "）",
                    DormantNote);
            }

            NoteResiduePawns(mapResidue.Concat(wokenResidue));
        }

        List<Thing> itemResidue = SpawnedItemsOf(baseline.Map)
            .Where(thing => !baseline.Items.Contains(thing) && !IsAmbientWildlifeCorpse(thing))
            .Where(thing => !IsHeldForCheckpoint(thing))
            .ToList();
        if (itemResidue.Count > 0)
        {
            string where = baseline.Label == MainMapLabel ? "" : baseline.Label + " ";
            AddResidueDiagnostic(where + itemResidue.Count + " 件" + ItemResidueTitle + "（"
                                 + Describe(itemResidue.Select(DescribeItem)) + "）"
                                 + DescribeCandidateProducers(baseline.Map, itemResidue)
                                 + HandwrittenThingIdHint(itemResidue));
        }

        List<Thing> buildingResidue = BuildingsOf(baseline.Map)
            .Where(thing => !baseline.Buildings.Contains(thing) && !IsHeldForCheckpoint(thing))
            .ToList();
        if (buildingResidue.Count > 0)
        {
            AddResidueDiagnostic(baseline.Label + " " + buildingResidue.Count + " " + BuildingResidueTitle + "（"
                                 + Describe(buildingResidue.Select(DescribeBuilding)) + "）"
                                 + HandwrittenThingIdHint(buildingResidue));
        }

        List<Fire> fires = FiresOf(baseline.Map);
        if (fires.Count > 0)
        {
            AddResidueDiagnostic(baseline.Label + " " + fires.Count + " " + FireResidueTitle + "（"
                                 + Describe(fires.Select(fire => fire.parent != null
                                     ? "附着于 " + fire.parent.LabelShort
                                     : "地面 " + fire.Position)) + "），已扑灭");
            foreach (Fire fire in fires)
            {
                if (!fire.Destroyed)
                {
                    fire.Destroy();
                }
            }
        }
    }

    private static bool IsAmbientWildlife(Pawn pawn)
    {
        return pawn.Faction == null && pawn.RaceProps != null && pawn.RaceProps.Animal;
    }

    private static bool IsAmbientWildlifeCorpse(Thing thing)
    {
        return thing is Corpse corpse && corpse.InnerPawn != null && IsAmbientWildlife(corpse.InnerPawn);
    }

    private void AppendOrphanFingerprints(string owner)
    {
        List<Pawn> orphanResidue = RimRunnerOrphanSweep.Collect();
        if (orphanResidue.Count > 0)
        {
            foreach (Pawn orphan in orphanResidue)
            {
                RimRunnerSentinel.Warn(RimRunnerWarningKey.OrphanPawn, owner + " 收尾后仍有孤儿 pawn："
                          + "未销毁，但不在地图上、不在世界池、不在容器内，仍被地图上、容器内或世界池里某个 pawn 的 directRelations，"
                          + "或某条 RelationshipRecord 的 References 引用。GeneratePawn 连带的亲属、"
                          + "奖励、艺术品、书经 TaleData_Pawn.GenerateRandom 生成的 pawn 都属此类，引擎已当场清理",
                    orphan.LabelShort + "｜" + RimRunnerOrphanSweep.DescribeCarriers(orphan));
            }

            RimRunnerOrphanSweep.SweepAtCaseEnd();
        }
    }

    private void AppendLateTeardownFingerprints(string owner)
    {
        AppendQueuedIncidentResidue(owner);
        AppendOrphanFingerprints(owner);
        RimRunnerLetterProbe.ReportDangling(owner);
    }

    private void AppendQueuedIncidentResidue(string owner)
    {
        List<RimRunnerStorytellerQuiesce.ClearedIncident> queuedIncidents = RimRunnerStorytellerQuiesce.DrainQueued(owner);
        if (queuedIncidents.Count > 0)
        {
            AddResidueDiagnostic(QueueResidueTitle + " " + queuedIncidents.Count + " 条待发事件（"
                + Describe(queuedIncidents.Select(incident => incident.Text(owner))) + "）",
                QueueDrainedNote);
        }
    }

    internal void AppendBlockedBreakDiagnostic()
    {
        if (!RimRunnerHediffBreakBlock.RegisteredThisSegment && RimRunnerHediffBreakBlock.CancelledCount == 0)
        {
            return;
        }

        AddEngineDiagnostic(RimRunnerDiagnosticKind.Measurement, "hediff 阶段崩溃屏蔽",
            "掷中后取消发起 " + RimRunnerHediffBreakBlock.CancelledCount + " 次");
    }

    private string HandwrittenThingIdHint(IEnumerable<Thing> residue)
    {
        if (checkpointState == null)
        {
            return "";
        }

        int hits = residue.Count(thing => thing?.ThingID != null && HandwrittenStateHolds(thing.ThingID));
        return hits == 0
            ? ""
            : "；其中 " + hits + " 件的 ThingID 写进 checkpoint 状态但未登记交棒";
    }

    private bool HandwrittenStateHolds(string thingId)
    {
        foreach (KeyValuePair<string, string> pair in checkpointState!)
        {
            if (!pair.Key.StartsWith("__", StringComparison.Ordinal)
                && string.Equals(pair.Value, thingId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal const string ResidueText = "本用例结束后仍残留";

    private void AddResidueDiagnostic(string what, string note = "")
    {
        string suffix = string.IsNullOrEmpty(note) ? "" : "；" + note;
        PushFingerprint(RimRunnerFindingKey.Residue, what, ResidueText + suffix);
        InCase.pendingResidue.Add(what + suffix);
    }

    private void AddComponentNotes(IEnumerable<string> notes)
    {
        foreach (string note in notes)
        {
            AddResidueDiagnostic(note, "写入方不是被测产品，不回档");
        }
    }

    internal void AddWorldNotes(IEnumerable<string> notes, string batch)
    {
        foreach (string note in notes)
        {
            RimRunnerEngineLog.Info(InCase.id + " " + batch + "世界层与开始执行前不匹配：" + note);
            PushFingerprint(RimRunnerFindingKey.WorldDrift, null, batch + "世界层与开始执行前不匹配：" + note);
        }
    }

    private void AppendWindowFingerprints()
    {
        RimRunnerWindowWatch.WriteCaseLines((window, text) => PushFingerprint(RimRunnerFindingKey.WindowDrift, window, text), (what, tail) =>
        {
            PushFingerprint(RimRunnerFindingKey.Residue, what, ResidueText + tail);
            InCase.pendingResidue.Add(what + tail);
        }, close: true);
    }

    internal IReadOnlyList<string> PeekResidueSubjects(bool close = false)
    {
        List<string> list = [];
        RimRunnerWindowWatch.WriteCaseLines((_, _) => { }, (what, _) => list.Add(what), close);
        return list;
    }

    internal IReadOnlyList<RimRunnerWindowWatch.CasePush> PeekCasePushes() => RimRunnerWindowWatch.PeekCasePushes();

    private string DescribeOrigin(Pawn pawn, Dictionary<Pawn, Thing> spawners)
    {
        string origin = pawn.LabelShort + "[" + (pawn.kindDef?.defName ?? "<kind?>") + "/"
                        + (pawn.Faction?.def?.defName ?? "无派系") + "/" + pawn.ThingID;
        LordJob? lordJob = pawn.GetLord()?.LordJob;
        if (lordJob != null)
        {
            origin += "/" + lordJob.GetType().Name;
        }

        if (spawners.TryGetValue(pawn, out Thing? spawner))
        {
            origin += "/产自 " + DescribeAt(spawner);
        }

        string? arrival = RimRunnerArrivalTag.Describe(pawn, InCase.id);
        if (arrival != null)
        {
            origin += "/" + arrival;
        }

        return origin + "]";
    }

    private string DescribeItem(Thing thing)
    {
        string? arrival = RimRunnerArrivalTag.Describe(thing, InCase.id);
        return arrival == null ? thing.LabelShort : thing.LabelShort + "[" + arrival + "]";
    }

    private static Dictionary<Pawn, Thing> PawnSpawnersOf(Map map, List<Pawn> residue)
    {
        var spawners = new Dictionary<Pawn, Thing>();
        var wanted = new HashSet<Pawn>(residue);
        foreach (Thing thing in map.listerThings.AllThings)
        {
            List<Pawn>? spawned = (thing as ThingWithComps)?.GetComp<CompSpawnerPawn>()?.spawnedPawns;
            if (spawned == null)
            {
                continue;
            }

            foreach (Pawn pawn in spawned)
            {
                if (pawn != null && wanted.Contains(pawn))
                {
                    spawners[pawn] = thing;
                }
            }
        }

        return spawners;
    }

    private static string DescribeCandidateProducers(Map map, List<Thing> residue)
    {
        var residueDefs = new HashSet<ThingDef>(residue.Select(thing => thing.def));
        var producers = new List<string>();
        foreach (Thing thing in map.listerThings.AllThings)
        {
            if (thing is not ThingWithComps withComps)
            {
                continue;
            }

            foreach (CompSpawner spawner in withComps.GetComps<CompSpawner>())
            {
                ThingDef? product = spawner.PropsSpawner?.thingToSpawn;
                if (product != null && residueDefs.Contains(product))
                {
                    producers.Add(DescribeAt(thing) + "→" + product.defName);
                }
            }
        }

        return producers.Count == 0 ? "" : "；图上同 def 候选产出方：" + Describe(producers);
    }

    private static string DescribeBuilding(Thing thing)
    {
        return thing.LabelShort + "[" + (thing.Faction?.def?.defName ?? "无派系") + "/" + DescribeAt(thing) + "]";
    }

    private static string DescribeAt(Thing thing)
    {
        return thing.def.defName + "@(" + thing.Position.x + "," + thing.Position.z + ")";
    }

    private static string Describe(IEnumerable<string> names) => string.Join("、", names);

    private static IEnumerable<Pawn> SpawnedPawnsOf(Map? map)
    {
        return map == null || map.Disposed
            ? Enumerable.Empty<Pawn>()
            : map.mapPawns.AllPawnsSpawned.ToList();
    }

    private static IEnumerable<Pawn> AllPawnsOf(Map? map)
    {
        return map == null || map.Disposed
            ? Enumerable.Empty<Pawn>()
            : map.mapPawns.AllPawns.ToList();
    }

    private static IEnumerable<Pawn> OnMapPawnsOf(Map? map)
    {
        return map == null || map.Disposed
            ? Enumerable.Empty<Pawn>()
            : map.listerThings.ThingsInGroup(ThingRequestGroup.Pawn).OfType<Pawn>().ToList();
    }

    private static IEnumerable<Pawn> DormantPawnsOf(Map? map)
    {
        if (map == null || map.Disposed)
        {
            return [];
        }

        HashSet<Pawn> registered = [.. map.mapPawns.AllPawnsSpawned];
        return OnMapPawnsOf(map).Where(pawn => !registered.Contains(pawn)).ToList();
    }

    internal const string DormantNote = "非本用例生成";

    private static IEnumerable<Thing> BuildingsOf(Map? map)
    {
        return map == null || map.Disposed
            ? Enumerable.Empty<Thing>()
            : map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial)
                .Where(thing => thing.def.destroyable).ToList();
    }

    private static List<Fire> FiresOf(Map? map)
    {
        return map == null || map.Disposed
            ? []
            : map.listerThings.ThingsOfDef(ThingDefOf.Fire).OfType<Fire>().ToList();
    }

    private static IEnumerable<Thing> SpawnedItemsOf(Map? map)
    {
        return map == null || map.Disposed
            ? Enumerable.Empty<Thing>()
            : map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).ToList();
    }
}
