using System.Diagnostics;
using System.Text;
using System.Threading;
using Verse.AI;

namespace RimRunner;

/// <summary>一条经 MoteMaker.ThrowText 的浮动文字观测，含位置、地图、文本。</summary>
public sealed class RimRunnerMoteObservation
{
    /// <summary>mote 落点。</summary>
    public Vector3 Location;
    /// <summary>所在地图。</summary>
    public required Map Map;
    /// <summary>mote 文本。</summary>
    public required string Text;
}

/// <summary>一条信件观测：def / 标题 / 正文。</summary>
/// <remarks>标题与正文是 TaggedString 的原文，带未解析的标签。期望值取同一 TaggedString 的 ToString() 比，只比可见文字时两侧都 StripTags()。</remarks>
public sealed class RimRunnerLetterObservation
{
    /// <summary>LetterDef 名。</summary>
    public string? DefName;
    /// <summary>信件标题原文。</summary>
    public required string Label;
    /// <summary>信件正文原文；非 ChoiceLetter 为 null。</summary>
    public string? Text;
}

/// <summary>一条消息观测：def / 文本。</summary>
public sealed class RimRunnerMessageObservation
{
    /// <summary>MessageTypeDef 名。</summary>
    public string? DefName;
    /// <summary>消息文本。</summary>
    public required string Text;
}

/// <summary>一条运行期观测（job、能力、弹丸），含阶段、主体、结果。</summary>
public sealed class RimRunnerRuntimeObservation
{
    /// <summary>事件阶段名。</summary>
    /// <remarks>job 为 Started、Cleaned、Suspended；能力为 CastingStarted、Activated；弹丸为 Launched、Impacted。Impacted 即进入碰撞处理，碰撞对象读 HitThing。</remarks>
    public required string Stage;
    /// <summary>主体的 ThingID，job 与能力为执行的 pawn，弹丸为弹丸本身。</summary>
    public required string Subject;
    /// <summary>def 名：job 的 JobDef、能力的 AbilityDef、弹丸的 ThingDef。</summary>
    public string? DefName;
    /// <summary>job 所带能力的 AbilityDef 名，不带能力的 job 与其余记录为 null。</summary>
    public string? RelatedDefName;
    /// <summary>结果：job 的结束条件或能力激活的成败。</summary>
    /// <remarks>Cleaned 与 Suspended 为 JobCondition 名，Activated 为 True 或 False，其余阶段为 null。</remarks>
    public string? Outcome;
    /// <summary>附加明细：供诊断原样输出的键值串，格式不属契约。</summary>
    public required string Detail;
    /// <summary>job 的 loadID；非 job 记录为 -1。</summary>
    /// <remarks>同一条 job 的 Started 与 Cleaned / Suspended 记录 JobId 相同。</remarks>
    public int JobId = -1;
    /// <summary>签发 job 的 think tree 节点类名。</summary>
    /// <remarks>非 job 记录为 null，不经 think tree 签发的 job 也为 null。</remarks>
    public string? JobGiver;
    /// <summary>job 的 targetA；非 job 记录为 null。</summary>
    /// <remarks>Thing 记 ThingID，格记坐标，无效记 invalid。</remarks>
    public string? TargetA;
    /// <summary>弹丸 Impacted 记录的碰撞对象 ThingID。</summary>
    /// <remarks>落在格上、因而没有碰撞对象的记录，与其余记录一样为 null。有碰撞对象只表示撞到它，不表示伤到它。</remarks>
    public string? HitThing;
    /// <summary>能力激活前后各项登记读数的差值，按读数名取。</summary>
    /// <remarks>差值为激活后读数减激活前读数，无法给出读数时为 NaN，读数由描述符的 AbilityActivationMeter 登记。非 Activated 记录为空表。</remarks>
    public IReadOnlyDictionary<string, float> ActivationDeltas = RimRunnerActivationMeters.NoDeltas;
    /// <summary>事件发生的 TicksGame。</summary>
    public int GameTick;
}

