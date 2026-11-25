namespace RimRunner;

/// <summary>靶规格公共部分：两种靶都有意义的项与公共校验。</summary>
/// <remarks>跨项的互斥组合在生成入口整份校验，一次抛全部。</remarks>
public abstract record RimRunnerTargetSpecBase
{
    private protected RimRunnerTargetSpecBase()
    {
    }

    /// <summary>物种。</summary>
    public required PawnKindDef Kind { get; init; }

    /// <summary>派系立场。</summary>
    public required RimRunnerTargetStance Stance { get; init; }

    /// <summary>威胁标记：false 时殖民者的敌意响应、炮塔与 AI 搜敌不以该 pawn 为目标。</summary>
    /// <remarks>false 等同生成后对该 pawn 调 ctx.DisableThreat，只改别人是否以该 pawn 为目标；true 照 vanilla 算威胁。</remarks>
    public required bool Threat { get; init; }

    /// <summary>行动档。</summary>
    /// <remarks>非 Mobile 的真实物种靶须配 agency=Pinned。Stunned 不管承伤，要靶全程站立，配 damage=Absorbed。</remarks>
    public required RimRunnerPawnMobilityProfile Mobility { get; init; }

    /// <summary>承伤档。</summary>
    public required RimRunnerTargetDamage Damage { get; init; }

    /// <summary>非玩家派系被打倒地时的随机致死掷骰。</summary>
    public required RimRunnerTargetDeathOnDowned DeathOnDowned { get; init; }

    /// <summary>是否许本用例里死亡。</summary>
    public required RimRunnerTargetDeath Death { get; init; }

    /// <summary>kind 的种族是否人形。</summary>
    public bool Humanlike => Kind.race?.race?.Humanlike ?? false;

    internal void Validate()
    {
        if (Kind == null) throw new ArgumentNullException(nameof(Kind), RimRunnerLog.Prefix + " 靶规格须给 kind");
        if (Death == null)
        {
            throw new ArgumentNullException(nameof(Death),
                RimRunnerLog.Prefix + " 靶规格须给 death");
        }

        ThrowIfNullMembers();
        List<string> conflicts = [];
        if (Kind.race == null)
        {
            conflicts.Add("kind " + Kind.defName + " 没有 race");
        }

        if (Damage == RimRunnerTargetDamage.Absorbed && Death.Excused)
        {
            conflicts.Add("中止伤害的靶不会死，death 不能是 Excused");
        }

        if (Kind.race?.race is { doesntMove: true } && Mobility == RimRunnerPawnMobilityProfile.AwakeImmobile)
        {
            conflicts.Add("种族 " + Kind.race.defName + " 是 doesntMove，行动档不能配 AwakeImmobile");
        }

        if (Kind.race != null)
        {
            AddConflicts(conflicts);
        }

        if (conflicts.Count > 0)
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " 靶规格自相矛盾（" + Kind.defName + "）：" + string.Join("；", conflicts));
        }
    }

    private protected abstract void ThrowIfNullMembers();

    private protected abstract void AddConflicts(List<string> conflicts);
}

/// <summary>真实物种靶的规格：每项 required；常用角色走静态组合，偏离项走 with。</summary>
public sealed record RimRunnerTargetSpec : RimRunnerTargetSpecBase
{
    /// <summary>敌对人形组合：敌对人形靶（Pirate、钉住，清 trait 与随身物、定 Baseliner）。</summary>
    /// <remarks>行动档 Stunned 时承伤取 Absorbed，其余取 Normal。</remarks>
    public static RimRunnerTargetSpec HostileHuman(RimRunnerPawnViolenceProfile violence, RimRunnerPawnMobilityProfile mobility)
        => new()
        {
            Kind = DefDatabase<PawnKindDef>.GetNamed("Pirate"),
            Stance = RimRunnerTargetStance.Hostile,
            Agency = RimRunnerTargetAgency.Pinned,
            Violence = violence,
            Threat = true,
            Needs = RimRunnerTargetNeeds.Natural,
            HediffBreaks = RimRunnerTargetHediffBreaks.Natural,
            Mobility = mobility,
            Damage = DamageFor(mobility),
            Generation = RimRunnerTargetGeneration.ClearTraits | RimRunnerTargetGeneration.ClearLoadout | RimRunnerTargetGeneration.Baseliner,
            DeathOnDowned = RimRunnerTargetDeathOnDowned.Normal,
            Death = RimRunnerTargetDeath.Forbidden,
        };

    /// <summary>敌对动物组合：敌对动物靶（kind 为 null 取 Megasloth，钉住、成年）。</summary>
    /// <remarks>行动档 Stunned 时承伤取 Absorbed，其余取 Normal。</remarks>
    public static RimRunnerTargetSpec HostileBeast(PawnKindDef? kind, RimRunnerPawnMobilityProfile mobility)
        => new()
        {
            Kind = kind ?? DefDatabase<PawnKindDef>.GetNamed("Megasloth"),
            Stance = RimRunnerTargetStance.Hostile,
            Agency = RimRunnerTargetAgency.Pinned,
            Violence = RimRunnerPawnViolenceProfile.Unrestricted,
            Threat = true,
            Needs = RimRunnerTargetNeeds.Natural,
            HediffBreaks = RimRunnerTargetHediffBreaks.Natural,
            Mobility = mobility,
            Damage = DamageFor(mobility),
            Generation = RimRunnerTargetGeneration.FixedAge | RimRunnerTargetGeneration.ClearTraits | RimRunnerTargetGeneration.ClearLoadout,
            DeathOnDowned = RimRunnerTargetDeathOnDowned.Normal,
            Death = RimRunnerTargetDeath.Forbidden,
        };

