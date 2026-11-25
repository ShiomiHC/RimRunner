using RimWorld.Planet;
using Verse.AI;
using Verse.AI.Group;

namespace RimRunner;

/// <summary>靶的暴力档：会不会打回来。</summary>
public enum RimRunnerPawnViolenceProfile
{
    /// <summary>暴力不限：沿用生成结果，vanilla 暴力能力保持原样。</summary>
    Unrestricted,

    /// <summary>可攻击：只核验 pawn 有暴力能力，没有即抛。</summary>
    Capable,

    /// <summary>无暴力能力：成年背景换成 vanilla 的 Missionary99，暴力类工作禁用。</summary>
    /// <remarks>无 story 的 pawn 施加即抛。</remarks>
    Incapable
}

/// <summary>靶的行动档：会不会走开。</summary>
public enum RimRunnerPawnMobilityProfile
{
    /// <summary>可动：行动能力沿用生成结果。</summary>
    Mobile,

    /// <summary>倒地、昏迷且不能移动：挂 vanilla 的 Anesthetic。</summary>
    Anesthetized,

    /// <summary>倒地、清醒且不能移动：挂钉住 hediff PinHediff。</summary>
    /// <remarks>清醒只在施加时核验；之后意识随伤与疼痛照降，钉住 hediff 在身期间失去清醒即在用例末出 TARGET-AWAKE-LOST 警告。</remarks>
    AwakeImmobile,

    /// <summary>眩晕但不倒地：靶稳站格心，AI 无法拉走。</summary>
    /// <remarks>不改承伤：挨打照常结算，打到倒地即离开本档。要靶全程站立，靶规格写 damage=Absorbed，或直接调用时另调 RimRunnerInvulnerability.AddAbsorbed。</remarks>
    Stunned
}

/// <summary>pawn 夹具原语：隔离生成、确定性归一化与销毁生命周期。</summary>
public static class RimRunnerPawns
{
    internal const long FixedAdultAgeTicks = 25L * GenDate.TicksPerYear;

    private static readonly HashSet<string> generatedThisCase = new(StringComparer.Ordinal);

    /// <summary>本 phase 经本类生成的 pawn 的 ThingID。</summary>
    /// <remarks>每条用例的每个 phase 开始时清空，交棒的规格靶由 RimRunnerContext 重登。</remarks>
    public static IReadOnlyCollection<string> GeneratedThisCase => generatedThisCase;

    /// <summary>钉住 hediff 的 defName。</summary>
    public const string PinHediffName = "RimRunner_Pinned";

    /// <summary>AwakeImmobile 与 SubdueAndPin 挂的钉住 hediff。</summary>
    /// <remarks>只锁 Moving（封顶 0.1，低于能动门槛即倒地）与爬行；无病程，存在期间不自愈、不到期。</remarks>
    public static HediffDef PinHediff => DefDatabase<HediffDef>.GetNamed(PinHediffName);

    /// <summary>该 pawn 是否本 phase 经本类生成。</summary>
    public static bool IsGeneratedThisCase(Pawn? pawn)
        => pawn?.ThingID != null && generatedThisCase.Contains(pawn.ThingID);

    private static readonly Dictionary<string, SpecTarget> specTargetsThisCase = new(StringComparer.Ordinal);

    internal sealed class SpecTarget
    {
        internal required RimRunnerTargetSpecBase Spec;

        internal required string[] GeneratedHediffs;

        internal required Pawn? Pawn;

        internal bool DeathJudged;
    }

    internal sealed class CarriedSpecTarget
    {
        internal required string ThingId;
        internal required RimRunnerTargetSpecBase Spec;
        internal required string[] GeneratedHediffs;
    }

    internal static void RestoreSpecTarget(Pawn pawn, RimRunnerTargetSpecBase spec, string[] generatedHediffs)
    {
        specTargetsThisCase[pawn.ThingID] = new SpecTarget
        {
            Spec = spec,
            Pawn = pawn,
            GeneratedHediffs = generatedHediffs
        };
        generatedThisCase.Add(pawn.ThingID);
    }

    internal static void MarkSpecTarget(Pawn pawn, RimRunnerTargetSpecBase spec)
        => specTargetsThisCase[pawn.ThingID] = new SpecTarget
        {
            Spec = spec,
            Pawn = pawn,
            GeneratedHediffs = pawn.health?.hediffSet?.hediffs.Select(h => h.def.defName).ToArray() ?? []
        };

    internal static bool TryGetSpecTarget(Pawn? pawn, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SpecTarget? target)
    {
        target = null;
        return pawn?.ThingID != null && specTargetsThisCase.TryGetValue(pawn.ThingID, out target);
    }

    internal static IEnumerable<SpecTarget> SpecTargetsThisCase => specTargetsThisCase.Values;

    private static readonly HashSet<string> raidMembersThisCase = new(StringComparer.Ordinal);

    internal static void MarkRaidMember(Pawn pawn) => raidMembersThisCase.Add(pawn.ThingID);

    internal static bool IsRaidMember(Pawn? pawn)
        => pawn?.ThingID != null && raidMembersThisCase.Contains(pawn.ThingID);

    private static readonly List<RolledXenotype> rolledXenotypesThisCase = [];

    private sealed class RolledXenotype(PawnKindDef kind, string xenotype, Pawn pawn)
    {
        internal readonly PawnKindDef Kind = kind;
        internal readonly string Xenotype = xenotype;
        internal Pawn? Pawn = pawn;
        internal string? Label;
    }

    internal static IEnumerable<RimRunnerFindingEntry> RolledXenotypeFindingsThisCase
        => rolledXenotypesThisCase.Select(r => XenotypeFinding(r.Kind, r.Xenotype, r.Pawn?.LabelShort ?? r.Label!));

    internal static int RolledXenotypeCountThisCase => rolledXenotypesThisCase.Count;

    private static void NoteRolledXenotype(PawnKindDef kind, Faction? faction, bool mustBeCapableOfViolence, Pawn pawn)
    {
        if (RolledXenotypeName(kind, faction, mustBeCapableOfViolence, pawn) is { } name)
            rolledXenotypesThisCase.Add(new RolledXenotype(kind, name, pawn));
    }

