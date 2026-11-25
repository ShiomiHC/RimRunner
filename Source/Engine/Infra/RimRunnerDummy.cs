using Verse.AI;

namespace RimRunner;

/// <summary>假人 comp 的定义层缺省：血量系数、待机树；产品派生假人时可在 XML 里改。</summary>
public sealed class RimRunnerDummyCompProperties : CompProperties
{
    /// <summary>定义层血量系数：定义层缺省血量系数，实例未改时取这个值。</summary>
    public float healthScale = 1f;

    /// <summary>定义层缺省思维树，XML 写 RimRunnerDummy_Wait。</summary>
    public ThinkTreeDef? thinkTree;

    /// <summary>XML 加载用的构造。</summary>
    public RimRunnerDummyCompProperties()
    {
        compClass = typeof(RimRunnerDummyComp);
    }

    /// <summary>定义层配置校验：未指定 thinkTree 或 healthScale ≤ 0 即报配置错误。</summary>
    public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
    {
        foreach (string error in base.ConfigErrors(parentDef))
        {
            yield return error;
        }

        if (thinkTree == null)
        {
            yield return "RimRunnerDummyCompProperties.thinkTree 未指定：假人实例缺省要一棵不被任何种族引用的树";
        }

        if (healthScale <= 0f)
        {
            yield return "RimRunnerDummyCompProperties.healthScale 须大于 0：" + healthScale;
        }
    }
}

/// <summary>假人实例参数：血量系数、主思维树与常量树、stat 底值表与最终值表；由靶规格写入。</summary>
public sealed class RimRunnerDummyComp : ThingComp
{
    private float healthScale = 1f;
    private ThinkTreeDef? thinkTree;
    private ThinkTreeDef? constantThinkTree;
    private Dictionary<StatDef, float> statBases = [];
    private Dictionary<StatDef, float> statsFinalized = [];

    private List<StatDef>? statBaseKeys;
    private List<float>? statBaseValues;
    private List<StatDef>? statFinalizedKeys;
    private List<float>? statFinalizedValues;

    /// <summary>定义层缺省。</summary>
    public RimRunnerDummyCompProperties Props => (RimRunnerDummyCompProperties)props;

    /// <summary>实例血量系数：血量系数，乘进 Pawn.HealthScale。</summary>
    public float HealthScale => healthScale;

    /// <summary>实例主思维树：实例选的主思维树，替换 Pawn_Thinker.MainThinkTree。</summary>
    public ThinkTreeDef? ThinkTree => thinkTree;

    /// <summary>实例常量树：实例选的常量树，替换原版取的常量树；null 沿用种族常量树。</summary>
    public ThinkTreeDef? ConstantThinkTree => constantThinkTree;

    /// <summary>实例 stat 底值表：表中 stat 的底值取表中值，hediff、衣物等修正照常叠加。</summary>
    public IReadOnlyDictionary<StatDef, float> StatBases => statBases;

    /// <summary>实例 stat 最终值表：经 StatWorker.GetValue 的读数取表中值，盖过一切修正。</summary>
    public IReadOnlyDictionary<StatDef, float> StatsFinalized => statsFinalized;

    /// <summary>stat 底值查询：该 stat 是否写底值及其值。</summary>
    public bool TryGetStatBase(StatDef? stat, out float value) => TryGet(statBases, stat, out value);

    /// <summary>stat 最终值查询：该 stat 是否写最终值及其值。</summary>
    public bool TryGetStatFinalized(StatDef? stat, out float value) => TryGet(statsFinalized, stat, out value);

    private static bool TryGet(Dictionary<StatDef, float> stats, StatDef? stat, out float value)
    {
        if (stat != null && stats.Count > 0)
        {
            return stats.TryGetValue(stat, out value);
        }

        value = 0f;
        return false;
    }

    /// <summary>实例值初始化：按定义层缺省初始化实例值。</summary>
    public override void Initialize(CompProperties props)
    {
        base.Initialize(props);
        healthScale = Props.healthScale;
        thinkTree = Props.thinkTree;
    }

    /// <summary>实例值存档：实例值随 pawn 存读档。</summary>
    public override void PostExposeData()
    {
        base.PostExposeData();
        Scribe_Values.Look(ref healthScale, "healthScale", Props.healthScale);
        Scribe_Defs.Look(ref thinkTree, "thinkTree");
        Scribe_Defs.Look(ref constantThinkTree, "constantThinkTree");
        Scribe_Collections.Look(ref statBases, "statBases", LookMode.Def, LookMode.Value,
            ref statBaseKeys, ref statBaseValues);
        Scribe_Collections.Look(ref statsFinalized, "statsFinalized", LookMode.Def, LookMode.Value,
            ref statFinalizedKeys, ref statFinalizedValues);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            thinkTree ??= Props.thinkTree;
            statBases ??= [];
            statsFinalized ??= [];
        }
    }

    internal void Configure(float healthScale, ThinkTreeDef? thinkTree, ThinkTreeDef? constantThinkTree,
        IReadOnlyDictionary<StatDef, float> statBases, IReadOnlyDictionary<StatDef, float> statsFinalized)
    {
        List<StatDef> touched = this.statBases.Keys.Concat(this.statsFinalized.Keys).ToList();
        this.healthScale = healthScale;
        this.thinkTree = thinkTree;
        this.constantThinkTree = constantThinkTree;
        this.statBases = statBases.ToDictionary(e => e.Key, e => e.Value);
        this.statsFinalized = statsFinalized.ToDictionary(e => e.Key, e => e.Value);
        touched.AddRange(statBases.Keys);
        touched.AddRange(statsFinalized.Keys);

        foreach (StatDef stat in touched)
        {
            stat.Worker.ClearCacheForThing(parent);
        }
    }
}