/// <summary>观测缓冲：用例期内记浮动文字、信件、消息、job、能力与弹丸事件。</summary>
/// <remarks>每类至多 512 条，超出的丢弃并输出警告，此后用例读该类即判哨兵故障。ObservesRuntime 为假的用例不记。</remarks>
public static class RimRunnerObservationBuffer
{
    internal const int MaxEventsPerKind = 512;
    private static readonly Lock Sync = new();
    private static readonly List<RimRunnerMoteObservation> Motes = [];
    private static readonly List<RimRunnerLetterObservation> Letters = [];
    private static readonly List<RimRunnerMessageObservation> Messages = [];
    private static readonly List<RimRunnerRuntimeObservation> Jobs = [];
    private static readonly List<RimRunnerRuntimeObservation> Abilities = [];
    private static readonly List<RimRunnerRuntimeObservation> Projectiles = [];
    private static readonly HashSet<string> OverflowKinds = [];
    private static readonly HashSet<string> OverflowReadKinds = [];
    private static bool recording = true;
    private static readonly HashSet<string> ObservedPawnIds = [];
    private static readonly HashSet<string> AllJobsPawnIds = [];
    private static readonly Dictionary<string, int> FilteredJobsByPawn = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> FilteredJobDefsTotal = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, int>> FilteredJobDefsByPawn = new(StringComparer.Ordinal);
    private static int filteredJobsTotal;
    private static readonly HashSet<string> ObservedProjectileIds = [];
    private static readonly Dictionary<RimRunnerMoteObservation, List<(IntVec3 Center, int Radius)>> MoteHitsByObservation = [];
    private static readonly List<(string CaseId, string Detail)> PendingUnobservedCasterWarnings = [];
    private static readonly List<(string CaseId, string Detail)> PendingUnobservedShooterWarnings = [];
    private static string? activeCaseId;

    private sealed class UnobservedCaster
    {
        internal required string Name;
        internal int Casts;
    }

    private static readonly List<UnobservedCaster> unobservedCasters = [];

    internal static string? ActiveCaseId
    {
        get
        {
            lock (Sync)
            {
                return activeCaseId;
            }
        }
    }

    internal static void BeginCase(string caseId, IEnumerable<Pawn> pawns, bool observesRuntime)
    {
        RimRunnerMapLifecyclePatches.Reset();
        lock (Sync)
        {
            activeCaseId = caseId;
            recording = observesRuntime;
            OverflowKinds.Clear();
            OverflowReadKinds.Clear();
            Motes.Clear();
            Letters.Clear();
            Messages.Clear();
            Jobs.Clear();
            Abilities.Clear();
            Projectiles.Clear();
            unobservedCasters.Clear();
            ObservedPawnIds.Clear();
            AllJobsPawnIds.Clear();
            FilteredJobsByPawn.Clear();
            FilteredJobDefsTotal.Clear();
            FilteredJobDefsByPawn.Clear();
            filteredJobsTotal = 0;
            RimRunnerPawns.ResetGeneratedThisCase();
            ObservedProjectileIds.Clear();
            MoteHitsByObservation.Clear();
            PendingUnobservedCasterWarnings.Clear();
            PendingUnobservedShooterWarnings.Clear();
            foreach (Pawn pawn in pawns)
            {
                ObservePawnUnsafe(pawn);
            }
        }
    }

    internal static void ObservePawn(Pawn pawn)
    {
        lock (Sync)
        {
            ObservePawnUnsafe(pawn);
        }
    }

    internal static bool ObserveAllJobs(Pawn pawn)
    {
        lock (Sync)
        {
            if (pawn == null || !ObservedPawnIds.Contains(ThingId(pawn)))
            {
                return false;
            }

            AllJobsPawnIds.Add(ThingId(pawn));
            return true;
        }
    }

    internal static bool IsObservedPawn(Pawn pawn)
    {
        lock (Sync)
        {
            return pawn != null && ObservedPawnIds.Contains(ThingId(pawn));
        }
    }

