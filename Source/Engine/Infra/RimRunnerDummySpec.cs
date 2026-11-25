namespace RimRunner;

/// <summary>假人靶规格：从 Default 或 Of(kind) 起手，偏离项走 with。</summary>
/// <remarks>定义层没有需求、心情、story、装备与生成随机；智力档 ToolUser，按 Humanlike 或 Animal 分支的原版逻辑不触发。缺省种族不出血。缺省待机树不走动，邻格有敌对就还手。</remarks>
public sealed record RimRunnerDummySpec : RimRunnerTargetSpecBase
{
    private RimRunnerDummySpec()
    {
    }

    /// <summary>缺省假人（单部位·全 tag），各项即「中性」的定义。</summary>
    /// <remarks>敌对、算威胁、Mobile、承伤 Normal、倒地即死 Off、不许死、系数 1、待机树、种族常量树、不先站定、不写 stat。</remarks>
    public static RimRunnerDummySpec Default => Of(RimRunnerDummies.DefaultKind);

    /// <summary>指定假人：指定一个假人 kind（引擎自带或产品派生的），其余同 Default。</summary>
    /// <remarks>kind 以 RimRunnerDummyKindBase 为父，种族以 RimRunnerDummyBase 为父且不是 Animal；种族不合在生成入口抛，kind 缺父定义的中性项时出 DUMMY-DEF-ACTIVATES。</remarks>
    public static RimRunnerDummySpec Of(PawnKindDef kind)
        => new()
        {
            Kind = kind ?? throw new ArgumentNullException(nameof(kind), RimRunnerLog.Prefix + " RimRunnerDummySpec.Of 须给 kind"),
            Stance = RimRunnerTargetStance.Hostile,
            Threat = true,
            Mobility = RimRunnerPawnMobilityProfile.Mobile,
            Damage = RimRunnerTargetDamage.Normal,
            DeathOnDowned = RimRunnerTargetDeathOnDowned.Off,
            Death = RimRunnerTargetDeath.Forbidden,
            HealthScale = 1f,
            ThinkTree = RimRunnerDummies.WaitTree,
            ConstantThinkTree = null,
            StandFirst = false,
        };

    /// <summary>血量系数，乘进 Pawn.HealthScale；须为大于 0 的有限数。</summary>
    /// <remarks>单部位池 = 150 × 系数；除部位上限外还进疼痛、致死阈值与自然愈合，会流血的假人上还进出血总量。系数须在首次受伤前施加；生成期已带任何伤口时生成入口抛。</remarks>
    public required float HealthScale { get; init; }

    /// <summary>假人主思维树：实例选的主思维树；不许是任何种族的主思维树或常量树。</summary>
    public required ThinkTreeDef ThinkTree { get; init; }

    /// <summary>假人常量树：实例选的常量树；null 即沿用种族常量树（一棵空树）。</summary>
    /// <remarks>不许是任何种族的主思维树或常量树。</remarks>
    public required ThinkTreeDef? ConstantThinkTree { get; init; }

    /// <summary>落图先站定、首次被打断后交回实例的树；只配会走动的树。</summary>
    /// <remarks>站着的是原版 Wait，贴身自动近战照常。</remarks>
    public required bool StandFirst { get; init; }

    /// <summary>stat 底值表（WithStat）。</summary>
    public IReadOnlyDictionary<StatDef, float> StatBases { get; private init; } = new Dictionary<StatDef, float>();

    /// <summary>stat 最终值表（WithStatFinalized）。</summary>
    public IReadOnlyDictionary<StatDef, float> StatsFinalized { get; private init; } = new Dictionary<StatDef, float>();

    /// <summary>stat 底值：追加或替换一项 stat 底值的副本。</summary>
    /// <remarks>写底值：hediff 的 statOffsets、statFactors，衣物与 post-process 照常叠上；worker 改写 GetBaseValueFor 或 GetValueUnfinalized 的 stat 生成时被拒。</remarks>
    public RimRunnerDummySpec WithStat(StatDef stat, float value)
    {
        if (stat == null) throw new ArgumentNullException(nameof(stat), RimRunnerLog.Prefix + " WithStat 须给 stat");
        return this with { StatBases = new Dictionary<StatDef, float>(StatBases) { [stat] = value } };
    }