/// <summary>假人判定：假人种族集与取 comp 的入口，判一个种族或 pawn 是否为假人。</summary>
/// <remarks>假人种族即带 RimRunnerDummyComp 的种族，含产品派生的；启动期从 DefDatabase 收齐一次。</remarks>
public static class RimRunnerDummies
{
    /// <summary>引擎缺省假人（单部位·全 tag）的 defName；种族与 kind 同名。</summary>
    public const string DefaultKindName = "RimRunnerDummy";

    /// <summary>单部位·无 tag 假人的 defName；种族与 kind 同名。</summary>
    public const string BareKindName = "RimRunnerDummyBare";

    /// <summary>原版 Human body 假人的 defName；种族与 kind 同名。</summary>
    public const string HumanBodyKindName = "RimRunnerDummyHumanBody";

    /// <summary>会流血的单部位·全 tag 假人的 defName；种族与 kind 同名。</summary>
    public const string BleedingKindName = "RimRunnerDummyBleeding";

    /// <summary>强制待机树的 defName。</summary>
    public const string HoldTreeName = "RimRunnerDummy_Hold";

    /// <summary>待机树的 defName；实例缺省。</summary>
    public const string WaitTreeName = "RimRunnerDummy_Wait";

    /// <summary>接敌树的 defName。</summary>
    public const string CombatTreeName = "RimRunnerDummy_Combat";

    /// <summary>强制待机 job 的 defName。</summary>
    public const string HoldJobName = "RimRunnerDummyHold";

    private static HashSet<ThingDef> races = new(ReferenceEqualityComparer.Instance);

    /// <summary>单部位全 tag 假人：全部 capacity 随那一个部位的血量比例走；规格缺省。</summary>
    /// <remarks>意识随伤与疼痛降，伤重后跌破可清醒线即倒地。</remarks>
    public static PawnKindDef DefaultKind => DefDatabase<PawnKindDef>.GetNamed(DefaultKindName);

    /// <summary>单部位·无 tag 假人：部位血量不扣 capacity，不走动、不配 AwakeImmobile。</summary>
    /// <remarks>GetBrain() 返回唯一部位；疼痛照扣意识，要意识恒定就挂无痛伤口。操作能力恒 0，近战命中率随之压低。</remarks>
    public static PawnKindDef BareKind => DefDatabase<PawnKindDef>.GetNamed(BareKindName);

    /// <summary>原版 Human body 假人：身体用原版 Human 的部位结构。</summary>
    /// <remarks>Odyssey 的真空图上 vanilla 会发真空服；引擎在假人生成后、产品落图钩子前剥掉生成期着装。</remarks>
    public static PawnKindDef HumanBodyKind => DefDatabase<PawnKindDef>.GetNamed(HumanBodyKindName);

    /// <summary>流血假人：会流血的单部位且全 tag 假人，缺省假人加上 vanilla 的出血、失血与血迹。</summary>
    /// <remarks>失血照原版累积，到致死线即死。血迹与失血来自种族的抽象父定义 RimRunnerDummyBleedingBase，部位出血系数在 body 上。</remarks>
    public static PawnKindDef BleedingKind => DefDatabase<PawnKindDef>.GetNamed(BleedingKindName);

    /// <summary>强制待机树：发引擎的站立 job，无自动近战，不走动。</summary>
    public static ThinkTreeDef HoldTree => DefDatabase<ThinkTreeDef>.GetNamed(HoldTreeName);

    /// <summary>待机树：发原版 Wait，永不到期，邻格有敌对就近战，不走动；规格缺省。</summary>
    /// <remarks>倒地、眩晕、昏迷时原版 Wait 不出手，故配任何行动档。不在图上（被抬起、被吞噬、复活落图前）时发强制待机，落图后换回 Wait。</remarks>
    public static ThinkTreeDef WaitTree => DefDatabase<ThinkTreeDef>.GetNamed(WaitTreeName);

    /// <summary>接敌树：找敌人走过去打，没有敌人时 Wait 60 拍后重找；会走动。</summary>
    public static ThinkTreeDef CombatTree => DefDatabase<ThinkTreeDef>.GetNamed(CombatTreeName);

    internal static bool RaceSetEmpty => races.Count == 0;

    /// <summary>假人种族查询：该种族是否带假人 comp。</summary>
    public static bool IsDummyRace(ThingDef? def) => def != null && races.Contains(def);