    internal static void ObserveProjectile(Projectile projectile)
    {
        lock (Sync)
        {
            if (projectile != null)
            {
                ObservedProjectileIds.Add(ThingId(projectile));
            }
        }
    }

    internal static void EndCase()
    {
        lock (Sync)
        {
            activeCaseId = null;
            recording = true;
        }
    }

    /// <summary>浮动文字观测快照：本用例期内经 MoteMaker.ThrowText 记下的全部条目。</summary>
    public static List<RimRunnerMoteObservation> SnapshotMotes()
    {
        NoteCaseRead("Mote");
        lock (Sync)
        {
            return [.. Motes];
        }
    }

    internal static int CountObservedMotesNear(string text, Map? map, IntVec3 center, int cellRadius)
    {
        NoteCaseRead("Mote");
        bool track = RimRunnerContext.AmbientPhase == RimRunnerPhase.Case;
        string? overlap = null;
        int count = 0;

        lock (Sync)
        {
            foreach (RimRunnerMoteObservation observation in Motes)
            {
                if (!string.Equals(observation.Text, text, StringComparison.Ordinal)
                    || (map != null && observation.Map != map))
                {
                    continue;
                }

                IntVec3 c = observation.Location.ToIntVec3();
                if (Math.Abs(c.x - center.x) > cellRadius || Math.Abs(c.z - center.z) > cellRadius)
                {
                    continue;
                }

                count++;
                if (!track || activeCaseId == null)
                {
                    continue;
                }

                if (!MoteHitsByObservation.TryGetValue(observation, out List<(IntVec3 Center, int Radius)>? hits))
                {
                    hits = [];
                    MoteHitsByObservation[observation] = hits;
                }

                bool repeatSameQuery = false;
                for (int i = 0; i < hits.Count; i++)
                {
                    (IntVec3 oldCenter, int oldRadius) = hits[i];
                    if (oldCenter.x == center.x && oldCenter.z == center.z && oldRadius == cellRadius)
                    {
                        repeatSameQuery = true;
                        break;
                    }
                }

                if (repeatSameQuery)
                {
                    continue;
                }

                for (int i = 0; i < hits.Count; i++)
                {
                    (IntVec3 oldCenter, int oldRadius) = hits[i];
                    if (oldCenter.x != center.x || oldCenter.z != center.z)
                    {
                        overlap ??= text + " 位于 " + c + " 的 mote 同时落进 " + center + "（半径 " + cellRadius
                                    + "）与 " + oldCenter + "（半径 " + oldRadius + "）";
                        break;
                    }
                }

                hits.Add((center, cellRadius));
            }
        }

        if (overlap != null)
        {
            RimRunnerWarnings.Warn(RimRunnerWarningKey.MoteNeighborOverlap,
                "同一条 mote 观测被两处邻域目标命中：" + overlap);
        }

        return count;
    }

    internal static List<RimRunnerLetterObservation> SnapshotLetters()
    {
        lock (Sync)
        {
            return [.. Letters];
        }
    }

    internal static List<RimRunnerMessageObservation> SnapshotMessages()
    {
        lock (Sync)
        {
            return [.. Messages];
        }
    }

    internal static List<RimRunnerRuntimeObservation> SnapshotJobs()
    {
        lock (Sync)
        {
            return [.. Jobs];
        }
    }

    internal static List<RimRunnerRuntimeObservation> SnapshotAbilities()
    {
        lock (Sync)
        {
            return [.. Abilities];
        }
    }

    internal static List<RimRunnerRuntimeObservation> SnapshotProjectiles()
    {
        lock (Sync)
        {
            return [.. Projectiles];
        }
    }

    internal static void RecordMote(Vector3 location, Map map, string text)
    {
        lock (Sync)
        {
            if (activeCaseId == null || !TryAccept("Mote", Motes.Count))
            {
                return;
            }

            Motes.Add(new RimRunnerMoteObservation
            {
                Location = location,
                Map = map,
                Text = text
            });
        }
    }