    private static void ReleaseRolledXenotype(Pawn pawn)
    {
        foreach (RolledXenotype rolled in rolledXenotypesThisCase)
        {
            if (rolled.Pawn == pawn)
            {
                rolled.Label = pawn.LabelShort;
                rolled.Pawn = null;
            }
        }
    }

    internal static RimRunnerFindingEntry? RolledXenotypeFinding(PawnKindDef kind, Pawn pawn)
        => RolledXenotypeFinding(kind, pawn.Faction, mustBeCapableOfViolence: false, pawn);

    internal static RimRunnerFindingEntry? RolledXenotypeFinding(PawnKindDef kind, Faction? faction,
        bool mustBeCapableOfViolence, Pawn pawn)
        => RolledXenotypeName(kind, faction, mustBeCapableOfViolence, pawn) is { } name
            ? XenotypeFinding(kind, name, pawn.LabelShort)
            : null;

    private static RimRunnerFindingEntry XenotypeFinding(PawnKindDef kind, string xenotype, string subject)
        => RimRunnerFindingEntry.Of(RimRunnerFindingKey.RolledXenotype, subject,
            "kind " + kind.defName + " 的异种是生成时掷出的 " + xenotype + "（未强制异种）");

    internal static string? RolledXenotypeLine(PawnKindDef kind, Pawn pawn) => RolledXenotypeFinding(kind, pawn)?.Line;

    internal static string? RolledXenotypeLine(PawnKindDef kind, Faction? faction, bool mustBeCapableOfViolence, Pawn pawn)
        => RolledXenotypeFinding(kind, faction, mustBeCapableOfViolence, pawn)?.Line;

    private static string? RolledXenotypeName(PawnKindDef kind, Faction? faction, bool mustBeCapableOfViolence, Pawn pawn)
    {
        if (!ModsConfig.BiotechActive || pawn.genes == null || !kind.RaceProps.Humanlike) return null;
        XenotypeDef? actual = pawn.genes.Xenotype;
        string? custom = pawn.genes.CustomXenotype?.name;
        XenotypeDef? sole = SoleDeterministicXenotype(kind, faction, mustBeCapableOfViolence);
        if (custom == null && sole != null && actual == sole) return null;
        return custom ?? actual?.defName;
    }

    private static XenotypeDef? SoleDeterministicXenotype(PawnKindDef kind, Faction? faction, bool mustBeCapableOfViolence)
    {
        if (!ModsConfig.BiotechActive) return null;
        Dictionary<XenotypeDef, float> chances = PawnGenerator.XenotypesAvailableFor(kind, null, faction);
        try
        {
            XenotypeDef? sole = null;
            int positiveCount = 0;
            foreach (KeyValuePair<XenotypeDef, float> entry in chances)
            {
                if (entry.Key == null || entry.Value <= 0f) continue;
                if (mustBeCapableOfViolence && !entry.Key.canGenerateAsCombatant) continue;
                positiveCount++;
                if (positiveCount == 1) sole = entry.Key;
                else return null;
            }

            return positiveCount == 0 ? XenotypeDefOf.Baseliner : sole;
        }
        finally
        {
            chances.Clear();
        }
    }

    internal static void ResetGeneratedThisCase()
    {
        generatedThisCase.Clear();
        rolledXenotypesThisCase.Clear();
        specTargetsThisCase.Clear();
        raidMembersThisCase.Clear();
    }

    internal static bool IsTestPawn(Pawn pawn)
        => RimRunnerObservationBuffer.IsObservedPawn(pawn) || IsGeneratedThisCase(pawn);

    private const int StunProfileTicks = 100000;

    /// <summary>隔离 pawn 生成：按 kind、派系与异种生成一个未落图的隔离 pawn。</summary>
    /// <remarks>跳过老年病、旧伤与亲属；发育阶段采用 kind 声明的，未声明只生成成年人。不强制异种时只按 kind、派系与意识形态的 xenotypeSet 掷，无派系同样。forceNoBackstory 为真时两背景留空。</remarks>
    /// <exception cref="InvalidOperationException">生成出的 pawn 种族与 kind 的种族不匹配时抛。</exception>
    public static Pawn GenerateIsolatedPawn(
        PawnKindDef kind,
        Faction? faction,
        XenotypeDef? forcedXenotype = null,
        bool mustBeCapableOfViolence = false,
        bool forceNoBackstory = false)
        => GenerateIsolated(kind, faction, forcedXenotype, mustBeCapableOfViolence, forceNoBackstory, skipOldAgeInjuries: true, raidMember: false);

    internal static Pawn GenerateIsolatedPawnOnTile(PawnKindDef kind, Faction? faction, PlanetTile tile)
        => GenerateIsolated(kind, faction, forcedXenotype: null, mustBeCapableOfViolence: false, forceNoBackstory: false,
            skipOldAgeInjuries: true, raidMember: false, tile);

    private static void StripGeneratedApparel(Pawn pawn)
    {
        if (!RimRunnerDummies.IsDummyRace(pawn.def)) return;
        pawn.apparel?.DestroyAll();
        pawn.outfits?.forcedHandler?.Reset();
    }

    internal static Pawn GenerateRaidMemberPawn(PawnKindDef kind, Faction? faction, XenotypeDef? forcedXenotype)
        => GenerateIsolated(kind, faction, forcedXenotype, mustBeCapableOfViolence: true, forceNoBackstory: false, skipOldAgeInjuries: false, raidMember: true);