    /// <summary>假人 comp 查询：该 pawn 是否假人，是则给出该 pawn 的 comp。</summary>
    public static bool TryGet(Pawn pawn, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RimRunnerDummyComp comp) => TryGet((Thing)pawn, out comp);

    internal static bool TryGet(Thing? thing, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RimRunnerDummyComp comp)
    {
        if (thing == null || races.Count == 0 || !races.Contains(thing.def))
        {
            comp = null;
            return false;
        }

        comp = ((ThingWithComps)thing).GetComp<RimRunnerDummyComp>();
        return comp != null;
    }

    internal static void BuildRaceSet()
    {
        HashSet<ThingDef> found = new(ReferenceEqualityComparer.Instance);
        foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
        {
            if (def.race != null && def.HasComp(typeof(RimRunnerDummyComp)))
            {
                found.Add(def);
            }
        }

        races = found;
    }
}

/// <summary>假人 Priority 根：ThinkNode_Priority 的子类，成员沿用基类。</summary>
public sealed class RimRunnerDummyThinkNode_Priority : ThinkNode_Priority
{
}

/// <summary>强制待机节点：恒发 RimRunnerDummyHold。</summary>
public sealed class RimRunnerDummyJobGiver_Hold : ThinkNode_JobGiver
{
    protected override Job TryGiveJob(Pawn pawn)
    {
        return JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed(RimRunnerDummies.HoldJobName));
    }
}

/// <summary>假人 Wait 节点：发原版 Wait 的节点；expiryTicks ≤ 0 即永不到期。</summary>
public sealed class RimRunnerDummyJobGiver_Wait : ThinkNode_JobGiver
{
    /// <summary>Wait 的到期拍数，≤ 0 永不到期；XML 写。</summary>
    public int expiryTicks = -1;

    /// <summary>深拷时带上 expiryTicks。</summary>
    public override ThinkNode DeepCopy(bool resolve = true)
    {
        RimRunnerDummyJobGiver_Wait copy = (RimRunnerDummyJobGiver_Wait)base.DeepCopy(resolve);
        copy.expiryTicks = expiryTicks;
        return copy;
    }

    private const int OffMapRecheckTicks = 15;

    protected override Job TryGiveJob(Pawn pawn)
    {
        if (!pawn.Spawned)
        {
            Job hold = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed(RimRunnerDummies.HoldJobName));
            hold.expiryInterval = OffMapRecheckTicks;
            hold.checkOverrideOnExpire = true;
            return hold;
        }

        Job job = JobMaker.MakeJob(JobDefOf.Wait);
        if (expiryTicks > 0) job.expiryInterval = expiryTicks;
        return job;
    }
}

/// <summary>接敌节点：JobGiver_AIFightEnemies 的子类，成员沿用基类。</summary>
public sealed class RimRunnerDummyJobGiver_FightEnemies : JobGiver_AIFightEnemies
{
}

/// <summary>假人树声明：这棵树会不会让假人走动、会不会出手、会不会主动出手。</summary>
/// <remarks>假人规格据此判树与假人是否相配。会走动配不走动的种族、会走动或主动出手配非 Mobile 行动档、可能出手配无近战手段的 kind、不走动配先站定，这四组都不合。不走动配先站定拒规格，其余三组照常生成、记警告。</remarks>
public sealed class RimRunnerDummyThinkTreeExtension : DefModExtension
{
    private static readonly RimRunnerDummyThinkTreeExtension Undeclared = new();

    /// <summary>会走动：会让假人离开原地。</summary>
    public bool moves = true;

    /// <summary>会出手：可能让假人出手，含邻格还手；据此核 kind 的近战手段。</summary>
    public bool attacks = true;

    /// <summary>主动出手：不等敌对贴身就去打；身体失能时这类树的行为落空，只配 Mobile 行动档。</summary>
    public bool initiates = true;

    /// <summary>树声明读取：读树的声明，未声明按三项皆真。</summary>
    public static RimRunnerDummyThinkTreeExtension Of(ThinkTreeDef tree)
        => tree.GetModExtension<RimRunnerDummyThinkTreeExtension>() ?? Undeclared;

    internal static bool IsDeclared(ThinkTreeDef tree)
        => tree != null && tree.GetModExtension<RimRunnerDummyThinkTreeExtension>() != null;
}

/// <summary>强制待机 driver：永不结束，不出手。</summary>
public sealed class RimRunnerDummyJobDriver_Hold : JobDriver
{
    /// <summary>预留：本 job 不占预留，直接返回可开始。</summary>
    public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

    /// <summary>倒地可起：倒地档下也能起。</summary>
    public override bool CanBeginNowWhileLyingDown() => true;

    protected override IEnumerable<Toil> MakeNewToils()
    {
        Toil hold = ToilMaker.MakeToil("RimRunnerDummyHold");
        hold.initAction = () => pawn.pather?.StopDead();
        hold.defaultCompleteMode = ToilCompleteMode.Never;
        yield return hold;
    }
}