    internal static void RecordLetter(Letter letter)
    {
        lock (Sync)
        {
            if (activeCaseId == null || letter == null || !TryAccept("Letter", Letters.Count))
            {
                return;
            }

            Letters.Add(new RimRunnerLetterObservation
            {
                DefName = letter.def?.defName,
                Label = letter.Label.ToString(),
                Text = (letter as ChoiceLetter)?.Text.ToString()
            });
        }
    }

    internal static void RecordMessage(Message message)
    {
        lock (Sync)
        {
            if (activeCaseId == null || message == null || !TryAccept("Message", Messages.Count))
            {
                return;
            }

            Messages.Add(new RimRunnerMessageObservation
            {
                DefName = message.def?.defName,
                Text = message.text
            });
        }
    }

    internal static void RecordJobStarted(JobDriver? driver, Pawn pawn, Job job)
    {
        if (!IsTestDirectedJob(job) && !RecordsAllJobs(pawn))
        {
            CountFilteredJob(pawn, job);
            return;
        }

        RecordRuntime(Jobs, "Job", "Started", ThingId(pawn),
            "id=" + (job?.loadID.ToString() ?? "null")
                  + "; def=" + (job?.def?.defName ?? "null")
                  + "; driver=" + (driver?.GetType().Name ?? "null")
                  + "; startTick=" + (job?.startTick.ToString() ?? "null")
                  + "; jobGiver=" + (job?.jobGiver?.GetType().Name ?? "null")
                  + "; ability=" + (job?.ability?.def?.defName ?? "null")
                  + "; targetA=" + TargetText(job?.targetA ?? LocalTargetInfo.Invalid),
            requireObservedPawn: true,
            defName: job?.def?.defName,
            relatedDefName: job?.ability?.def?.defName,
            job: job);
    }

    internal static void RecordJobCleaned(
        JobDriver driver, Pawn? pawn, Job job, JobCondition condition, bool suspended)
    {
        if (!IsTestDirectedJob(job) && !RecordsAllJobs(pawn))
        {
            CountFilteredJob(pawn, job);
            return;
        }

        RecordRuntime(Jobs, "Job", suspended ? "Suspended" : "Cleaned", ThingId(pawn),
            "id=" + (job?.loadID.ToString() ?? "null")
                  + "; def=" + (job?.def?.defName ?? "null")
                  + "; driver=" + (driver?.GetType().Name ?? "null")
                  + "; ability=" + (job?.ability?.def?.defName ?? "null")
                  + "; playerForced=" + (job?.playerForced == true)
                  + "; jobGiver=" + (job?.jobGiver?.GetType().Name ?? "null")
                  + "; condition=" + condition
                  + "; tick=" + Find.TickManager.TicksGame
                  + "; curToil=" + (driver?.CurToilIndex.ToString() ?? "-")
                  + ":" + (driver?.CurToilString ?? "-")
                  + "; lastJobTag=" + (pawn?.mindState?.lastJobTag.ToString() ?? "-")
                  + "; stack=" + DiagStack(),
            requireObservedPawn: true,
            defName: job?.def?.defName,
            relatedDefName: job?.ability?.def?.defName,
            outcome: condition.ToString(),
            job: job);
    }

    internal static void RecordAbilityCastingStarted(Ability ability, Pawn pawn)
    {
        RecordRuntime(Abilities, "Ability", "CastingStarted", ThingId(pawn),
            "def=" + (ability?.def?.defName ?? "null"),
            requireObservedPawn: true,
            defName: ability?.def?.defName,
            subjectPawn: pawn);
    }