    [RimRunnerDirectGeneration("隔离生成入口的实现：生成根与袭击部署之下唯一的那次 GeneratePawn")]
    private static Pawn GenerateIsolated(
        PawnKindDef kind,
        Faction? faction,
        XenotypeDef? forcedXenotype,
        bool mustBeCapableOfViolence,
        bool forceNoBackstory,
        bool skipOldAgeInjuries,
        bool raidMember,
        PlanetTile? tile = null)
    {
        PawnGenerationRequest request = new(
            kind,
            faction,
            forceGenerateNewPawn: true,
            canGeneratePawnRelations: false,
            mustBeCapableOfViolence: mustBeCapableOfViolence,
            colonistRelationChanceFactor: 0f,
            forcedXenotype: forcedXenotype,
            forceNoBackstory: forceNoBackstory);
        if (tile.HasValue)
        {
            request.Tile = tile.Value;
        }

        if (kind.pawnGroupDevelopmentStage.HasValue)
        {
            request.AllowedDevelopmentalStages = kind.pawnGroupDevelopmentStage.Value;
        }

        if (raidMember && !Find.Storyteller.difficulty.ChildRaidersAllowed && faction != null && faction.HostileTo(Faction.OfPlayer))
        {
            request.AllowedDevelopmentalStages = DevelopmentalStage.Adult;
        }

        if (faction == null && forcedXenotype == null && ModsConfig.BiotechActive && kind.RaceProps.Humanlike)
        {
            request.ForcedXenotype = PawnGenerator.GetXenotypeForGeneratedPawn(request);
        }

        Pawn pawn;
        using (SuppressRelativeTurnedThought(kind))
        using (skipOldAgeInjuries ? RimRunnerOldAgeInjurySkip.Enter() : null)
        {
            pawn = PawnGenerator.GeneratePawn(request);
        }

        StripGeneratedApparel(pawn);
        AssertKindHonored(kind, faction, pawn);
        generatedThisCase.Add(pawn.ThingID);
        if (forcedXenotype == null && !raidMember) NoteRolledXenotype(kind, faction, mustBeCapableOfViolence, pawn);
        return pawn;
    }

    private static void AssertKindHonored(PawnKindDef kind, Faction? faction, Pawn pawn)
    {
        if (KindHonored(kind, pawn)) return;

        throw new InvalidOperationException(
            RimRunnerLog.Prefix + " 生成出来的 pawn 与所请求的 kind 不是同一种族："
            + "kind=" + kind.defName + "（race=" + kind.race.defName + "）"
            + " 实得 race=" + pawn.def.defName
            + " 派系=" + (faction?.def?.defName ?? "null"));
    }

    /// <summary>种族核对：pawn 的种族是否即 kind 的种族；任一方为 null 或 kind 无种族时为真。</summary>
    public static bool KindHonored(PawnKindDef? kind, Pawn? pawn)
    {
        return kind?.race == null || pawn?.def == null || pawn.def == kind.race;
    }

    private static IDisposable? SuppressRelativeTurnedThought(PawnKindDef? kind)
    {
        MutantDef? mutant = kind?.mutant;
        if (mutant == null || mutant.relativeTurnedThought == null)
        {
            return null;
        }

        return new RelativeTurnedThoughtSuppression(mutant);
    }

    private sealed class RelativeTurnedThoughtSuppression : IDisposable
    {
        private readonly MutantDef mutant;
        private readonly ThoughtDef saved;

        internal RelativeTurnedThoughtSuppression(MutantDef mutant)
        {
            this.mutant = mutant;
            saved = mutant.relativeTurnedThought;
            mutant.relativeTurnedThought = null;
        }

        public void Dispose()
        {
            mutant.relativeTurnedThought = saved;
        }
    }

