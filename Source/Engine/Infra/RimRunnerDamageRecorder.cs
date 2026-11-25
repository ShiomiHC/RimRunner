using System.Globalization;

namespace RimRunner;

/// <summary>受击记录器：监控一组受击者，按名义、实伤、折甲后、落伤分账，另记逐笔与近战命中。</summary>
/// <remarks>名义账记护甲折减前的量，实伤账记结算后实际落下的量，均为窗内合计，逐笔读 HitsOn；伤害账只收经 Thing.TakeDamage 的命中，近战命中账不论伤害走哪条路。本窗没开过监控、或按受击者读没监控的 pawn 即抛。</remarks>
public static class RimRunnerDamageRecorder
{
    private const int Cap = 200;

    private static Pawn? watched;
    private static readonly HashSet<Pawn> watchedSet = [];
    private static readonly HashSet<Pawn> monitoredThisWindow = [];
    internal static bool HasWindow { get; private set; }
    private static readonly HashSet<int> forgottenThisWindow = [];
    private static string phase = "?";
    private static readonly List<string> records = [];
    private static readonly Dictionary<Pawn, int> propagatedHits = [];
    private static readonly Dictionary<Pawn, float> nominalTaken = [];
    private static readonly Dictionary<Pawn, float> settledTaken = [];
    private static readonly Dictionary<Thing, Dictionary<Pawn, float>> nominalDealt = [];
    private static readonly Dictionary<Thing, Dictionary<Pawn, Dictionary<string, float>>> nominalDealtBySource = [];
    private static readonly Dictionary<Thing, Dictionary<Pawn, Dictionary<string, float>>> armoredDealtBySource = [];
    private static readonly Dictionary<Thing, Dictionary<Pawn, Dictionary<string, float>>> landedDealtBySource = [];
    private static readonly Dictionary<Pawn, Dictionary<Pawn, List<int>>> meleeHits = [];
    private static readonly List<HitEntry> hits = [];
    private static readonly List<HitEntry> pendingHits = [];
    private static int armoredRolls;

    private sealed class HitEntry(int tick, Pawn victim, Thing? instigator, string source, DamageDef def, float nominal)
    {
        public readonly int Tick = tick;
        public readonly Pawn Victim = victim;
        public readonly Thing? Instigator = instigator;
        public readonly string Source = source;
        public readonly DamageDef Def = def;
        public readonly float Nominal = nominal;
        public float Settled;
        public bool Absorbed;

        public RimRunnerDamageHit ToHit() => new(Tick, Victim, Instigator, Source, Def, Nominal, Settled, Absorbed);
    }

    private static readonly List<UnwatchedHit> nominalUnwatched = [];
    private static readonly List<UnwatchedHit> settledUnwatched = [];
    private static readonly List<UnwatchedHit> meleeUnwatched = [];

    private sealed record UnwatchedHit(Pawn Victim, Thing Instigator);

    /// <summary>折甲后分账开关，Watch 与用例末复位为关。</summary>
    /// <remarks>开时施加者分账里的每笔命中另按 vanilla 护甲规则折算一次，被击中的衣物因此多磨损一次。折算的随机滚点走独立随机流，全局随机流保持原位。</remarks>
    public static bool ArmoredLedger;

    /// <summary>监控一组受击者并清空全部账目。</summary>
    /// <remarks>同时把 ArmoredLedger 复位为关、FirstHitTick 复位为 -1，只监控一个时另写字符串记录。受击者都算进本窗监控集。传空集即结束本窗，之后读账即抛。</remarks>
    public static void Watch(IReadOnlyCollection<Pawn> victims)
    {
        if (victims == null)
        {
            throw new ArgumentNullException(nameof(victims));
        }

        watchedSet.Clear();
        monitoredThisWindow.Clear();
        forgottenThisWindow.Clear();
        nominalUnwatched.Clear();
        settledUnwatched.Clear();
        meleeUnwatched.Clear();
        foreach (Pawn victim in victims)
        {
            if (victim != null)
            {
                watchedSet.Add(victim);
                monitoredThisWindow.Add(victim);
            }
        }

        HasWindow = monitoredThisWindow.Count > 0;
        watched = watchedSet.Count == 1 ? watchedSet.First() : null;
        phase = "?";
        records.Clear();
        propagatedHits.Clear();
        nominalTaken.Clear();
        settledTaken.Clear();
        nominalDealt.Clear();
        nominalDealtBySource.Clear();
        armoredDealtBySource.Clear();
        landedDealtBySource.Clear();
        meleeHits.Clear();
        hits.Clear();
        pendingHits.Clear();
        ArmoredLedger = false;
        FirstHitTick = -1;
    }

