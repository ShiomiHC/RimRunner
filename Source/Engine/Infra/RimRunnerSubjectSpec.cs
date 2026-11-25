namespace RimRunner;

/// <summary>被测体规格：物种、归属、AI 档与生成项；威胁、承伤、行动档、需求、hediff 崩溃另行压制。</summary>
/// <remarks>五项依次调 DisableThreat、RimRunnerInvulnerability、ApplyMobilityProfile、SuspendNeeds、BlockHediffBreaks。被测体不登记规格靶：「带生成随机」行只解读靶。</remarks>
public sealed record RimRunnerSubjectSpec
{
    /// <summary>玩家派系组合：玩家派系的一次性 pawn，按 kind 守住种族、归一化。</summary>
    /// <remarks>归一化清 trait、背景、年龄与随身物，生成期 hediff 照留；生成出的种族与 kind 不匹配即抛。</remarks>
    public static RimRunnerSubjectSpec Disposable(PawnKindDef kind, RimRunnerDisposableAgency agency)
        => new()
        {
            Kind = kind,
            Affiliation = RimRunnerSubjectAffiliation.Player,
            Agency = agency,
            Generation = RimRunnerPawns.NormalizedGeneration,
            ForceNoBackstory = false,
            Xenotype = RimRunnerSubjectXenotype.Unforced,
            MustBeCapableOfViolence = false,
        };

    /// <summary>任意人形组合：种族不限的一次性人形，其余同 Disposable。</summary>
    /// <remarks>kind 取玩家派系的 basicMemberKind，basicMemberKind 为空时取 Colonist。</remarks>
    public static RimRunnerSubjectSpec DisposableHumanlike(RimRunnerDisposableAgency agency)
        => Disposable(Faction.OfPlayer?.def?.basicMemberKind ?? PawnKindDefOf.Colonist, agency);

    /// <summary>无派系对照组组合：无派系、保留自主 AI 的对照组 pawn，按 kind 守住种族、归一化。</summary>
    public static RimRunnerSubjectSpec UnaffiliatedControl(PawnKindDef kind)
        => Disposable(kind, RimRunnerDisposableAgency.Free) with
        {
            Affiliation = RimRunnerSubjectAffiliation.Unaffiliated(RimRunnerTargetDeathOnDowned.Normal),
        };

    /// <summary>物种。</summary>
    public required PawnKindDef Kind { get; init; }

    /// <summary>落到哪个派系，及非玩家派系下的倒地即死掷骰。</summary>
    public required RimRunnerSubjectAffiliation Affiliation { get; init; }

    /// <summary>被测体 AI 档：Pinned 走 ctx.PinAgency，Free 保留自主 AI。</summary>
    public required RimRunnerDisposableAgency Agency { get; init; }

    /// <summary>生成后清掉的随机项；含 Baseliner 位时生成入口抛。</summary>
    public required RimRunnerTargetGeneration Generation { get; init; }

    /// <summary>生成期两个背景槽留空。</summary>
    /// <remarks>为真时背景技能加成与强挂 hediff 都不发生；为假时二者留在 pawn 上到用例末，归一化不撤销它们。同一批成员须都开或都不开。</remarks>
    public required bool ForceNoBackstory { get; init; }

    /// <summary>生成请求强制的异种。</summary>
    public required RimRunnerSubjectXenotype Xenotype { get; init; }

    /// <summary>生成请求的 mustBeCapableOfViolence。</summary>
    public required bool MustBeCapableOfViolence { get; init; }

    /// <summary>kind 的种族是否人形。</summary>
    public bool Humanlike => Kind.race?.race?.Humanlike ?? false;

    /// <summary>规格全文，供报错与读数。</summary>
    public override string ToString()
        => "subject kind=" + Kind.defName + " " + Affiliation + " agency=" + Agency
           + " generation=" + Generation + " forceNoBackstory=" + ForceNoBackstory
           + " xenotype=" + Xenotype + " violenceCapable=" + MustBeCapableOfViolence;

    internal void Validate()
    {
        if (Kind == null) throw new ArgumentNullException(nameof(Kind), RimRunnerLog.Prefix + " 被测体规格须给 kind");
        if (Affiliation == null) throw new ArgumentNullException(nameof(Affiliation), RimRunnerLog.Prefix + " 被测体规格须给归属");
        if (Xenotype == null) throw new ArgumentNullException(nameof(Xenotype), RimRunnerLog.Prefix + " 被测体规格须给异种项");

        List<string> conflicts = [];
        if (Kind.race?.race == null)
        {
            conflicts.Add("kind " + Kind.defName + " 的 race 不是 pawn 种族");
        }

        if ((Generation & RimRunnerTargetGeneration.Baseliner) != 0)
        {
            conflicts.Add("generation 含 Baseliner 位；被测体的异种只由 Xenotype = RimRunnerSubjectXenotype.Baseliner 指定");
        }

        if (conflicts.Count > 0)
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " 被测体规格自相矛盾（" + Kind.defName + "）：" + string.Join("；", conflicts));
        }
    }
}