    /// <summary>job 身份快照：返回 loadID，同一 Job 实例被对象池复用后 loadID 不同。</summary>
    /// <exception cref="ArgumentException">job 为 null 或未发号时抛。</exception>
    public static int JobIdentity(Job job)
    {
        if (job == null || job.loadID < 0)
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " 取 job 身份时 job 为 null 或未发号（loadID="
                                    + (job == null ? "<null job>" : job.loadID.ToString()) + "）",
                nameof(job));
        }

        return job.loadID;
    }

    /// <summary>该 pawn 是否仍在执行那个 job 身份。</summary>
    public static bool IsRunningJob(Pawn? pawn, int jobId)
    {
        Job? current = pawn?.CurJob;
        return current != null && current.loadID == jobId;
    }

    private static string? defaultNickCaseId;
    private static int defaultNickSeq;

    /// <summary>取下一个缺省昵称（&lt;用例 Id&gt;-&lt;序号&gt;；不在用例体内为 pawn-&lt;序号&gt;）。</summary>
    /// <remarks>序号在用例 Id 变化时归零，同一用例的各 phase 连续编号。</remarks>
    public static string NextDefaultNick()
    {
        string? caseId = RimRunnerObservationBuffer.ActiveCaseId;
        if (!string.Equals(caseId, defaultNickCaseId, StringComparison.Ordinal))
        {
            defaultNickCaseId = caseId;
            defaultNickSeq = 0;
        }

        defaultNickSeq++;
        return (caseId ?? "pawn") + "-" + defaultNickSeq;
    }

    /// <summary>设昵称：只对 NameTriple 名字生效，把其中的昵称改成给定值。</summary>
    public static void SetNick(Pawn pawn, string nick)
    {
        if (pawn.Name is NameTriple triple)
        {
            pawn.Name = new NameTriple(triple.First, nick, triple.Last);
        }
    }

    /// <summary>清生成随机 trait（含被压制的），保留基因强制 trait。</summary>
    public static void RemoveRandomTraits(Pawn pawn)
    {
        for (int i = (pawn.story?.traits?.allTraits.Count ?? 0) - 1; i >= 0; i--)
        {
            Trait trait = pawn.story!.traits.allTraits[i];
            if (trait.sourceGene == null)
            {
                trait.suppressedByTrait = false;
                trait.suppressedByGene = null;
                pawn.story.traits.RemoveTrait(trait);
            }
        }
    }

    private static BackstoryDef? neutralChildhood;
    private static BackstoryDef? neutralAdulthood;

    /// <summary>中性背景：把 backstory 归一到零 workDisables 的中性档。</summary>
    /// <remarks>未成年 pawn 的成年背景置空；无 story 的 pawn 保持原样返回。</remarks>
    public static void SetNeutralBackstory(Pawn pawn)
    {
        if (pawn.story == null)
        {
            return;
        }

        neutralChildhood ??= DefDatabase<BackstoryDef>.AllDefsListForReading.First(backstory =>
            backstory.slot == BackstorySlot.Childhood
            && backstory.workDisables == WorkTags.None
            && !backstory.IsPlayerColonyChildBackstory);
        neutralAdulthood ??= DefDatabase<BackstoryDef>.AllDefsListForReading.First(backstory =>
            backstory.slot == BackstorySlot.Adulthood
            && backstory.workDisables == WorkTags.None);
        pawn.story.Childhood = neutralChildhood;
        pawn.story.Adulthood = pawn.DevelopmentalStage.Adult() ? neutralAdulthood : null;
        pawn.Notify_DisabledWorkTypesChanged();
        pawn.skills?.Notify_SkillDisablesChanged();
    }

    /// <summary>把 pawn 的绘制位置重置到移动插值根（所在格与下一格按移动进度插值）。</summary>
    public static void SyncDrawPos(Pawn? pawn)
    {
        pawn?.Drawer?.tweener?.ResetTweenedPosToRoot();
    }

    /// <summary>把年龄钉到固定值（孩子 kind 取其未成年段中点，否则 25 岁），饱食按比例跟到新上限。</summary>
    /// <exception cref="InvalidOperationException">kind 年龄窗与种族生命阶段无交集，或钉后发育阶段不在 kind 声明内时抛。</exception>
    public static void SetFixedAge(Pawn pawn)
    {
        if (!TryFixedAgeTicks(pawn.kindDef, pawn.def, out long ticks, out string? failure))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + pawn.LabelShort + " 无法钉固定年龄：" + failure);
        }

        SetAgeTicks(pawn, ticks);
        DevelopmentalStage? declared = pawn.kindDef?.pawnGroupDevelopmentStage;
        if (declared.HasValue && !declared.Value.HasAny(pawn.DevelopmentalStage))
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " " + pawn.kindDef!.defName + " 钉固定年龄 " + pawn.ageTracker.AgeBiologicalYearsFloat.ToString("0.0")
                + " 岁后发育阶段为 " + pawn.DevelopmentalStage + "，不在 kind 声明的 " + declared.Value + " 内");
        }
    }

    internal static void SetFixedAdultAge(Pawn pawn) => SetAgeTicks(pawn, FixedAdultAgeTicks);

    internal static bool TryFixedAgeTicks(PawnKindDef kind, ThingDef race, out long ticks,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? failure)
    {
        ticks = FixedAdultAgeTicks;
        failure = null;
        if (!PinsMinorAge(kind))
        {
            return true;
        }

        DevelopmentalStage stages = kind.pawnGroupDevelopmentStage!.Value;

        List<LifeStageAge> ages = race.race.lifeStageAges;
        bool found = false;
        float lo = 0f;
        float hi = 0f;
        for (int i = 0; i < ages.Count; i++)
        {
            float start = Math.Max(ages[i].minAge, kind.minGenerationAge);
            float end = Math.Min(i + 1 < ages.Count ? ages[i + 1].minAge : float.MaxValue, kind.maxGenerationAge);
            if (!stages.HasAny(ages[i].def.developmentalStage) || start >= end)
            {
                if (found) break;
                continue;
            }

            if (!found) lo = start;
            found = true;
            hi = end;
        }

        if (!found)
        {
            failure = "kind " + kind.defName + " 只允许 " + stages + "，年龄窗 " + kind.minGenerationAge + "–" + kind.maxGenerationAge
                      + " 岁与种族 " + race.defName + " 里这些发育阶段的生命阶段无交集";
            return false;
        }

        ticks = (long)((lo + hi) / 2f * GenDate.TicksPerYear);
        return true;
    }

    internal static bool PinsMinorAge(PawnKindDef? kind)
        => kind?.pawnGroupDevelopmentStage is DevelopmentalStage stages && !stages.HasAny(DevelopmentalStage.Adult);

    private static void SetAgeTicks(Pawn pawn, long ticks)
    {
        Need_Food? food = pawn.needs?.food;
        float foodPercentage = food?.CurLevelPercentage ?? 0f;
        pawn.ageTracker.AgeBiologicalTicks = ticks;
        pawn.ageTracker.AgeChronologicalTicks = ticks;
        if (food == null)
        {
            return;
        }

        StatDefOf.MaxNutrition.Worker.ClearCacheForThing(pawn);
        food.CurLevelPercentage = foodPercentage;
    }

    /// <summary>确定性归一化：清随机 trait、钉年龄、换中性背景、销毁衣装武器与行囊；幂等。</summary>
    /// <remarks>clearGeneratedHediffs 为真时另清空全部 hediff。</remarks>
    /// <exception cref="InvalidOperationException">pawn 是暴力档 Incapable 的规格靶，或是带有随身物的袭击成员时抛。</exception>
    public static void NormalizeForDeterminism(Pawn pawn, bool clearGeneratedHediffs = false)
    {
        string? raidLoadout = IsRaidMember(pawn) ? DescribeLoadout(pawn) : null;
        bool incapableTarget = TryGetSpecTarget(pawn, out SpecTarget? target)
                               && target.Spec is RimRunnerTargetSpec { Violence: RimRunnerPawnViolenceProfile.Incapable };
        if (raidLoadout != null || incapableTarget)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " NormalizeForDeterminism 会剥掉夹具："
                + (raidLoadout != null ? "袭击成员 " + pawn.LabelShort + " 的随身物（" + raidLoadout + "）" : "")
                + (incapableTarget ? "靶 " + pawn.LabelShort + " 的无暴力档（中性背景下可暴力）" : "")
                + "。靶与袭击成员不归一化");
        }

        ApplyGeneration(pawn, clearGeneratedHediffs
            ? NormalizedGeneration | RimRunnerTargetGeneration.ClearHediffs
            : NormalizedGeneration);
    }

    private static string? DescribeLoadout(Pawn pawn)
    {
        int equipment = pawn.equipment?.AllEquipmentListForReading.Count ?? 0;
        int apparel = pawn.apparel?.WornApparelCount ?? 0;
        int inventory = pawn.inventory?.innerContainer.Count ?? 0;
        if (equipment + apparel + inventory == 0) return null;
        return "武器 " + equipment + " · 衣装 " + apparel + " · 行囊 " + inventory;
    }

    /// <summary>NormalizeForDeterminism 施加的生成清理位组合。</summary>
    public const RimRunnerTargetGeneration NormalizedGeneration =
        RimRunnerTargetGeneration.ClearTraits | RimRunnerTargetGeneration.NeutralBackstory
        | RimRunnerTargetGeneration.FixedAge | RimRunnerTargetGeneration.ClearLoadout;

    /// <summary>生成清理：按生成清理位清掉 pawn 的生成随机。</summary>
    public static void ApplyGeneration(Pawn pawn, RimRunnerTargetGeneration generation)
    {
        bool has(RimRunnerTargetGeneration item) => (generation & item) != 0;
        if (has(RimRunnerTargetGeneration.ClearTraits)) RemoveRandomTraits(pawn);
        if (has(RimRunnerTargetGeneration.FixedAge)) SetFixedAge(pawn);
        if (has(RimRunnerTargetGeneration.NeutralBackstory)) SetNeutralBackstory(pawn);
        if (has(RimRunnerTargetGeneration.ClearLoadout)) ClearLoadout(pawn);
        if (!has(RimRunnerTargetGeneration.ClearHediffs)) return;
        foreach (Hediff hediff in pawn.health.hediffSet.hediffs.ToList())
        {
            pawn.health.RemoveHediff(hediff);
        }
    }

    /// <summary>清空随身物：销毁 pawn 的衣装、武器与行囊。</summary>
    public static void ClearLoadout(Pawn pawn)
    {
        pawn.apparel?.DestroyAll();
        pawn.equipment?.DestroyAllEquipment();
        pawn.inventory?.DestroyAll();
    }

    /// <summary>只销毁行囊。</summary>
    public static void ClearInventory(Pawn pawn)
    {
        pawn.inventory?.DestroyAll();
    }

    /// <summary>以地图中心为锚取第一个干净落点格。</summary>
    /// <remarks>由近及远取 DropCellFinder.IsGoodDropSpot 认可、非雾、无 pawn 与物品的格；中心半径 GenRadial.MaxRadialPatternRadius − 1 格内没有时按用例前提不成立认领。</remarks>
    public static IntVec3 AnchoredDropSpot(Map map)
    {
        foreach (IntVec3 cell in GenRadial.RadialCellsAround(
                     map.Center, GenRadial.MaxRadialPatternRadius - 1f, useCenter: true))
        {
            if (DropCellFinder.IsGoodDropSpot(cell, map, allowFogged: false, canRoofPunch: false)
                && cell.GetFirstPawn(map) == null
                && cell.GetFirstItem(map) == null)
            {
                return cell;
            }
        }

        RimRunnerFault.Require(false, NoDropSpotReason,
            RimRunnerCellBlockers.MapLabel(map) + " 中心半径 " + (GenRadial.MaxRadialPatternRadius - 1f) + " 格");
        throw new InvalidOperationException(NoDropSpotReason);
    }

    internal const string NoDropSpotReason = "以地图中心为锚找不到夹具落点";

    /// <summary>按 AI 档处置刚落图的一次性 pawn。</summary>
    public static void ApplyDisposableAgency(RimRunnerContext ctx, Pawn pawn, RimRunnerDisposableAgency agency)
    {
        if (agency == RimRunnerDisposableAgency.Pinned)
        {
            ctx.PinAgency(pawn);
        }
    }

    internal static void RunSpawnHook(Pawn pawn) => RimRunnerProduct.Capabilities.RunSpawnedHooks(pawn);

    internal static void RunGeneratedHook(Pawn pawn) => RimRunnerProduct.Capabilities.RunGeneratedHooks(pawn);

    /// <summary>按暴力档改 pawn：Incapable 换成年背景，Capable 只核验。</summary>
    /// <exception cref="InvalidOperationException">Capable 下 pawn 无暴力能力，或 Incapable 下无 story、施加未生效时抛。</exception>
    public static void ApplyViolenceProfile(Pawn pawn, RimRunnerPawnViolenceProfile profile)
    {
        switch (profile)
        {
            case RimRunnerPawnViolenceProfile.Unrestricted:
                return;
            case RimRunnerPawnViolenceProfile.Capable:
                if (pawn.WorkTagIsDisabled(WorkTags.Violent))
                {
                    throw new InvalidOperationException(
                        RimRunnerLog.Prefix + " 生成的战斗靶不具备暴力能力：" + pawn);
                }

                return;
            case RimRunnerPawnViolenceProfile.Incapable:
                if (pawn.story == null)
                {
                    throw new InvalidOperationException(
                        RimRunnerLog.Prefix + " 无 story 的 pawn 无法应用无暴力能力档：" + pawn);
                }

                pawn.story.Adulthood = DefDatabase<BackstoryDef>.GetNamed("Missionary99");
                pawn.Notify_DisabledWorkTypesChanged();
                if (!pawn.WorkTagIsDisabled(WorkTags.Violent))
                {
                    throw new InvalidOperationException(
                        RimRunnerLog.Prefix + " 无暴力能力档未生效：" + pawn);
                }

                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile, null);
        }
    }

    /// <summary>按行动档改 pawn，返回挂上的 hediff（若有）。</summary>
    /// <remarks>只改行动能力；承伤由靶规格的 damage 或 RimRunnerInvulnerability 另定。</remarks>
    /// <exception cref="InvalidOperationException">施加后 pawn 不处于该档要的状态时抛。</exception>
    public static Hediff? ApplyMobilityProfile(Pawn pawn, RimRunnerPawnMobilityProfile profile)
    {
        Hediff? hediff;
        switch (profile)
        {
            case RimRunnerPawnMobilityProfile.Mobile:
                return null;
            case RimRunnerPawnMobilityProfile.Anesthetized:
                hediff = pawn.health.AddHediff(DefDatabase<HediffDef>.GetNamed("Anesthetic"));
                if (hediff == null || !pawn.Downed || pawn.Awake() || pawn.health.CanCrawlOrMove)
                {
                    throw new InvalidOperationException(
                        RimRunnerLog.Prefix + " 麻醉行动档未得到倒地、昏迷且不能移动的 pawn：" + pawn);
                }

                return hediff;
            case RimRunnerPawnMobilityProfile.AwakeImmobile:
                hediff = pawn.health.AddHediff(PinHediff);
                if (hediff == null || !pawn.Downed || !pawn.Awake()
                    || pawn.health.CanCrawlOrMove
                    || pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                {
                    throw new InvalidOperationException(
                        RimRunnerLog.Prefix + " 清醒瘫痪行动档未得到倒地、清醒且不能移动的 pawn：" + pawn
                        + "（Downed=" + pawn.Downed + "，Awake=" + pawn.Awake() + "，CanCrawlOrMove=" + pawn.health.CanCrawlOrMove
                        + "，Moving=" + pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving) + "）");
                }

                RimRunnerCaseWatch.RegisterAwakeImmobile(pawn, hediff);
                return hediff;
            case RimRunnerPawnMobilityProfile.Stunned:
                pawn.stances.stunner.StunFor(StunProfileTicks, null, addBattleLog: false, showMote: false);
                if (!pawn.stances.stunner.Stunned || pawn.Downed)
                {
                    throw new InvalidOperationException(
                        RimRunnerLog.Prefix + " 眩晕行动档未得到 stunned 且非倒地的 pawn：" + pawn
                        + " Stunned=" + pawn.stances.stunner.Stunned + " Downed=" + pawn.Downed);
                }

                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile, null);
        }
    }

    /// <summary>制服并钉住：先打伤再挂钉住 hediff，返回时 pawn 倒地、不能移动、不出血且存活。</summary>
    /// <remarks>打出的出血伤口全部包扎；制服后的疼痛、意识与 Moving 以 label 为名记一条 Trace 诊断。</remarks>
    /// <exception cref="InvalidOperationException">打伤致死，或包扎后仍出血，或挂钉住 hediff 后仍未倒地、仍能移动或仍能爬行时抛。</exception>
    public static Hediff SubdueAndPin(RimRunnerContext ctx, Pawn pawn, string label)
    {
        HealthUtility.DamageUntilDowned(pawn, allowBleedingWounds: false);
        if (pawn.Dead)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + "[" + label + "] 制服后靶已死亡：Dead=" + pawn.Dead);
        }

        List<Hediff> bleeding = pawn.health.hediffSet.hediffs.Where(hediff => hediff.Bleeding).ToList();
        foreach (Hediff hediff in bleeding)
        {
            hediff.Tended(1f, 1f);
        }

        pawn.health.hediffSet.DirtyCache();
        if (pawn.health.hediffSet.BleedRateTotal > 0f)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + "[" + label + "] 制服后包扎 " + bleeding.Count + " 处仍在出血：BleedRateTotal="
                + pawn.health.hediffSet.BleedRateTotal.ToString("0.###") + "，出血的 hediff = "
                + string.Join("、", pawn.health.hediffSet.hediffs.Where(hediff => hediff.Bleeding)
                    .Select(hediff => hediff.def.defName + "@" + (hediff.Part?.def.defName ?? "全身"))));
        }

        ctx.AddDiagnostic(RimRunnerDiagnosticKind.Trace, label,
            "downedByDamage=" + pawn.Downed
                              + "; pain=" + pawn.health.hediffSet.PainTotal.ToString("0.##")
                              + "; consciousness=" + pawn.health.capacities
                                  .GetLevel(PawnCapacityDefOf.Consciousness).ToString("0.##")
                              + "; moving=" + pawn.health.capacities
                                  .GetLevel(PawnCapacityDefOf.Moving).ToString("0.##")
                              + "; awake=" + pawn.Awake()
                              + "; canCrawl=" + pawn.health.CanCrawl
                              + "; tendedBleeding=" + bleeding.Count);

        Hediff? pin = pawn.health.AddHediff(PinHediff);
        if (pin == null || !pawn.Downed || pawn.Dead || pawn.health.CanCrawlOrMove)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + "[" + label + "] 制服态钉住失败：Downed=" + pawn.Downed
                + " Dead=" + pawn.Dead + " CanCrawlOrMove=" + pawn.health.CanCrawlOrMove);
        }

        return pin;
    }

    /// <summary>回收一次性 pawn：解散它所在或引用它的编队，中断图上以它为目标的 job，连尸体一起销毁并丢弃。</summary>
    /// <remarks>可重复调：null 返回，已丢弃的只摘信与夹具。已离场照销毁丢弃；所搬的物品随它销毁，被搬的 pawn 与用例前物品落回图上。连带销毁其火、弹丸与未结算爆炸；在飞弹改打所在格。受击账留到段末；长用例可 Forget 或 Watch([])。</remarks>
    public static void Cleanup(Pawn? pawn)
    {
        if (pawn == null)
        {
            return;
        }

        RimRunnerLetterProbe.RemoveLettersAbout(pawn);
        ReleaseRolledXenotype(pawn);
        if (pawn.Discarded)
        {
            UnregisterFixtures(pawn);
            NotifyRecycled(pawn);
            return;
        }

        StowCarried(pawn);
        DissolveLord(pawn);

        Map map = pawn.MapHeld;
        if (map != null)
        {
            foreach (Pawn other in map.mapPawns.AllPawnsSpawned.ToList())
            {
                Job job = other.CurJob;
                if (job != null && (job.targetA.Thing == pawn
                                    || job.targetB.Thing == pawn || job.targetC.Thing == pawn))
                {
                    other.jobs.StopAll();
                }
            }
        }

        UnregisterFixtures(pawn);
        pawn.jobs?.StopAll();
        Corpse corpse = pawn.Corpse;
        if (corpse is { Destroyed: false })
        {
            corpse.Destroy();
        }

        DestroyAndDiscard(pawn);
        NotifyRecycled(pawn);
    }

    private static void UnregisterFixtures(Pawn pawn)
    {
        RimRunnerInvulnerability.Remove(pawn);
        RimRunnerAmbientControl.Remove(pawn);
        RimRunnerPainControl.Remove(pawn);
        RimRunnerAgencyPin.Remove(pawn);
        RimRunnerAgencyPin.RestoreHostility(pawn);
        RimRunnerNeedsFreeze.Remove(pawn);
        RimRunnerThreatDisable.Forget(pawn);
        RimRunnerHediffBreakBlock.Remove(pawn);
    }

    private static void DissolveLord(Pawn pawn)
    {
        Lord? lord = pawn.GetLord();
        if (lord != null)
        {
            StowCarriedOfDisposableMembers(lord);
            lord.lordManager.RemoveLord(lord);

            if (pawn.GetLord() != null)
            {
                RimRunnerSentinel.Raise("解散编队之后 pawn 仍挂在编队上：Pawn.lord 与该编队的 ownedPawns 不自洽",
                    pawn.LabelShortCap + " ↔ " + lord.LordJob?.GetType().Name);
            }
        }

        Map map = pawn.MapHeld;
        if (map == null)
        {
            return;
        }

        foreach (Lord other in map.lordManager.lords.ToList())
        {
            if (LordJobReferences(other.LordJob, pawn))
            {
                StowCarriedOfDisposableMembers(other);
                other.lordManager.RemoveLord(other);
            }
        }

        foreach (Lord remaining in map.lordManager.lords)
        {
            if (LordJobReferences(remaining.LordJob, pawn))
            {
                RimRunnerSentinel.Raise("解散编队之后 pawn 仍被编队的字段引用：该 LordJob 的承载形态不在解散编队的扫描面内",
                    pawn.LabelShortCap + " ↔ " + remaining.LordJob?.GetType().Name);
            }
        }
    }

    private static void StowCarriedOfDisposableMembers(Lord lord)
    {
        foreach (Pawn member in lord.ownedPawns)
        {
            if (IsGeneratedThisCase(member))
            {
                StowCarried(member);
            }
        }
    }

    private static void NotifyRecycled(Pawn pawn)
    {
        RimRunnerContext? active = RimRunnerContext.Active;
        Action notify = RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Case or RimRunnerPhase.Scene => () => active!.NotifyRecycled(pawn),
            RimRunnerPhase.Enumeration => () => active!.EnumerationFaults.Add(
                "ShardCells 回收 pawn " + pawn.ThingID + "：枚举只读，不生成、不回收"),
            RimRunnerPhase.None => () => { }
        };
        notify();
    }

    private static bool IsBaselineItem(Thing carried)
    {
        return RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Case or RimRunnerPhase.Scene => RimRunnerContext.Active!.IsBaselineItem(carried),
            RimRunnerPhase.Enumeration or RimRunnerPhase.None => false
        };
    }

    private static void StowCarried(Pawn pawn)
    {
        Thing? carried = pawn.carryTracker?.CarriedThing;
        if (carried == null || carried is Pawn || pawn.inventory == null
            || IsBaselineItem(carried))
        {
            return;
        }

        pawn.carryTracker!.innerContainer.TryTransferToContainer(carried, pawn.inventory.innerContainer);
    }

    private static bool LordJobReferences(LordJob? job, Pawn pawn)
    {
        if (job == null)
        {
            return false;
        }

        foreach (FieldInfo field in LordJobPawnFields(job.GetType()))
        {
            object? value = field.GetValue(job);
            if (value == null)
            {
                continue;
            }

            if (ReferenceEquals(value, pawn))
            {
                return true;
            }

            if (value is IEnumerable<Pawn> many && many.Contains(pawn))
            {
                return true;
            }
        }

        return false;
    }

    private static FieldInfo[] LordJobPawnFields(Type jobType)
    {
        if (LordJobPawnFieldCache.TryGetValue(jobType, out FieldInfo[]? cached))
        {
            return cached;
        }

        FieldInfo[] fields = RimRunnerAbsentReferences
            .ReadableFields(jobType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => typeof(Pawn).IsAssignableFrom(field.FieldType)
                            || typeof(IEnumerable<Pawn>).IsAssignableFrom(field.FieldType))
            .ToArray();
        LordJobPawnFieldCache[jobType] = fields;
        return fields;
    }

    private static readonly Dictionary<Type, FieldInfo[]> LordJobPawnFieldCache = [];

    internal static void DestroyAndDiscard(Pawn pawn)
    {
        ReapLiveDamageSources(pawn);
        if (!pawn.Destroyed)
        {
            pawn.Destroy();
        }

        ReleaseRitualObligations(pawn);

        if (Find.WorldPawns.Contains(pawn))
        {
            Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
        }
        else if (!pawn.Discarded)
        {
            pawn.Discard(silentlyRemoveReferences: true);
        }

        ReleaseRelationshipRecordReferences(pawn);
        ReleaseTaleReferences(pawn);
        AssertNothingStillConcerns(pawn);
        RimRunnerRetentionProbe.NoteRecycled(pawn);
    }

    private static void ReleaseRelationshipRecordReferences(Pawn pawn)
    {
        RelationshipRecords? records = Find.RelationshipRecords;
        if (records == null)
        {
            return;
        }

        foreach (KeyValuePair<int, RelationshipRecord> pair in records.Records)
        {
            pair.Value.RemoveReference(pawn);
        }
    }

    private static void ReleaseTaleReferences(Pawn pawn)
    {
        if (!pawn.Discarded || Find.TaleManager == null)
        {
            return;
        }

        Find.TaleManager.AllTalesListForReading
            .RemoveAll(tale => tale.Unused && RimRunnerTaleDanglingProbe.Carries(tale, pawn));
    }

    private static void AssertNothingStillConcerns(Pawn pawn)
    {
        if (!pawn.Discarded)
        {
            return;
        }

        List<string> holders = [];
        List<int> taleUses = Find.TaleManager.AllTalesListForReading
            .Where(tale => RimRunnerTaleDanglingProbe.Carries(tale, pawn))
            .Select(tale => tale.Uses)
            .ToList();
        if (taleUses.Count > 0)
        {
            holders.Add("TaleManager（tale 仍持有，uses=" + string.Join(",", taleUses) + "：RemoveTale 拒删）");
        }

        if (Find.PlayLog.AnyEntryConcerns(pawn))
        {
            holders.Add("PlayLog");
        }

        if (Find.BattleLog.AnyEntryConcerns(pawn))
        {
            holders.Add("BattleLog");
        }

        if (AnyRitualObligationConcerns(pawn))
        {
            holders.Add("Ideo 仪式义务（ReleaseRitualObligations 之后仍持有）");
        }

        if (AnyRelationshipRecordConcerns(pawn))
        {
            holders.Add("RelationshipRecords（ReleaseRelationshipRecordReferences 之后 References 仍持有）");
        }

        string? damageSource = AnyLiveDamageSourceConcerns(pawn);
        if (damageSource != null)
        {
            holders.Add(damageSource);
        }

        if (holders.Count > 0)
        {
            RimRunnerSentinel.Raise("已丢弃的 pawn 仍被引用。登记处（tale / 日志 / 仪式义务 / 关系记录）的引用"
                      + "此后每次存档都会写出一条 vanilla 警告级日志，"
                      + "落在存档发生时正在执行的用例名下；伤害源一项的后果写在该项里",
                pawn.LabelShortCap + "：" + string.Join("、", holders));
        }
    }

    private static void ReapLiveDamageSources(Pawn pawn)
    {
        foreach (Map map in Find.Maps)
        {
            foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
            {
                if (thing is Fire fire && ReferenceEquals(fire.instigator, pawn) && !fire.Destroyed)
                {
                    fire.Destroy();
                }
            }

            foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).ToList())
            {
                if (thing is not Projectile projectile || projectile.Destroyed)
                {
                    continue;
                }

                if (ReferenceEquals(projectile.Launcher, pawn))
                {
                    projectile.Destroy();
                    continue;
                }

                if (ReferenceEquals(projectile.intendedTarget.Thing, pawn))
                {
                    projectile.intendedTarget = new LocalTargetInfo(projectile.intendedTarget.Cell);
                }

                if (ReferenceEquals(projectile.usedTarget.Thing, pawn))
                {
                    projectile.usedTarget = new LocalTargetInfo(projectile.usedTarget.Cell);
                }
            }

            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.Explosion).ToList())
            {
                if (thing is Explosion explosion && ReferenceEquals(explosion.instigator, pawn) && !explosion.Destroyed)
                {
                    explosion.Destroy();
                }
            }
        }
    }

    private static string? AnyLiveDamageSourceConcerns(Pawn pawn)
    {
        List<string>? sources = null;
        foreach (Map map in Find.Maps)
        {
            foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire))
            {
                if (thing is Fire fire && ReferenceEquals(fire.instigator, pawn))
                {
                    (sources ??= []).Add("Fire@" + fire.Position);
                }
            }

            foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile))
            {
                if (thing is Projectile projectile
                    && (ReferenceEquals(projectile.Launcher, pawn) || ReferenceEquals(projectile.intendedTarget.Thing, pawn)
                        || ReferenceEquals(projectile.usedTarget.Thing, pawn)))
                {
                    (sources ??= []).Add(projectile.def.defName + "@" + projectile.Position);
                }
            }

            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.Explosion))
            {
                if (thing is Explosion explosion && ReferenceEquals(explosion.instigator, pawn))
                {
                    (sources ??= []).Add("Explosion@" + explosion.Position);
                }
            }
        }

        return sources == null
            ? null
            : "图上仍有以该 pawn 为 instigator 的伤害源或以该 pawn 为目标的在飞弹（"
              + "伤到血肉 pawn 时该 pawn 进 tale 与 BattleLog，受害者是非敌对人形时还进受害者的记忆）："
              + string.Join("、", sources);
    }

    private static void ReleaseRitualObligations(Pawn pawn)
    {
        foreach (Ideo ideo in Find.IdeoManager.IdeosListForReading)
        {
            foreach (Precept precept in ideo.PreceptsListForReading)
            {
                if (precept is not Precept_Ritual ritual)
                {
                    continue;
                }

                RemoveObligationsConcerning(ritual.activeObligations, pawn,
                    obligation => ritual.RemoveObligation(obligation));
                RemoveObligationsConcerning(ritual.completedObligations, pawn,
                    obligation => ritual.completedObligations.Remove(obligation));
            }
        }
    }

    private static bool AnyRelationshipRecordConcerns(Pawn pawn)
    {
        RelationshipRecords? records = Find.RelationshipRecords;
        if (records == null)
        {
            return false;
        }

        foreach (KeyValuePair<int, RelationshipRecord> pair in records.Records)
        {
            if (pair.Value.References.Contains(pawn))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyRitualObligationConcerns(Pawn pawn)
    {
        foreach (Ideo ideo in Find.IdeoManager.IdeosListForReading)
        {
            foreach (Precept precept in ideo.PreceptsListForReading)
            {
                if (precept is Precept_Ritual ritual
                    && (AnyConcerns(ritual.activeObligations, pawn)
                        || AnyConcerns(ritual.completedObligations, pawn)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void RemoveObligationsConcerning(List<RitualObligation>? obligations,
        Pawn pawn, Action<RitualObligation> remove)
    {
        if (obligations == null)
        {
            return;
        }

        for (int i = obligations.Count - 1; i >= 0; i--)
        {
            if (ObligationConcerns(obligations[i], pawn))
            {
                remove(obligations[i]);
            }
        }
    }

    private static bool AnyConcerns(List<RitualObligation>? obligations, Pawn pawn)
    {
        return obligations != null
               && obligations.Any(obligation => ObligationConcerns(obligation, pawn));
    }

    private static bool ObligationConcerns(RitualObligation? obligation, Pawn pawn)
    {
        return obligation != null
               && (TargetConcerns(obligation.targetA, pawn)
                   || TargetConcerns(obligation.targetB, pawn)
                   || TargetConcerns(obligation.targetC, pawn)
                   || (obligation.onlyForPawns != null && obligation.onlyForPawns.Contains(pawn)));
    }

    private static bool TargetConcerns(TargetInfo target, Pawn pawn)
    {
        Thing thing = target.Thing;
        return thing == pawn || (thing is Corpse corpse && corpse.InnerPawn == pawn);
    }

    /// <summary>丢弃自快照以来新增进世界池的 pawn，连同这次丢弃产生的孤儿。</summary>
    public static void DiscardWorldPawnsAddedSince(HashSet<Pawn> before)
    {
        HashSet<Pawn> added = [.. Find.WorldPawns.AllPawnsAliveOrDead.Where(pawn => !before.Contains(pawn))];
        HashSet<Pawn> orphansBefore = RimRunnerOrphanSweep.SnapshotIgnoringHolders(added);
        foreach (Pawn pawn in added)
        {
            Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
        }

        RimRunnerOrphanSweep.SweepAddedSince(orphansBefore);
    }
}