    internal static void RecordAbilityActivated(
        Ability ability, LocalTargetInfo target, LocalTargetInfo destination, bool succeeded,
        IReadOnlyDictionary<string, float> deltas)
    {
        RecordRuntime(Abilities, "Ability", "Activated", ThingId(ability.pawn),
            "def=" + (ability.def?.defName ?? "null")
                   + "; target=" + TargetText(target)
                   + "; destination=" + TargetText(destination)
                   + "; succeeded=" + succeeded
                   + string.Concat(deltas.Select(delta => "; " + delta.Key + "="
                       + (float.IsNaN(delta.Value) ? "unavailable" : delta.Value.ToString("0.###")))),
            requireObservedPawn: true,
            defName: ability.def?.defName,
            outcome: succeeded.ToString(),
            activationDeltas: deltas,
            subjectPawn: ability.pawn);
    }

    internal static void RecordProjectileLaunched(
        Projectile projectile, Thing launcher, LocalTargetInfo usedTarget, LocalTargetInfo intendedTarget)
    {
        lock (Sync)
        {
            if (activeCaseId == null || launcher is not Pawn pawn
                                     || !ObservedPawnIds.Contains(ThingId(pawn)))
            {
                return;
            }

            string projectileId = ThingId(projectile);
            ObservedProjectileIds.Add(projectileId);
            RecordRuntime(Projectiles, "Projectile", "Launched", projectileId,
                "def=" + (projectile?.def?.defName ?? "null")
                       + "; launcher=" + ThingId(launcher)
                       + "; usedTarget=" + TargetText(usedTarget)
                       + "; intendedTarget=" + TargetText(intendedTarget),
                defName: projectile?.def?.defName);
        }
    }

    internal static void RecordProjectileImpact(
        Projectile projectile, Thing? hitThing, Thing launcher, LocalTargetInfo usedTarget, LocalTargetInfo intendedTarget)
    {
        lock (Sync)
        {
            string projectileId = ThingId(projectile);
            if (activeCaseId != null && launcher is Pawn launcherPawn
                && ObservedPawnIds.Contains(ThingId(launcherPawn))
                && !ObservedProjectileIds.Contains(projectileId))
            {
                PendingUnobservedShooterWarnings.Add((activeCaseId,
                    launcherPawn.LabelShort + " 发射 " + (projectile?.def?.defName ?? "null")));
            }

            if (activeCaseId == null || !ObservedProjectileIds.Contains(projectileId))
            {
                return;
            }

            RecordRuntime(Projectiles, "Projectile", "Impacted", projectileId,
                "def=" + (projectile?.def?.defName ?? "null")
                       + "; launcher=" + ThingId(launcher)
                       + "; usedTarget=" + TargetText(usedTarget)
                       + "; intendedTarget=" + TargetText(intendedTarget)
                       + "; cell=" + (projectile?.Spawned == true
                           ? projectile.ExactPosition.ToIntVec3().ToString()
                           : "unspawned"),
                defName: projectile?.def?.defName,
                hitThing: hitThing == null ? null : ThingId(hitThing));
        }
    }

    private static void RecordRuntime(
        List<RimRunnerRuntimeObservation> observations,
        string kind,
        string stage,
        string subject,
        string detail,
        bool requireObservedPawn = false,
        string? defName = null,
        string? relatedDefName = null,
        string? outcome = null,
        IReadOnlyDictionary<string, float>? activationDeltas = null,
        Pawn? subjectPawn = null,
        Job? job = null,
        string? hitThing = null)
    {
        lock (Sync)
        {
            if (activeCaseId == null || !TryAccept(kind, observations.Count))
            {
                return;
            }

            if (requireObservedPawn && !ObservedPawnIds.Contains(subject))
            {
                if (kind == "Ability")
                {
                    if (RimRunnerPawns.IsGeneratedThisCase(subjectPawn))
                    {
                        PendingUnobservedCasterWarnings.Add((activeCaseId!,
                            subject + " 的 " + kind + "." + stage + "（" + (defName ?? "null") + "）"));
                    }

                    NoteUnobservedCaster(subjectPawn, subject);
                }

                return;
            }

            observations.Add(new RimRunnerRuntimeObservation
            {
                Stage = stage,
                Subject = subject,
                DefName = defName,
                RelatedDefName = relatedDefName,
                Outcome = outcome,
                Detail = detail,
                JobId = job?.loadID ?? -1,
                JobGiver = job?.jobGiver?.GetType().Name,
                TargetA = job == null ? null : TargetText(job.targetA),
                HitThing = hitThing,
                ActivationDeltas = activationDeltas ?? RimRunnerActivationMeters.NoDeltas,
                GameTick = Find.TickManager?.TicksGame ?? -1
            });
        }
    }

