namespace RimRunner;

internal static class RimRunnerDummyDefAudit
{
    private sealed class Field
    {
        internal readonly string Path;
        internal readonly string Effect;
        internal readonly Func<PawnKindDef, bool> Written;

        internal Field(string path, string effect, Func<PawnKindDef, bool> written)
        {
            Path = path;
            Effect = effect;
            Written = written;
        }
    }

    private static readonly Field[] KindFields =
    [
        new("weaponTags", "配 weaponMoney > 0，能操作且未禁暴力的假人生成期按 tag 在预算内发武器",
            k => !k.weaponTags.NullOrEmpty() && k.weaponMoney.max > 0f
                 && k.RaceProps.ToolUser
                 && k.RaceProps.body.HasPartWithTag(BodyPartTagDefOf.ManipulationLimbCore)
                 && (k.disabledWorkTags & WorkTags.Violent) == 0),
        new("techHediffsRequired", "生成期装上列出的植入体",
            k => !k.techHediffsRequired.NullOrEmpty()),
        new("techHediffsTags", "配 techHediffsChance > 0，生成期按 tag 从植入体池随机装",
            k => !k.techHediffsTags.NullOrEmpty() && k.techHediffsChance > 0f),
        new("startingHediffs", "生成期挂上列出的 hediff",
            k => !k.startingHediffs.NullOrEmpty()),
        new("fixedInventory", "生成期把物品放进背包、装备栏或穿上",
            k => !k.fixedInventory.NullOrEmpty()),
        new("inventoryOptions", "生成期把物品放进背包、装备栏或穿上",
            k => k.inventoryOptions != null),
        new("abilities", "带上列出的能力",
            k => !k.abilities.NullOrEmpty()),
        new("mutant", "生成即变成 mutant",
            k => k.mutant != null),
        new("missingParts", "body 有可截的外部部位，生成期截掉",
            k => !k.missingParts.NullOrEmpty() && BodyHas(k, p => p.depth == BodyPartDepth.Outside
                && (p.def.permanentInjuryChanceFactor != 0f || p.def.pawnGeneratorCanAmputate))),
        new("existingDamage", "body 有可生疤的部位，生成期带永久伤",
            k => !k.existingDamage.NullOrEmpty() && BodyHas(k, p => p.def.canScarify)),
        new("combatPower", "大于 0，敌对落图时计入地图危险度，按点数选取 kind 的池可选中它",
            k => k.combatPower > 0f),
        new("allowOldAgeInjuries", "为 true，body 有可伤的外部部位，生成期按年龄掷老年伤",
            k => k.allowOldAgeInjuries && BodyHas(k, p => p.depth == BodyPartDepth.Outside
                && (p.def.permanentInjuryChanceFactor != 0f || p.def.pawnGeneratorCanAmputate))),
        new("canBeScattered", "为 true，进古代建筑干尸的 kind 池",
            k => k.canBeScattered && k.RaceProps.IsFlesh),
        new("trader", "为 true，进合成 tale 的商人抽签池",
            k => k.trader),
        new("titleSelectOne", "进按头衔抽 kind 的任务池",
            k => !k.titleSelectOne.NullOrEmpty()),
    ];

    private static readonly Field[] RaceFields =
    [
        new("race.wildBiomes", "进该生物群系的野生动物刷出池，不看智力档",
            k => !k.RaceProps.wildBiomes.NullOrEmpty()),
        new("race.hediffGiverSets", "运行期按 giver 长 hediff",
            k => k.RaceProps.hediffGiverSets?.Any(set => set.defName != BleedingGiverSetName) == true),
        new("race.detritusLeavings", "死亡时按几率刷出物品",
            k => !k.RaceProps.detritusLeavings.NullOrEmpty()),
        new("race.trainableTags", "有派系时 tag 命中的训练项可训",
            k => !k.RaceProps.trainableTags.NullOrEmpty()),
        new("race.specialTrainables", "有派系时列出的特殊训练项可训",
            k => !k.RaceProps.specialTrainables.NullOrEmpty()),
        new("race.canBecomeShambler", "为 true，尸体可被复活成 shambler",
            k => k.RaceProps.canBecomeShambler),
        new("race.fleshType", "是机械体，种族进机械体的池子与充电、工作分配",
            k => k.RaceProps.IsMechanoid),
        new("tradeTags", "带同名 tag 的商人库存把种族 def 当商品生成",
            k => !k.race.tradeTags.NullOrEmpty() && k.race.tradeability != Tradeability.None),
        new("thingCategories", "按分类生成库存的商人把种族 def 当该分类商品",
            k => !k.race.thingCategories.NullOrEmpty() && k.race.tradeability != Tradeability.None),
        new("killedLeavings", "死亡时掉落列出的物品",
            k => !k.race.killedLeavings.NullOrEmpty()),
        new("killedLeavingsPlayerHostile", "对玩家敌对时死亡掉落列出的物品",
            k => !k.race.killedLeavingsPlayerHostile.NullOrEmpty()),
        new("killedLeavingsRanges", "死亡时掉落列出的物品",
            k => !k.race.killedLeavingsRanges.NullOrEmpty()),
        new("butcherProducts", "屠宰时另产出列出的物品",
            k => !k.race.butcherProducts.NullOrEmpty()),
        new("equipmentType", "为 Primary 且写 weaponTags，种族 def 进武器候选池",
            k => k.race.equipmentType == EquipmentType.Primary && !k.race.weaponTags.NullOrEmpty()),
        new("isTechHediff", "为 true 且写 techHediffsTags，种族 def 进植入体候选池",
            k => k.race.isTechHediff && !k.race.techHediffsTags.NullOrEmpty()),
    ];