    /// <summary>stat 最终值：追加或替换一项 stat 最终值的副本。</summary>
    /// <remarks>写最终值：只替换经 StatWorker.GetValue 的读数，修正与 applyPostProcess 一并盖掉；同一 stat 又写 WithStat 在生成入口被拒。</remarks>
    public RimRunnerDummySpec WithStatFinalized(StatDef stat, float value)
    {
        if (stat == null) throw new ArgumentNullException(nameof(stat), RimRunnerLog.Prefix + " WithStatFinalized 须给 stat");
        return this with { StatsFinalized = new Dictionary<StatDef, float>(StatsFinalized) { [stat] = value } };
    }

    /// <summary>规格全文：kind 与各项取值。</summary>
    public override string ToString()
        => "dummy kind=" + Kind.defName + " stance=" + Stance + " threat=" + Threat + " mobility=" + Mobility + " damage=" + Damage
           + " deathOnDowned=" + DeathOnDowned + " death=" + Death
           + " healthScale=" + HealthScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
           + " thinkTree=" + ThinkTree.defName
           + " constantThinkTree=" + (ConstantThinkTree?.defName ?? "(race)")
           + " standFirst=" + StandFirst
           + " statBases=" + FormatStats(StatBases)
           + " statsFinalized=" + FormatStats(StatsFinalized);