    private static void NoteUnobservedCaster(Pawn? pawn, string subject)
    {
        string name = pawn?.LabelShort ?? subject;
        foreach (UnobservedCaster seen in unobservedCasters)
        {
            if (string.Equals(seen.Name, name, StringComparison.Ordinal))
            {
                seen.Casts++;
                return;
            }
        }

        unobservedCasters.Add(new UnobservedCaster { Name = name, Casts = 1 });
    }

    internal static string? UnobservedAbilityCastsNote()
    {
        lock (Sync)
        {
            if (unobservedCasters.Count == 0)
            {
                return null;
            }

            int casts = unobservedCasters.Sum(caster => caster.Casts);
            return casts + " 次施放来自未入册 pawn（"
                   + string.Join("、", unobservedCasters.Select(caster => caster.Name + "×" + caster.Casts))
                   + "），未记入 ObservedAbilities";
        }
    }

    internal static void FlushUnobservedAbilityWarnings()
    {
        List<(string CaseId, string Detail)> pending;
        lock (Sync)
        {
            if (PendingUnobservedCasterWarnings.Count == 0)
            {
                return;
            }

            pending = [.. PendingUnobservedCasterWarnings];
            PendingUnobservedCasterWarnings.Clear();
        }

        foreach ((string caseId, string detail) in pending)
        {
            RimRunnerSentinel.Warn(RimRunnerWarningKey.UnobservedCaster,
                caseId + "：引擎生成的 pawn 施法落地时不在观测名册", detail);
        }
    }

    internal static void FlushUnobservedProjectileWarnings()
    {
        List<(string CaseId, string Detail)> pending;
        lock (Sync)
        {
            if (PendingUnobservedShooterWarnings.Count == 0)
            {
                return;
            }

            pending = [.. PendingUnobservedShooterWarnings];
            PendingUnobservedShooterWarnings.Clear();
        }

        foreach ((string caseId, string detail) in pending)
        {
            RimRunnerSentinel.Warn(RimRunnerWarningKey.UnobservedShooter,
                caseId + "：已观测 pawn 发射的弹丸落地时不在观测名册", detail);
        }
    }

    private static void ObservePawnUnsafe(Pawn pawn)
    {
        if (pawn != null)
        {
            ObservedPawnIds.Add(ThingId(pawn));
        }
    }

    private static bool IsTestDirectedJob(Job job)
    {
        return job != null && (job.playerForced || job.ability != null);
    }

    private static void CountFilteredJob(Pawn? pawn, Job? job)
    {
        if (pawn == null)
        {
            return;
        }

        lock (Sync)
        {
            if (activeCaseId == null)
            {
                return;
            }

            string id = ThingId(pawn);
            if (!ObservedPawnIds.Contains(id))
            {
                return;
            }

            FilteredJobsByPawn[id] = FilteredJobsByPawn.TryGetValue(id, out int count) ? count + 1 : 1;
            filteredJobsTotal++;

            string defName = job?.def?.defName ?? "<null>";
            FilteredJobDefsTotal[defName] = FilteredJobDefsTotal.TryGetValue(defName, out int totalCount) ? totalCount + 1 : 1;
            if (!FilteredJobDefsByPawn.TryGetValue(id, out Dictionary<string, int>? byDef))
            {
                byDef = new Dictionary<string, int>(StringComparer.Ordinal);
                FilteredJobDefsByPawn[id] = byDef;
            }

            byDef[defName] = byDef.TryGetValue(defName, out int pawnDefCount) ? pawnDefCount + 1 : 1;
        }
    }

