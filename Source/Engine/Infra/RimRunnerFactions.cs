namespace RimRunner;

/// <summary>派系好感前置：派系好感断言的共享前置，取中立派系并把基线架到 0。</summary>
public static class RimRunnerFactions
{
    /// <summary>取一个可扣好感的中立派系。</summary>
    /// <remarks>取第一个非玩家、非隐藏、非临时、非永久敌对、当前不敌对玩家且取得出成员 kind 的人形派系。没有时经 RimRunnerFault.Require 认领：用例窗里结果为 SENTINEL_FAULT。</remarks>
    /// <exception cref="InvalidOperationException">没有合格派系且在用例窗、场面与分片枚举之外调用时抛，文案带成因。</exception>
    public static Faction FindNeutral() => FindNeutralIn(Find.FactionManager.AllFactionsListForReading);

    internal static Faction FindNeutralIn(IEnumerable<Faction> factions)
    {
        foreach (Faction f in factions)
        {
            if (!f.IsPlayer && !f.def.permanentEnemy && !f.def.hidden && !f.temporary
                && !f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction
                && MemberKind(f) != null)
            {
                return f;
            }
        }

        RimRunnerFault.Require(false, NoNeutralReason, "中立派系");
        throw new InvalidOperationException(NoNeutralReason);
    }

    internal const string NoNeutralReason = "世界上没有可扣好感且能生成人形成员的中立派系";

    internal static Faction FindHostileIn(IEnumerable<Faction> factions)
    {
        foreach (Faction candidate in factions)
        {
            if (!candidate.IsPlayer && candidate.def.humanlikeFaction && candidate.HostileTo(Faction.OfPlayer))
            {
                return candidate;
            }
        }

        RimRunnerFault.Require(false, NoHostileReason, "敌对派系");
        throw new InvalidOperationException(NoHostileReason);
    }

    internal const string NoHostileReason = "无法生成敌对立场的 pawn：存档里没有敌对人形派系";

    /// <summary>派系的固定成员 kind；无法取得时返回 null。</summary>
    /// <remarks>取 basicMemberKind，为空时取 pawnGroupMakers 里按定义顺序的首个人形 option。</remarks>
    public static PawnKindDef? MemberKind(Faction faction)
    {
        if (faction.def.basicMemberKind != null)
        {
            return faction.def.basicMemberKind;
        }

        foreach (PawnGroupMaker maker in faction.def.pawnGroupMakers ?? [])
        {
            foreach (PawnGenOption option in maker.options)
            {
                if (option.kind.RaceProps.Humanlike)
                {
                    return option.kind;
                }
            }
        }

        return null;
    }

    /// <summary>好感基线：把好感基线架到 0，并登记本 phase 末还原到调用前的值。</summary>
    /// <remarks>还原经 RestoreGoodwill，玩家正攻打该派系据点时同样被拒；调用前须保证据点已回收。</remarks>
    public static void ArmBaseline(RimRunnerContext ctx, Faction faction, string tag)
    {
        int before = faction.PlayerGoodwill;
        RestoreGoodwill(faction, 0, tag + " 架基线");
        ctx.Defer(() => RestoreGoodwill(faction, before, tag + " 还原中立派系好感"),
            tag + " 还原中立派系好感");
    }

    /// <summary>把好感精确写到 target。</summary>
    /// <remarks>写不到 target 即抛；玩家正攻打该派系任一据点时正向变更被静默拒绝，调用前须保证据点已回收。</remarks>
    public static void RestoreGoodwill(Faction faction, int target, string tag)
    {
        Find.GoodwillSituationManager.RecalculateAll(canSendHostilityChangedLetter: false);

        for (int i = 0; i < 200 && faction.PlayerGoodwill != target; i++)
        {
            int before = faction.PlayerGoodwill;
            if (!Faction.OfPlayer.TryAffectGoodwillWith(faction, target - before,
                    canSendMessage: false, canSendHostilityLetter: false))
            {
                break;
            }

            if (faction.PlayerGoodwill == before) break;
        }

        if (faction.PlayerGoodwill != target)
        {
            throw new InvalidOperationException(
                $"{RimRunnerLog.Prefix} {tag}：派系 {faction.Name} 的好感停在 {faction.PlayerGoodwill}，"
                + $"未到 {target}");
        }
    }
}