    private static string FormatStats(IReadOnlyDictionary<StatDef, float> stats)
        => "{" + string.Join(",", stats.OrderBy(e => e.Key.defName, StringComparer.Ordinal)
            .Select(e => e.Key.defName + "=" + e.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "}";

    private protected override void ThrowIfNullMembers()
    {
        if (ThinkTree == null) throw new ArgumentNullException(nameof(ThinkTree), RimRunnerLog.Prefix + " 假人规格须给思维树");
    }

    private protected override void AddConflicts(List<string> conflicts)
    {
        if (Kind.race.race == null)
        {
            conflicts.Add("kind " + Kind.defName + " 的 race " + Kind.race.defName + " 不是 pawn 种族");
            return;
        }

        if (!Kind.race.HasComp(typeof(RimRunnerDummyComp)))
        {
            conflicts.Add("kind " + Kind.defName + " 的种族不带假人 comp");
        }

        if (Kind.race.race.intelligence == Intelligence.Animal)
        {
            conflicts.Add("kind " + Kind.defName + " 的种族 intelligence 是 Animal：假人会进 manhunter、狩猎、驯服一组池子，"
                + "无派系时残留检查按野物豁免、泄漏不报");
        }

        if (!(HealthScale > 0f) || float.IsInfinity(HealthScale))
        {
            conflicts.Add("healthScale 须为大于 0 的有限数：" + HealthScale);
        }

        AddTreeConflicts(conflicts, "思维树", ThinkTree);
        if (ConstantThinkTree != null) AddTreeConflicts(conflicts, "常量树", ConstantThinkTree);
        if (StandFirst && !RimRunnerDummyThinkTreeExtension.Of(ThinkTree).moves)
        {
            conflicts.Add("先站定配不走动的思维树 " + ThinkTree.defName + "：站定期间本就不动，没有可交回的行动");
        }

        AddFiniteConflicts(conflicts, "WithStat", StatBases);
        AddFiniteConflicts(conflicts, "WithStatFinalized", StatsFinalized);
        foreach (StatDef stat in StatBases.Keys)
        {
            if (StatsFinalized.ContainsKey(stat))
            {
                conflicts.Add("stat " + stat.defName + " 同时写 WithStat 与 WithStatFinalized：最终值盖过底值，底值不起作用");
            }

            AddBaseHookConflict(conflicts, stat);
        }
    }

    private static void AddFiniteConflicts(List<string> conflicts, string method, IReadOnlyDictionary<StatDef, float> stats)
    {
        foreach (KeyValuePair<StatDef, float> entry in stats)
        {
            if (float.IsNaN(entry.Value) || float.IsInfinity(entry.Value))
            {
                conflicts.Add(method + " " + entry.Key.defName + " 须为有限数：" + entry.Value);
            }
        }
    }

    private static void AddBaseHookConflict(List<string> conflicts, StatDef stat)
    {
        if (BaseHookBypasser(stat.Worker.GetType(), out string name) is { } declaring)
        {
            RimRunnerEngineLog.Mark(RimRunnerObservationKeys.DummyBaseValueGate,
                "WithStat(" + stat.defName + ") 被底值闸拦截，worker " + declaring.FullName + " 改写 " + name);
            conflicts.Add("stat " + stat.defName + " 的 worker " + declaring.FullName + " 改写 " + name
                + "：读数不保证经 StatWorker.GetBaseValueFor 的底值");
        }
    }

    internal static Type? BaseHookBypasser(Type worker, out string method)
    {
        foreach ((string name, Type[] parameters) in BaseHookBypassers)
        {
            Type? declaring = worker.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, parameters, null)?.DeclaringType;
            if (declaring != null && declaring != typeof(StatWorker))
            {
                method = name;
                return declaring;
            }
        }

        method = "";
        return null;
    }

    private static readonly (string, Type[])[] BaseHookBypassers =
    [
        (nameof(StatWorker.GetBaseValueFor), [typeof(StatRequest)]),
        (nameof(StatWorker.GetValueUnfinalized), [typeof(StatRequest), typeof(bool)])
    ];

    private void AddTreeConflicts(List<string> conflicts, string role, ThinkTreeDef tree)
    {
        ThinkTreeDef? registered = DefDatabase<ThinkTreeDef>.GetNamedSilentFail(tree.defName);
        if (registered != null && registered != tree)
        {
            RimRunnerSentinel.Warn(RimRunnerWarningKey.ThinkTreeUnregistered,
                "假人规格的思维树与 DefDatabase 同名项不是同一对象：跨载档读档按 defName 会找回 DefDatabase 里那一棵",
                role + " " + tree.defName);
        }

        foreach (ThingDef race in DefDatabase<ThingDef>.AllDefsListForReading)
        {
            if (race.race != null && (race.race.thinkTreeMain == tree || race.race.thinkTreeConstant == tree))
            {
                conflicts.Add(role + " " + tree.defName + " 是种族 " + race.defName + " 的主树或常量树，实例不得选");
                break;
            }
        }

        bool declared = RimRunnerDummyThinkTreeExtension.IsDeclared(tree);
        List<string> found = [];
        RimRunnerDummyThinkTreeExtension traits = RimRunnerDummyThinkTreeExtension.Of(tree);
        if (traits.moves && Kind.race.race.doesntMove)
        {
            found.Add("种族 " + Kind.race.defName + " 是 doesntMove，" + role + " " + tree.defName + " 会走动");
        }

        if ((traits.moves || traits.initiates) && Mobility != RimRunnerPawnMobilityProfile.Mobile)
        {
            found.Add("行动档 " + Mobility + " 的身体不能自发行动，" + role + " " + tree.defName + " 会走动或主动出手");
        }

        if (traits.attacks && (!Kind.canMeleeAttack || Kind.race.tools.NullOrEmpty()))
        {
            found.Add(role + " " + tree.defName + " 会出手，kind " + Kind.defName + " 没有近战手段（canMeleeAttack="
                + Kind.canMeleeAttack + "，种族 tools " + (Kind.race.tools?.Count ?? 0) + " 项）");
        }

        if (declared && found.Count > 0)
        {
            RimRunnerSentinel.Warn(RimRunnerWarningKey.DummyTreeMismatch,
                "假人规格的树与身体不匹配：树声明的行为在这副身体上无法做到，仍生成",
                tree.defName + "：" + string.Join("；", found));
        }
        else if (found.Count > 0)
        {
            RimRunnerSentinel.Warn(RimRunnerWarningKey.UndeclaredThinkTree,
                "假人规格未经核实：树未带 RimRunnerDummyThinkTreeExtension 声明，按会走动、会出手、主动出手判出下列不匹配项",
                tree.defName + "：" + string.Join("；", found));
        }
    }
}