    internal static int FilteredJobsFor(Pawn? pawn)
    {
        lock (Sync)
        {
            return pawn != null && FilteredJobsByPawn.TryGetValue(ThingId(pawn), out int count) ? count : 0;
        }
    }

    internal static IReadOnlyList<KeyValuePair<string, int>> FilteredJobDefs()
    {
        lock (Sync)
        {
            return SortFilteredDefs(FilteredJobDefsTotal);
        }
    }

    internal static IReadOnlyList<KeyValuePair<string, int>> FilteredJobDefsFor(Pawn? pawn)
    {
        lock (Sync)
        {
            if (pawn == null || !FilteredJobDefsByPawn.TryGetValue(ThingId(pawn), out Dictionary<string, int>? byDef))
            {
                return [];
            }

            return SortFilteredDefs(byDef);
        }
    }

    private static List<KeyValuePair<string, int>> SortFilteredDefs(Dictionary<string, int> defs) =>
        defs.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToList();

    internal static int FilteredJobsTotal
    {
        get
        {
            lock (Sync)
            {
                return filteredJobsTotal;
            }
        }
    }

    internal static IReadOnlyList<KeyValuePair<string, int>> PawnsWithFilteredJobs
    {
        get
        {
            lock (Sync)
            {
                return FilteredJobsByPawn.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
            }
        }
    }

    private static bool RecordsAllJobs(Pawn? pawn)
    {
        lock (Sync)
        {
            return pawn != null && AllJobsPawnIds.Contains(ThingId(pawn));
        }
    }

    internal static string DiagStack()
    {
        StackTrace st = new(2, false);
        StringBuilder sb = new();
        int n = Math.Min(st.FrameCount, 14);
        for (int i = 0; i < n; i++)
        {
            MethodBase? m = RimRunnerCallSite.MethodOf(st.GetFrame(i)!);
            string? owner = m?.DeclaringType?.Name;
            if (owner == null || owner == "DynamicMethodDefinition"
                              || owner.StartsWith("Patch_") || owner.StartsWith("RimRunnerObservation"))
            {
                continue;
            }

            sb.Append(owner).Append('.').Append(m!.Name).Append('|');
        }

        return sb.ToString();
    }

    private static string ThingId(Thing? thing)
    {
        return thing?.ThingID ?? "null";
    }

    private static string TargetText(LocalTargetInfo target)
    {
        if (!target.IsValid)
        {
            return "invalid";
        }

        return target.HasThing ? ThingId(target.Thing) : target.Cell.ToString();
    }

    internal static void NoteCaseRead(string kind)
    {
        bool caseRead = RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Case or RimRunnerPhase.None => true,
            RimRunnerPhase.Scene or RimRunnerPhase.Enumeration => false
        };
        if (!caseRead)
        {
            return;
        }

        string caseId;
        lock (Sync)
        {
            if (activeCaseId == null || !OverflowKinds.Contains(kind) || !OverflowReadKinds.Add(kind))
            {
                return;
            }

            caseId = activeCaseId;
        }

        RimRunnerSentinel.Raise("用例 " + caseId + " 读观测缓冲里已溢出的类别：本条窗内超过 " + MaxEventsPerKind
                                + " 条，超出的事件已丢，读到的不是全部事件", kind);
    }

    private static bool TryAccept(string kind, int count)
    {
        if (!recording)
        {
            return false;
        }

        if (count < MaxEventsPerKind)
        {
            return true;
        }

        if (OverflowKinds.Add(kind))
        {
            RimRunnerSentinel.Warn(RimRunnerWarningKey.ObservationOverflow, "用例 " + activeCaseId + " 的观测缓冲超过 " + MaxEventsPerKind
                      + " 条，超出的事件已丢，用例此后读这一类即判哨兵故障", kind);
        }

        return false;
    }
}