    private static void ForgetSourcesOf(IReadOnlyCollection<Pawn> pawns)
    {
        List<Thing>? sources = null;
        foreach (Thing key in nominalDealt.Keys.Concat(nominalDealtBySource.Keys).Concat(armoredDealtBySource.Keys).Concat(landedDealtBySource.Keys))
        {
            bool ownedByForgotten = key is not Pawn && SourcePawn(key) is { } owner && pawns.Contains(owner);
            if (ownedByForgotten || IsEmptyRow(key))
            {
                (sources ??= []).Add(key);
            }
        }

        if (sources == null)
        {
            return;
        }

        foreach (Thing source in sources)
        {
            nominalDealt.Remove(source);
            nominalDealtBySource.Remove(source);
            armoredDealtBySource.Remove(source);
            landedDealtBySource.Remove(source);
        }
    }

    private static bool IsEmptyRow(Thing key)
    {
        return (!nominalDealt.TryGetValue(key, out Dictionary<Pawn, float>? nominal) || nominal.Count == 0)
               && (!nominalDealtBySource.TryGetValue(key, out Dictionary<Pawn, Dictionary<string, float>>? bySource) || bySource.Count == 0)
               && (!armoredDealtBySource.TryGetValue(key, out Dictionary<Pawn, Dictionary<string, float>>? armored) || armored.Count == 0)
               && (!landedDealtBySource.TryGetValue(key, out Dictionary<Pawn, Dictionary<string, float>>? landed) || landed.Count == 0);
    }

    private static Pawn? SourcePawn(Thing source)
    {
        return source switch
        {
            Fire fire => fire.instigator as Pawn,
            Projectile projectile => projectile.Launcher as Pawn,
            Explosion explosion => explosion.instigator as Pawn,
            _ => null,
        };
    }

    private static bool InvolvesAny(HitEntry hit, IReadOnlyCollection<Pawn> pawns) =>
        pawns.Contains(hit.Victim)
        || hit.Instigator is Pawn instigator && pawns.Contains(instigator)
        || hit.Instigator != null && SourcePawn(hit.Instigator) is { } owner && pawns.Contains(owner);

    /// <summary>监控单一受击者（Watch 的单人形态）。</summary>
    public static void Start(Pawn pawn) => Watch([pawn]);

    /// <summary>往监控集追加受击者，既有账目保留。</summary>
    /// <remarks>并入的受击者从此算进本窗监控集，此前记下的漏账随即摘掉。本窗未开时由此开窗。</remarks>
    public static void WatchAlso(IReadOnlyCollection<Pawn> victims)
    {
        if (victims == null)
        {
            throw new ArgumentNullException(nameof(victims));
        }

        foreach (Pawn victim in victims)
        {
            if (victim != null)
            {
                watchedSet.Add(victim);
                monitoredThisWindow.Add(victim);
                forgottenThisWindow.Remove(victim.thingIDNumber);
                ForgetUnwatched(victim);
            }
        }

        if (monitoredThisWindow.Count > 0)
        {
            HasWindow = true;
        }

        watched = watchedSet.Count == 1 ? watchedSet.First() : null;
    }

    private static void ForgetUnwatched(Pawn victim)
    {
        nominalUnwatched.RemoveAll(hit => hit.Victim == victim);
        settledUnwatched.RemoveAll(hit => hit.Victim == victim);
        meleeUnwatched.RemoveAll(hit => hit.Victim == victim);
    }