    /// <summary>组合：先站定再放开 AI 的敌对人形靶，其余同 HostileHuman。</summary>
    /// <remarks>即行动档 Mobile、Agency 取 StandThenFree 的 HostileHuman。</remarks>
    public static RimRunnerTargetSpec StandThenFreeHuman(RimRunnerPawnViolenceProfile violence)
        => HostileHuman(violence, RimRunnerPawnMobilityProfile.Mobile) with { Agency = RimRunnerTargetAgency.StandThenFree };

    /// <summary>中立旁观者组合：中立旁观者（中立派系固定成员 kind、钉住、生成随机全清）。</summary>
    /// <remarks>世界上没有可扣好感且取得出成员 kind 的中立派系时，同 RimRunnerFactions.FindNeutral 认领为前提故障：用例窗里结果为 SENTINEL_FAULT。</remarks>
    /// <exception cref="InvalidOperationException">没有合格中立派系且在用例窗、场面与分片枚举之外调用时抛，文案带成因。</exception>
    public static RimRunnerTargetSpec NeutralBystander()
    {
        Faction faction = RimRunnerFactions.FindNeutral();
        return new()
        {
            Kind = RimRunnerFactions.MemberKind(faction)!,
            Stance = RimRunnerTargetStance.Neutral,
            Agency = RimRunnerTargetAgency.Pinned,
            Violence = RimRunnerPawnViolenceProfile.Unrestricted,
            Threat = true,
            Needs = RimRunnerTargetNeeds.Natural,
            HediffBreaks = RimRunnerTargetHediffBreaks.Natural,
            Mobility = RimRunnerPawnMobilityProfile.Mobile,
            Damage = RimRunnerTargetDamage.Normal,
            Generation = RimRunnerTargetGeneration.All,
            DeathOnDowned = RimRunnerTargetDeathOnDowned.Normal,
            Death = RimRunnerTargetDeath.Forbidden,
        };
    }

    /// <summary>自主 AI 档。</summary>
    /// <remarks>Pinned 改成无期限 Wait 并压掉其贴身自动近战。</remarks>
    public required RimRunnerTargetAgency Agency { get; init; }

    /// <summary>暴力档；非人形只许 Unrestricted。</summary>
    public required RimRunnerPawnViolenceProfile Violence { get; init; }

    /// <summary>需求档。</summary>
    public required RimRunnerTargetNeeds Needs { get; init; }

    /// <summary>hediff 阶段崩溃档。</summary>
    public required RimRunnerTargetHediffBreaks HediffBreaks { get; init; }

    /// <summary>生成后清掉的随机项。</summary>
    public required RimRunnerTargetGeneration Generation { get; init; }

    /// <summary>规格全文：逐项写出 名=值。</summary>
    public override string ToString()
        => "kind=" + Kind.defName + " stance=" + Stance + " agency=" + Agency + " violence=" + Violence
           + " threat=" + Threat + " needs=" + Needs + " hediffBreaks=" + HediffBreaks + " mobility=" + Mobility + " damage=" + Damage
           + " generation=" + Generation + " deathOnDowned=" + DeathOnDowned + " death=" + Death;

    private static RimRunnerTargetDamage DamageFor(RimRunnerPawnMobilityProfile mobility)
        => mobility == RimRunnerPawnMobilityProfile.Stunned ? RimRunnerTargetDamage.Absorbed : RimRunnerTargetDamage.Normal;

    private protected override void ThrowIfNullMembers()
    {
    }

    private protected override void AddConflicts(List<string> conflicts)
    {
        if (Mobility != RimRunnerPawnMobilityProfile.Mobile && Agency != RimRunnerTargetAgency.Pinned)
        {
            conflicts.Add("行动档 " + Mobility + " 的身体无法提供 agency=" + Agency);
        }

        if (Humanlike && Stance == RimRunnerTargetStance.Player)
        {
            conflicts.Add("靶规格不接受人形 kind 配玩家立场：这一组合是被测体或对照组");
        }

        if (!Humanlike && Violence != RimRunnerPawnViolenceProfile.Unrestricted)
        {
            conflicts.Add("非人形 kind 只配 violence=Unrestricted");
        }
    }
}

/// <summary>靶派系立场：靶落到哪个派系立场。</summary>
public enum RimRunnerTargetStance
{
    /// <summary>敌对立场：取存档里任意一个敌对的人形派系。</summary>
    Hostile,

    /// <summary>中立立场：取一个可扣好感的中立派系。</summary>
    Neutral,

    /// <summary>玩家立场：取玩家派系，只给非人形 kind。</summary>
    Player
}