/// <summary>被测体派系归属：被测体落到哪个派系，非玩家派系连同倒地即死掷骰一起给。</summary>
/// <remarks>指定派系的立场按构造这一刻对玩家的关系推出，记在归属全文里。</remarks>
public sealed class RimRunnerSubjectAffiliation
{
    private readonly string label;

    private RimRunnerSubjectAffiliation(string label, RimRunnerTargetStance? stance, Faction? faction,
        RimRunnerTargetDeathOnDowned deathOnDowned)
    {
        this.label = label;
        Stance = stance;
        Faction = faction;
        DeathOnDowned = deathOnDowned;
    }

    /// <summary>玩家派系归属：玩家派系；vanilla 对玩家派系不掷倒地即死。</summary>
    public static RimRunnerSubjectAffiliation Player { get; } =
        new("affiliation=Player", RimRunnerTargetStance.Player, null, RimRunnerTargetDeathOnDowned.Normal);

    /// <summary>敌对派系归属：存档里任一敌对人形派系。</summary>
    public static RimRunnerSubjectAffiliation Hostile(RimRunnerTargetDeathOnDowned deathOnDowned)
        => new("affiliation=Hostile deathOnDowned=" + deathOnDowned, RimRunnerTargetStance.Hostile, null, deathOnDowned);

    /// <summary>中立派系归属：存档里首个不敌对玩家、取得出成员 kind 的人形派系；没有即抛。</summary>
    public static RimRunnerSubjectAffiliation Neutral(RimRunnerTargetDeathOnDowned deathOnDowned)
        => new("affiliation=Neutral deathOnDowned=" + deathOnDowned, RimRunnerTargetStance.Neutral, null, deathOnDowned);

    /// <summary>无派系归属：被测体生成后不归任何派系。</summary>
    public static RimRunnerSubjectAffiliation Unaffiliated(RimRunnerTargetDeathOnDowned deathOnDowned)
        => new("affiliation=Unaffiliated deathOnDowned=" + deathOnDowned, null, null, deathOnDowned);

    /// <summary>指定派系归属：指定的非玩家派系。</summary>
    /// <remarks>引擎不为被测体建 lord。</remarks>
    public static RimRunnerSubjectAffiliation Of(Faction faction, RimRunnerTargetDeathOnDowned deathOnDowned)
    {
        if (faction == null)
        {
            throw new ArgumentNullException(nameof(faction), RimRunnerLog.Prefix + " Of 须给派系");
        }

        if (faction.IsPlayer)
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " Of 只接受非玩家派系，传入的是玩家派系", nameof(faction));
        }

        RimRunnerTargetStance stance = faction.HostileTo(Faction.OfPlayer) ? RimRunnerTargetStance.Hostile : RimRunnerTargetStance.Neutral;
        return new("affiliation=" + faction.def.defName + "#" + faction.loadID + "(" + stance + ") deathOnDowned=" + deathOnDowned,
            stance, faction, deathOnDowned);
    }

    internal RimRunnerTargetStance? Stance { get; }

    internal Faction? Faction { get; }

    internal RimRunnerTargetDeathOnDowned DeathOnDowned { get; }

    /// <summary>归属全文，供报错与读数。</summary>
    public override string ToString() => label;
}

/// <summary>被测体生成请求强制的异种。</summary>
public sealed class RimRunnerSubjectXenotype
{
    private readonly XenotypeDef? def;
    private readonly bool baseliner;

    private RimRunnerSubjectXenotype(XenotypeDef? def, bool baseliner)
    {
        this.def = def;
        this.baseliner = baseliner;
    }

    /// <summary>异种不强制：异种由 kind、派系与意识形态 meme 的 xenotypeSet 掷。</summary>
    /// <remarks>无派系时同样只按这几处掷，沿用 kind、派系与意识形态 meme 的 xenotypeSet。有效正权重候选不止一种、或实得异种与唯一候选不匹配时记进失败指纹。</remarks>
    public static RimRunnerSubjectXenotype Unforced { get; } = new(null, baseliner: false);

    /// <summary>Baseliner 异种：人形强制 Baseliner；非人形沿用生成请求。</summary>
    /// <remarks>无 Biotech 时沿用生成请求。</remarks>
    public static RimRunnerSubjectXenotype Baseliner { get; } = new(null, baseliner: true);

    /// <summary>强制给定异种。</summary>
    public static RimRunnerSubjectXenotype Of(XenotypeDef xenotype)
        => new(xenotype ?? throw new ArgumentNullException(nameof(xenotype), RimRunnerLog.Prefix + " Of 须给异种；不强制写 Unforced"),
            baseliner: false);

    internal XenotypeDef? Resolve(bool humanlike) => baseliner ? (humanlike ? XenotypeDefOf.Baseliner : null) : def;

    /// <summary>异种项全文，供报错与读数。</summary>
    public override string ToString() => baseliner ? "Baseliner" : def?.defName ?? "(unforced)";
}