    /// <summary>从监控集移除一组 pawn，删掉它们作为受击者与施加者的全部账目。</summary>
    /// <remarks>它们发起的火、弹丸与爆炸作施加者的账一并删，其余账目保持原值。移除的受击者此后按名读即抛。移除的受击者此后的受击不进漏账。受击账目默认留到段末；长用例只释放一部分时用本方法删这几个，或 Watch([]) 整本清。</remarks>
    public static void Forget(IReadOnlyCollection<Pawn> pawns)
    {
        if (pawns == null)
        {
            throw new ArgumentNullException(nameof(pawns));
        }

        foreach (Pawn pawn in pawns)
        {
            if (pawn == null)
            {
                continue;
            }

            watchedSet.Remove(pawn);
            monitoredThisWindow.Remove(pawn);
            forgottenThisWindow.Add(pawn.thingIDNumber);
            ForgetUnwatched(pawn);
            propagatedHits.Remove(pawn);
            nominalTaken.Remove(pawn);
            settledTaken.Remove(pawn);
            nominalDealt.Remove(pawn);
            foreach (Dictionary<Pawn, float> byVictim in nominalDealt.Values)
            {
                byVictim.Remove(pawn);
            }

            nominalDealtBySource.Remove(pawn);
            foreach (Dictionary<Pawn, Dictionary<string, float>> byVictim in nominalDealtBySource.Values)
            {
                byVictim.Remove(pawn);
            }

            armoredDealtBySource.Remove(pawn);
            foreach (Dictionary<Pawn, Dictionary<string, float>> byVictim in armoredDealtBySource.Values)
            {
                byVictim.Remove(pawn);
            }

            landedDealtBySource.Remove(pawn);
            foreach (Dictionary<Pawn, Dictionary<string, float>> byVictim in landedDealtBySource.Values)
            {
                byVictim.Remove(pawn);
            }

            meleeHits.Remove(pawn);
            foreach (Dictionary<Pawn, List<int>> byVictim in meleeHits.Values)
            {
                byVictim.Remove(pawn);
            }
        }

        ForgetSourcesOf(pawns);
        hits.RemoveAll(hit => InvolvesAny(hit, pawns));
        pendingHits.RemoveAll(hit => InvolvesAny(hit, pawns));
        foreach (Pawn emptied in meleeHits.Where(row => row.Value.Count == 0).Select(row => row.Key).ToList())
        {
            meleeHits.Remove(emptied);
        }

        watched = watchedSet.Count == 1 ? watchedSet.First() : null;
    }

    internal static void ResetPropagationTally()
    {
        propagatedHits.Clear();
    }

    /// <summary>各受击者允许伤害传播的命中数。</summary>
    /// <remarks>计 dinfo.AllowDamagePropagation 为真的命中，每条用例开始执行前与 Watch 时清零。本窗未开过监控即抛。</remarks>
    public static IEnumerable<KeyValuePair<Pawn, int>> PropagatedHits
    {
        get
        {
            RequireWindow(nameof(PropagatedHits));
            return propagatedHits;
        }
    }

    /// <summary>阶段标记：此后的字符串记录都带上这个标签。</summary>
    public static void Mark(string phaseLabel) => phase = phaseLabel;

    /// <summary>停止监控：清空监控集，账目保留、仍可读。</summary>
    /// <remarks>本窗监控过的受击者与账目一并保留，Stop 之后按名读照常合法；此后不再收账、也不记漏账。账目留到用例末（多阶段为 phase 末）连同 ArmoredLedger 清空；那时仍在监控的由引擎停止并记 FIXTURE-RESIDUE。</remarks>
    public static void Stop()
    {
        watched = null;
        watchedSet.Clear();
    }

    internal static bool IsWatching => watchedSet.Count > 0;

    private static void RequireWindow(string reader)
    {
        RimRunnerFault.Require(HasWindow,
            "读伤害账前本窗没开过监控", reader);
    }

    private static void RequireMonitored(Pawn? victim, string reader)
    {
        if (victim == null)
        {
            throw new ArgumentNullException(nameof(victim), RimRunnerLog.Prefix + " " + reader + " 的受击者为 null");
        }

        RequireWindow(reader);
        RimRunnerFault.Require(monitoredThisWindow.Contains(victim),
            "该受击者不在本窗监控集；Forget 之后本窗不再有它的账",
            reader + "(" + victim.LabelShort + ")");
    }

    private static void RequireNoUnwatched(
        List<UnwatchedHit> hits, Func<Thing, bool> instigator, Func<Pawn, bool> victim, string reader)
    {
        foreach (UnwatchedHit hit in hits)
        {
            if (instigator(hit.Instigator) && victim(hit.Victim))
            {
                RimRunnerFault.Require(false,
                    "有受击者在本窗未 Watch，却受到攻击，这一笔不在账上",
                    reader + " ← " + hit.Victim.LabelShort + " 被 " + hit.Instigator.LabelShort + " 打过");
            }
        }
    }