/// <summary>靶自主 AI 档：只在行动档 Mobile 下有别，其余三档须写 Pinned。</summary>
public enum RimRunnerTargetAgency
{
    /// <summary>钉住：自发 job 改成无期限 Wait，挡随机精神态与社交斗殴。</summary>
    /// <remarks>连带压掉原版 Wait 的贴身自动近战；job 结束即回落 Wait。</remarks>
    Pinned,

    /// <summary>自主 AI：靶自己的思维树照常运行。</summary>
    Free,

    /// <summary>先站定：落图先起一段永不到期的 Wait，首次被打断后交回思维树。</summary>
    /// <remarks>站立用原版 Wait，贴身自动近战照常。</remarks>
    StandThenFree
}

/// <summary>靶需求档：靶的需求按哪一档推进。</summary>
public enum RimRunnerTargetNeeds
{
    /// <summary>需求照常推进。</summary>
    Natural,

    /// <summary>需求冻结：先填满再冻结，用例末还原。</summary>
    Frozen
}

/// <summary>hediff 阶段崩溃档：靶的 hediff 阶段崩溃按哪一档处理。</summary>
public enum RimRunnerTargetHediffBreaks
{
    /// <summary>照常：带 mentalBreakMtbDays 的 hediff 掷中即发起崩溃。</summary>
    Natural,

    /// <summary>屏蔽：掷中后不发起崩溃，对象回收时撤。</summary>
    Blocked
}

/// <summary>承伤档：靶按哪一档承受伤害。</summary>
public enum RimRunnerTargetDamage
{
    /// <summary>照常承伤。</summary>
    Normal,

    /// <summary>中止伤害：命中在结算前整笔吸收，伤型效果与护甲都不结算。</summary>
    /// <remarks>只吸收经 Thing.TakeDamage 的命中，直接 AddHediff 的伤口与 hediff 照常挂上；与 Death 许死互斥。</remarks>
    Absorbed,

    /// <summary>关闭伤口：完整结算伤害，丢弃伤口、本部位附加 hediff 与衣物磨损。</summary>
    /// <remarks>只作用于经 Thing.TakeDamage 且 workerClass 属 DamageWorker_AddInjury 一族的伤型；每笔实伤按部位当前血量截断，伤害结果照原版记实伤与受伤部位。</remarks>
    Woundless
}

/// <summary>生成清理项：生成后清掉的随机项，可组合。</summary>
[Flags]
public enum RimRunnerTargetGeneration
{
    /// <summary>生成随机全留。</summary>
    None = 0,

    /// <summary>清空生成期 hediff（植入体、成瘾、种族强挂等）。</summary>
    ClearHediffs = 1,

    /// <summary>年龄钉固定值：孩子 kind 取其未成年段中点，其余 25 岁（靶另断言落在末生命阶段）。</summary>
    FixedAge = 2,

    /// <summary>清随机 trait。</summary>
    ClearTraits = 4,

    /// <summary>清空随身物：销毁衣装、武器与行囊。</summary>
    ClearLoadout = 8,

    /// <summary>背景换成零 workDisables 的中性档。</summary>
    NeutralBackstory = 16,

    /// <summary>强制 Baseliner：人形按 Baseliner 异种生成，非人形沿用生成请求。</summary>
    /// <remarks>只在真实物种靶生成时起作用，RimRunnerPawns.ApplyGeneration 不认它；被测体规格里不许写。</remarks>
    Baseliner = 32,

    /// <summary>以上全部。</summary>
    All = ClearHediffs | FixedAge | ClearTraits | ClearLoadout | NeutralBackstory | Baseliner
}

/// <summary>倒地即死掷骰：非玩家派系被打倒地时 vanilla 的随机致死掷骰。</summary>
public enum RimRunnerTargetDeathOnDowned
{
    /// <summary>照 vanilla 掷。</summary>
    Normal,

    /// <summary>倒地即死关闭：倒地时跳过致死掷骰。</summary>
    /// <remarks>把 pawn.health.overrideDeathOnDownedChance 置 0，随存档保存。</remarks>
    Off
}

/// <summary>靶是否许在本用例里死亡；许死须带理由。</summary>
public sealed class RimRunnerTargetDeath
{
    private RimRunnerTargetDeath(bool excused, string? reason)
    {
        Excused = excused;
        Reason = reason;
    }

    /// <summary>不许死亡：靶死亡照常进失败指纹。</summary>
    public static RimRunnerTargetDeath Forbidden { get; } = new(false, null);

    /// <summary>许死：理由走 ctx.ExcuseDeath，为空即抛。</summary>
    public static RimRunnerTargetDeath ExcusedBecause(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " RimRunnerTargetDeath.ExcusedBecause 须写明理由", nameof(reason));
        }

        return new RimRunnerTargetDeath(true, reason);
    }

    /// <summary>是否许死。</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Reason))]
    public bool Excused { get; }

    /// <summary>许死理由：许死的理由；不许时为 null。</summary>
    public string? Reason { get; }

    /// <summary>读数文本。</summary>
    public override string ToString() => Excused ? "Excused(" + Reason + ")" : "Forbidden";
}
