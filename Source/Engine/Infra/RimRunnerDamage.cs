namespace RimRunner;

/// <summary>伤害注入：构造关内传播的 DamageInfo，选可受伤的落点部位。</summary>
public static class RimRunnerDamage
{
    /// <summary>穿透值：恒穿甲。</summary>
    /// <remarks>护甲值不超过 2（vanilla 护甲 stat 的上限）时折减恒为 0。落伤另乘目标的 IncomingDamageFactor（仅 externalViolence），全额断言须计入。</remarks>
    public const float ArmorPenFull = 2f;
    /// <summary>穿透值 999：任何护甲值下都全额穿透。</summary>
    public const float ArmorPenLethal = 999f;

    /// <summary>按护甲值算出恒定弹开的穿透值。</summary>
    /// <remarks>穿透 ≤ 护甲 − 2 使弹开带 ≥ 1，恒定弹开。护甲 roll 照抽一次，弹开结果不随这次抽取变。</remarks>
    public static float ArmorPenDeflect(float armorRating) => armorRating - 2f;

    /// <summary>构造一条关内传播的 DamageInfo。</summary>
    /// <remarks>angle 缺省 0，不小于 0 时构造不消耗 Rand。part 为 null 时受击部位由 vanilla 随机选。</remarks>
    /// <exception cref="InvalidOperationException">part 的 coverageAbs 为 0 时抛。</exception>
    public static DamageInfo Make(
        DamageDef def, float amount, Pawn? instigator, BodyPartRecord? part,
        float armorPen = ArmorPenFull, float angle = 0f)
    {
        AssertInjurablePart(part);
        DamageInfo dinfo = new(def, amount, armorPen, angle, instigator, part);
        dinfo.SetAllowDamagePropagation(false);
        return dinfo;
    }

    /// <summary>护甲 roll 之前已消耗的 Rand 抽数。</summary>
    /// <remarks>前提：窗口内只有一次 pawn.TakeDamage(dinfo)，dinfo 经 Make 构造、part 与本方法同一非空部位。护甲 roll 是窗口内第（本值 + 1）次抽取。pawn 穿着衣物时判 SENTINEL_FAULT。</remarks>
    public static int RandDrawsBeforeArmorRoll(Pawn pawn, BodyPartRecord part, DamageDef def)
    {
        string? blocker = ArmorRollModelBlocker(pawn, part);
        RimRunnerFault.Require(blocker == null, blocker, pawn.LabelShort);
        return pawn.RaceProps.IsFlesh && def.ExternalViolenceFor(pawn) ? 2 : 0;
    }

    internal static string? ArmorRollModelBlocker(Pawn pawn, BodyPartRecord? part)
    {
        if (part == null)
        {
            return "护甲 roll 计数要求显式受击部位：part 为 null 会走 ChooseHitPart 抽 Rand";
        }

        int worn = pawn.apparel?.WornApparelCount ?? 0;
        if (worn > 0)
        {
            return "护甲 roll 计数只对裸身 pawn 成立：穿着 " + worn + " 件衣物，着装 tale 与衣物自损链的抽数不可建模";
        }

        return null;
    }

    /// <summary>取落点：未缺失、coverageAbs 大于 0 的部位里最大血量最高的那个。</summary>
    /// <exception cref="InvalidOperationException">没有这样的部位时抛。</exception>
    public static BodyPartRecord PickInjuryPart(Pawn pawn)
    {
        BodyPartRecord part = EnumerateInjuryParts(pawn).FirstOrDefault() ?? throw new InvalidOperationException(
                RimRunnerLog.Prefix + " " + pawn + " 没有 coverageAbs>0 的可受伤部位；dead=" + pawn.Dead
                + " hediffs=[" + string.Join(", ", pawn.health.hediffSet.hediffs.Select(h => h.def.defName + (h.Part != null ? "@" + h.Part.def.defName : ""))) + "]");
        return part;
    }

    /// <summary>候选落点：未缺失、coverageAbs 大于 0 的部位，按最大血量再按覆盖率降序。</summary>
    public static IEnumerable<BodyPartRecord> EnumerateInjuryParts(Pawn pawn)
    {
        return pawn.health.hediffSet.GetNotMissingParts()
            .Where(part => part.coverageAbs > 0f)
            .OrderByDescending(part => part.def.GetMaxHealth(pawn))
            .ThenByDescending(part => part.coverageAbs);
    }

    private static void AssertInjurablePart(BodyPartRecord? part)
    {
        if (part != null && part.coverageAbs <= 0f)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 落点 " + part.def.defName + " 的 coverageAbs=0，"
                + "injury 不会落地");
        }
    }
}