    private static void RequireNoUnwatchedMelee(
        List<UnwatchedHit> hits, Func<Pawn, bool> attacker, Func<Pawn, bool> victim, string reader) =>
        RequireNoUnwatched(hits, thing => thing is Pawn pawn && attacker(pawn), victim, reader);

    internal static int ResetAfterSegment(bool watchingBefore)
    {
        if (watchingBefore)
        {
            return 0;
        }

        int leaked = watchedSet.Count;
        Watch([]);
        return leaked;
    }

    /// <summary>监控窗内的字符串记录条数（单一受击者时计）。</summary>
    /// <remarks>至多 200 条，满后停止记录。监控集多于一个时停止记录，此前已记的仍可读取。本窗未开过监控即抛。</remarks>
    public static int RecordCount
    {
        get
        {
            RequireWindow(nameof(RecordCount));
            return records.Count;
        }
    }

    private static int firstHitTick = -1;

    /// <summary>监控窗内首笔正伤害的 TicksGame；尚无 = -1。</summary>
    /// <remarks>本窗没开过监控即抛。</remarks>
    public static int FirstHitTick
    {
        get
        {
            RequireWindow(nameof(FirstHitTick));
            return firstHitTick;
        }

        private set => firstHitTick = value;
    }

    /// <summary>该受击者的名义承伤合计。</summary>
    /// <remarks>护甲折减前的 dinfo.Amount，按受击者合计，逐笔读 HitsOn，只对 Watch 过的 pawn 入账。本窗没开过监控即抛，该受击者不在本窗监控集也抛。Stop 之后照读。</remarks>
    public static float NominalTakenBy(Pawn victim)
    {
        RequireMonitored(victim, nameof(NominalTakenBy));
        return nominalTaken.TryGetValue(victim, out float taken) ? taken : 0f;
    }

    /// <summary>该受击者的实伤合计：每笔伤害结算完毕后实际落下的量。</summary>
    /// <remarks>护甲折减、伤型免疫与拦截 patch 生效之后落到 hediff 上的量，按受击者合计，逐笔读 HitsOn，只对 Watch 过的 pawn 入账。本窗没开过监控即抛，该受击者不在本窗监控集也抛。Stop 之后照读。</remarks>
    public static float SettledTakenBy(Pawn victim)
    {
        RequireMonitored(victim, nameof(SettledTakenBy));
        return settledTaken.TryGetValue(victim, out float taken) ? taken : 0f;
    }

    /// <summary>该受击者未关内传播的命中数。</summary>
    /// <remarks>本窗没开过监控即抛，该受击者不在本窗监控集也抛。Stop 之后照读。</remarks>
    public static int PropagatedHitsOf(Pawn victim)
    {
        RequireMonitored(victim, nameof(PropagatedHitsOf));
        return propagatedHits.TryGetValue(victim, out int hits) ? hits : 0;
    }

    /// <summary>按施加者 × 受击者过滤的名义伤害合计。</summary>
    /// <remarks>经 Thing.TakeDamage 的命中入账，直接 AddHediff 的伤口不入账；无施加者的伤害不入本账。施加者取 dinfo.Instigator，火无放火者时为那团火。本窗没开过监控、或同一笔漏账的施加者与受击者谓词都命中即抛。</remarks>
    public static float NominalDealtBy(Func<Thing, bool> instigator, Func<Pawn, bool> victim)
    {
        if (instigator == null)
        {
            throw new ArgumentNullException(nameof(instigator));
        }

        if (victim == null)
        {
            throw new ArgumentNullException(nameof(victim));
        }

        RequireWindow(nameof(NominalDealtBy));
        RequireNoUnwatched(nominalUnwatched, instigator, victim, nameof(NominalDealtBy));

        float sum = 0f;
        foreach (KeyValuePair<Thing, Dictionary<Pawn, float>> byInstigator in nominalDealt)
        {
            if (!instigator(byInstigator.Key))
            {
                continue;
            }

            foreach (KeyValuePair<Pawn, float> byVictim in byInstigator.Value)
            {
                if (victim(byVictim.Key))
                {
                    sum += byVictim.Value;
                }
            }
        }

        return sum;
    }