    private const string BleedingGiverSetName = "RimRunnerDummy_Bleeding";

    private static readonly Dictionary<PawnKindDef, List<KeyValuePair<string, string>>> cache = [];

    private static readonly HashSet<PawnKindDef> warnedThisCase = [];

    internal static void BeginCase() => warnedThisCase.Clear();

    internal static void WarnOnce(PawnKindDef kind)
    {
        if (!warnedThisCase.Add(kind)) return;
        foreach (KeyValuePair<string, string> finding in Audit(kind))
        {
            RimRunnerSentinel.Warn(finding.Key, Reasons[finding.Key], finding.Value);
        }
    }

    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        [RimRunnerWarningKey.DummyDefActivates] = "假人定义写会激活 vanilla 行为的字段，假人不再中性",
        [RimRunnerWarningKey.InertDeclaration] = "RimRunnerDummyIntentExtension 列一个不起作用的字段",
        [RimRunnerWarningKey.EmptyReason] = "RimRunnerDummyIntentExtension 的 reason 为空",
    };

    internal static List<KeyValuePair<string, string>> Audit(PawnKindDef kind)
    {
        if (cache.TryGetValue(kind, out List<KeyValuePair<string, string>>? found)) return found;
        found = [];
        Check(found, "kind " + kind.defName, kind.GetModExtension<RimRunnerDummyIntentExtension>(), KindFields, kind);
        Check(found, "种族 " + kind.race.defName, kind.race.GetModExtension<RimRunnerDummyIntentExtension>(), RaceFields, kind);
        cache[kind] = found;
        return found;
    }

    private static void Check(List<KeyValuePair<string, string>> found, string owner, RimRunnerDummyIntentExtension intent,
        Field[] fields, PawnKindDef kind)
    {
        HashSet<string> declared = new(intent?.fields ?? [], StringComparer.Ordinal);
        List<string> hits = [];
        foreach (Field field in fields)
        {
            if (!field.Written(kind)) continue;
            if (!declared.Remove(field.Path)) hits.Add(field.Path + "（" + field.Effect + "）");
        }

        if (hits.Count > 0)
        {
            found.Add(new KeyValuePair<string, string>(RimRunnerWarningKey.DummyDefActivates, owner + "：" + string.Join("；", hits)));
        }

        foreach (string inert in declared)
        {
            found.Add(new KeyValuePair<string, string>(RimRunnerWarningKey.InertDeclaration, owner + " " + inert
                + (fields.Any(field => field.Path == inert) ? "（该 def 未写到会激活的程度）" : "（不在判据表里）")));
        }

        if (intent != null && string.IsNullOrWhiteSpace(intent.reason))
        {
            found.Add(new KeyValuePair<string, string>(RimRunnerWarningKey.EmptyReason, owner));
        }
    }

    private static bool BodyHas(PawnKindDef kind, Func<BodyPartRecord, bool> predicate)
        => kind.RaceProps.body.AllParts.Any(predicate);
}

/// <summary>假人派生声明：声明有意保留的、会激活原版行为的字段。</summary>
/// <remarks>挂在 PawnKindDef 上的只认 kind 的字段，挂在种族 ThingDef 上的只认种族的字段。生成假人时列出的字段不记激活警告；列不起作用的字段或 reason 为空各记一条警告。</remarks>
public sealed class RimRunnerDummyIntentExtension : DefModExtension
{
    /// <summary>故意写的字段，写 XML 路径，如 weaponTags、race.wildBiomes。</summary>
    public List<string> fields = [];

    /// <summary>理由：为什么写这些字段。</summary>
    public string? reason;
}