    /// <summary>按施加者 × 受击者过滤的名义伤害，再按来源分账。</summary>
    /// <remarks>来源 = dinfo.Weapon 的 defName（枪械弹丸 = 武器 def，身体工具近战 = 施加者种族 def），无 Weapon 记在 &lt;伤型 defName&gt; 下。本窗没开过监控即抛，同一笔漏账的施加者与受击者谓词都命中也抛。</remarks>
    /// <returns>来源到名义伤害合计，无匹配为空表。</returns>
    public static Dictionary<string, float> NominalDealtBySource(Func<Thing, bool> instigator, Func<Pawn, bool> victim) =>
        DealtBySource(nominalDealtBySource, nominalUnwatched, instigator, victim, nameof(NominalDealtBySource));

    /// <summary>按施加者 × 受击者过滤的折甲后伤害，按来源分账。</summary>
    /// <remarks>只记 ArmoredLedger 开启期间的命中，来源键同 NominalDealtBySource。本窗没开过监控即抛。同一笔漏账的施加者与受击者谓词都命中即抛（查名义账的漏账）。</remarks>
    /// <exception cref="InvalidOperationException">ArmoredLedger 为假时抛。</exception>
    public static Dictionary<string, float> ArmoredDealtBySource(Func<Thing, bool> instigator, Func<Pawn, bool> victim)
    {
        if (!ArmoredLedger)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " ArmoredDealtBySource：ArmoredLedger 为关，折甲后分账为空");
        }

        return DealtBySource(armoredDealtBySource, nominalUnwatched, instigator, victim, nameof(ArmoredDealtBySource));
    }

    /// <summary>按施加者 × 受击者过滤的落伤量，按来源分账。</summary>
    /// <remarks>每个伤口记 min(伤口严重度, 部位血量)，取值在伤型缩放与护甲之后、按部位截断。内伤复制到外层部位的各记一笔。只记落成伤口的伤型。本窗没开过监控即抛。同一笔漏账的施加者与受击者谓词都命中即抛。</remarks>
    public static Dictionary<string, float> LandedDealtBySource(Func<Thing, bool> instigator, Func<Pawn, bool> victim) =>
        DealtBySource(landedDealtBySource, settledUnwatched, instigator, victim, nameof(LandedDealtBySource));

    /// <summary>该受击者在本窗挨的每一次：名义量与实伤，按发生先后。</summary>
    /// <remarks>经 Thing.TakeDamage、名义量为正的每一笔，被吸收的 Absorbed 为真、实伤 0，逐笔之和等于两本合计账。本窗没开过监控即抛，受击者不在监控集即抛，有一次没结算完也抛。Stop 后照读。</remarks>
    /// <returns>按发生先后的逐笔记录，没有命中时为空表。</returns>
    public static List<RimRunnerDamageHit> HitsOn(Pawn victim)
    {
        RequireMonitored(victim, nameof(HitsOn));
        return SettledHits(hit => hit.Victim == victim, nameof(HitsOn));
    }

    /// <summary>按施加者 × 受击者过滤的每一次，按发生先后。</summary>
    /// <remarks>笔的取法同 HitsOn，无施加者的那几次不在其中。本窗没开过监控即抛，同一笔漏账的施加者与受击者谓词都命中即抛，匹配的笔里有一次还没结算完也抛。</remarks>
    /// <returns>按发生先后的逐笔记录，无匹配为空表。</returns>
    public static List<RimRunnerDamageHit> Hits(Func<Thing, bool> instigator, Func<Pawn, bool> victim)
    {
        if (instigator == null)
        {
            throw new ArgumentNullException(nameof(instigator));
        }

        if (victim == null)
        {
            throw new ArgumentNullException(nameof(victim));
        }

        RequireWindow(nameof(Hits));
        RequireNoUnwatched(nominalUnwatched, instigator, victim, nameof(Hits));
        return SettledHits(hit => hit.Instigator != null && instigator(hit.Instigator) && victim(hit.Victim), nameof(Hits));
    }

    private static List<RimRunnerDamageHit> SettledHits(Func<HitEntry, bool> match, string reader)
    {
        List<RimRunnerDamageHit> result = [];
        foreach (HitEntry hit in hits)
        {
            if (!match(hit))
            {
                continue;
            }

            RimRunnerFault.Require(!pendingHits.Contains(hit),
                "这次命中尚未结算完",
                reader + " ← " + hit.Victim.LabelShort + " t" + hit.Tick + " " + hit.Def.defName);
            result.Add(hit.ToHit());
        }

        return result;
    }

    /// <summary>按攻方 × 受击者过滤的近战命中 tick。</summary>
    /// <remarks>Verb_MeleeAttack 一族的近战出手，沿用其 TryCastShot 判定，未落空、未被闪避即记，伤害经不经 Thing.TakeDamage、落没落伤口都记。本窗没开过监控即抛，同一笔漏账的施加者与受击者谓词都命中也抛。</remarks>
    /// <returns>每次命中时的 TicksGame，升序，无匹配为空表。</returns>
    public static List<int> MeleeHitTicks(Func<Pawn, bool> attacker, Func<Pawn, bool> victim)
    {
        if (attacker == null)
        {
            throw new ArgumentNullException(nameof(attacker));
        }

        if (victim == null)
        {
            throw new ArgumentNullException(nameof(victim));
        }

        RequireWindow(nameof(MeleeHitTicks));
        RequireNoUnwatchedMelee(meleeUnwatched, attacker, victim, nameof(MeleeHitTicks));

        List<int> ticks = [];
        foreach (KeyValuePair<Pawn, Dictionary<Pawn, List<int>>> byAttacker in meleeHits)
        {
            if (!attacker(byAttacker.Key))
            {
                continue;
            }

            foreach (KeyValuePair<Pawn, List<int>> byVictim in byAttacker.Value)
            {
                if (victim(byVictim.Key))
                {
                    ticks.AddRange(byVictim.Value);
                }
            }
        }

        ticks.Sort();
        return ticks;
    }

    private static Dictionary<string, float> DealtBySource(
        Dictionary<Thing, Dictionary<Pawn, Dictionary<string, float>>> ledger, List<UnwatchedHit> unwatched,
        Func<Thing, bool> instigator, Func<Pawn, bool> victim, string reader)
    {
        if (instigator == null)
        {
            throw new ArgumentNullException(nameof(instigator));
        }

        if (victim == null)
        {
            throw new ArgumentNullException(nameof(victim));
        }

        RequireWindow(reader);
        RequireNoUnwatched(unwatched, instigator, victim, reader);

        Dictionary<string, float> sum = [];
        foreach (KeyValuePair<Thing, Dictionary<Pawn, Dictionary<string, float>>> byInstigator in ledger)
        {
            if (!instigator(byInstigator.Key))
            {
                continue;
            }

            foreach (KeyValuePair<Pawn, Dictionary<string, float>> byVictim in byInstigator.Value)
            {
                if (!victim(byVictim.Key))
                {
                    continue;
                }

                foreach (KeyValuePair<string, float> bySource in byVictim.Value)
                {
                    sum.TryGetValue(bySource.Key, out float dealt);
                    sum[bySource.Key] = dealt + bySource.Value;
                }
            }
        }

        return sum;
    }

    /// <summary>按施加者 × 受击者逐对列出名义伤害的文本。</summary>
    /// <remarks>诊断描述不抛，同一笔漏账的施加者与受击者谓词都命中时附在末尾。</remarks>
    /// <returns>每对一段，带双方的标签、def、所在格与存活状态，无匹配为空串。</returns>
    public static string DescribeDealtBy(Func<Thing, bool> instigator, Func<Pawn, bool> victim)
    {
        if (instigator == null)
        {
            throw new ArgumentNullException(nameof(instigator));
        }

        if (victim == null)
        {
            throw new ArgumentNullException(nameof(victim));
        }

        List<string> parts = [];
        foreach (KeyValuePair<Thing, Dictionary<Pawn, float>> byInstigator in nominalDealt)
        {
            if (!instigator(byInstigator.Key))
            {
                continue;
            }

            foreach (KeyValuePair<Pawn, float> byVictim in byInstigator.Value)
            {
                if (victim(byVictim.Key))
                {
                    parts.Add(DescribeThing(byInstigator.Key) + " → " + DescribeThing(byVictim.Key)
                        + " = " + byVictim.Value.ToString("0.#", CultureInfo.InvariantCulture));
                }
            }
        }

        foreach (UnwatchedHit hit in nominalUnwatched)
        {
            if (instigator(hit.Instigator) && victim(hit.Victim))
            {
                parts.Add(DescribeThing(hit.Instigator) + " → " + DescribeThing(hit.Victim) + "（本窗没 Watch，未入账）");
            }
        }

        return string.Join("；", parts);
    }

    private static void AddBySource(
        Dictionary<Thing, Dictionary<Pawn, Dictionary<string, float>>> ledger, Thing instigator, Pawn victim, string source, float amount)
    {
        if (!ledger.TryGetValue(instigator, out Dictionary<Pawn, Dictionary<string, float>>? bySourceOfVictim))
        {
            bySourceOfVictim = [];
            ledger[instigator] = bySourceOfVictim;
        }

        if (!bySourceOfVictim.TryGetValue(victim, out Dictionary<string, float>? bySource))
        {
            bySource = [];
            bySourceOfVictim[victim] = bySource;
        }

        bySource.TryGetValue(source, out float dealtBySource);
        bySource[source] = dealtBySource + amount;
    }

    private static float ArmoredAmount(Pawn victim, DamageInfo dinfo)
    {
        DamageDef def = dinfo.Def;
        if (def.armorCategory == null)
        {
            return dinfo.Amount;
        }

        Rand.PushState(Gen.HashCombineInt(Gen.HashCombineInt(Find.TickManager.TicksGame, victim.thingIDNumber), ++armoredRolls));
        try
        {
            BodyPartRecord? part = dinfo.HitPart ?? victim.health.hediffSet.GetRandomNotMissingPart(def, dinfo.Height, dinfo.Depth);
            return part == null
                ? dinfo.Amount
                : ArmorUtility.GetPostArmorDamage(victim, dinfo.Amount, dinfo.ArmorPenetrationInt, part, ref def, out _, out _);
        }
        finally
        {
            Rand.PopState();
        }
    }

    private static string DescribeThing(Thing thing) =>
        thing.LabelShort + "/" + thing.def.defName + "@" + (thing.Spawned ? thing.Position.ToString() : "离图")
        + (thing is Pawn pawn && pawn.Dead ? "(死)" : "");

    /// <summary>字符串记录全文。</summary>
    /// <remarks>本窗没开过监控即抛。</remarks>
    public static string Dump()
    {
        RequireWindow(nameof(Dump));
        return records.Count == 0 ? "无受击记录" : string.Join(" || ", records);
    }

    private static void RecordUnwatched(List<UnwatchedHit> hits, Pawn victim, Thing? instigator)
    {
        if (watchedSet.Count == 0 || instigator == null || forgottenThisWindow.Contains(victim.thingIDNumber))
        {
            return;
        }

        hits.Add(new UnwatchedHit(victim, instigator));
    }

    internal static void RecordAbsorbed(Pawn? victim, bool absorbed)
    {
        if (absorbed && victim != null && TakePending(victim) is { } hit)
        {
            hit.Absorbed = true;
        }
    }

    private static HitEntry? TakePending(Pawn victim)
    {
        for (int i = pendingHits.Count - 1; i >= 0; i--)
        {
            if (pendingHits[i].Victim == victim)
            {
                HitEntry hit = pendingHits[i];
                pendingHits.RemoveAt(i);
                return hit;
            }
        }

        return null;
    }

    internal static void RecordDealt(Pawn? victim, float totalDamageDealt)
    {
        if (victim != null && TakePending(victim) is { } hit)
        {
            hit.Settled = totalDamageDealt;
        }

        if (victim == null || !watchedSet.Contains(victim) || totalDamageDealt <= 0f)
        {
            return;
        }

        settledTaken.TryGetValue(victim, out float taken);
        settledTaken[victim] = taken + totalDamageDealt;
    }

    internal static void RecordLanded(Pawn? victim, DamageInfo dinfo, float landed)
    {
        Thing instigator = dinfo.Instigator;
        if (victim == null || instigator == null || landed <= 0f)
        {
            return;
        }

        if (!watchedSet.Contains(victim))
        {
            RecordUnwatched(settledUnwatched, victim, instigator);
            return;
        }

        AddBySource(landedDealtBySource, instigator, victim, SourceKey(dinfo), landed);
    }

    internal static void RecordMeleeHit(Pawn attacker, Pawn victim)
    {
        if (!watchedSet.Contains(victim))
        {
            RecordUnwatched(meleeUnwatched, victim, attacker);
            return;
        }

        if (!meleeHits.TryGetValue(attacker, out Dictionary<Pawn, List<int>>? byVictim))
        {
            byVictim = [];
            meleeHits[attacker] = byVictim;
        }

        if (!byVictim.TryGetValue(victim, out List<int>? ticks))
        {
            ticks = [];
            byVictim[victim] = ticks;
        }

        ticks.Add(Find.TickManager.TicksGame);
    }

    private static string SourceKey(DamageInfo dinfo) =>
        dinfo.Weapon != null ? dinfo.Weapon.defName : "<" + dinfo.Def.defName + ">";

    internal static void Record(Pawn? victim, DamageInfo dinfo)
    {
        if (victim == null)
        {
            return;
        }

        if (!watchedSet.Contains(victim))
        {
            if (dinfo.Amount > 0f)
            {
                RecordUnwatched(nominalUnwatched, victim, dinfo.Instigator);
            }

            return;
        }

        if (dinfo.AllowDamagePropagation)
        {
            propagatedHits.TryGetValue(victim, out int hits);
            propagatedHits[victim] = hits + 1;
        }

        float amount = dinfo.Amount;
        Thing instigator = dinfo.Instigator;
        if (amount > 0f)
        {
            if (firstHitTick < 0)
            {
                firstHitTick = Find.TickManager.TicksGame;
            }

            nominalTaken.TryGetValue(victim, out float taken);
            nominalTaken[victim] = taken + amount;
            HitEntry hit = new(Find.TickManager.TicksGame, victim, instigator, SourceKey(dinfo), dinfo.Def, amount);
            hits.Add(hit);
            pendingHits.Add(hit);
            if (instigator != null)
            {
                if (!nominalDealt.TryGetValue(instigator, out Dictionary<Pawn, float>? byVictim))
                {
                    byVictim = [];
                    nominalDealt[instigator] = byVictim;
                }

                byVictim.TryGetValue(victim, out float dealt);
                byVictim[victim] = dealt + amount;

                string source = SourceKey(dinfo);
                AddBySource(nominalDealtBySource, instigator, victim, source, amount);
                if (ArmoredLedger)
                {
                    AddBySource(armoredDealtBySource, instigator, victim, source, ArmoredAmount(victim, dinfo));
                }
            }
        }

        if (watched == null || victim != watched || records.Count >= Cap)
        {
            return;
        }

        string dist = instigator != null && instigator.Spawned && victim.Spawned
            ? victim.Position.DistanceTo(instigator.Position).ToString("0.#")
            : "n/a";
        records.Add("[" + phase + "]t" + Find.TickManager.TicksGame
                    + " " + dinfo.Def.defName + "=" + amount.ToString("0.#")
                    + " dist=" + dist
                    + " from=" + (instigator != null
                        ? instigator.LabelShort + "/" + instigator.def.defName
                          + (instigator.Faction != null ? "@" + instigator.Faction.Name : "")
                        : "null"));
    }
}

/// <summary>受击记录器逐笔账里的一次：何时、谁打谁、名义量与实伤。</summary>
/// <param name="Tick">这一次的 TicksGame。</param>
/// <param name="Victim">受击者。</param>
/// <param name="Instigator">dinfo.Instigator；无施加者为 null。</param>
/// <param name="Source">来源，取法同 NominalDealtBySource 的键。</param>
/// <param name="Def">伤型。</param>
/// <param name="Nominal">护甲折减前的 dinfo.Amount。</param>
/// <param name="Settled">这一次结算完毕实际落下的量（PostApplyDamage 的 totalDamageDealt）；被吸收时为 0。</param>
/// <param name="Absorbed">这一次在 PreApplyDamage 被整笔吸收，伤型 worker 未执行。</param>
public sealed record RimRunnerDamageHit(
    int Tick, Pawn Victim, Thing? Instigator, string Source, DamageDef Def, float Nominal, float Settled, bool Absorbed);
