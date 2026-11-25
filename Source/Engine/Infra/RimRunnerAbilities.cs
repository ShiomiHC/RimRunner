using Verse.AI;

namespace RimRunner;

/// <summary>施放结果：CastThroughJob 一次施放的耗时、登记读数差值与观测。</summary>
public struct RimRunnerAbilityCastOutcome
{
    /// <summary>从排 job 到返回推进的 tick，含 Activated 之后的收尾段。</summary>
    public int Elapsed;
    /// <summary>施放窗内成功激活的各项登记读数差值之和，按读数名取。</summary>
    /// <remarks>描述符登记的读数都有键，没有成功激活时为 0。</remarks>
    public IReadOnlyDictionary<string, float> ActivationDeltas;
    /// <summary>施放窗内与本能力相关的 job 观测。</summary>
    public RimRunnerRuntimeObservation[] Jobs;
    /// <summary>施放窗内本能力的观测。</summary>
    public RimRunnerRuntimeObservation[] Abilities;
    /// <summary>施放窗内是否有成功的 Activate 观测。</summary>
    public bool Activated;
}

/// <summary>能力原语：装配能力、排施法 job、真实施放并收集观测、施放生命周期断言。</summary>
public static class RimRunnerAbilities
{
    /// <summary>装配能力：给 pawn 装一个能力，用例收尾摘除。</summary>
    /// <exception cref="InvalidOperationException">pawn 已持有该能力时抛，装上后无法读回也抛。</exception>
    public static Ability GainTracked(
        RimRunnerRegressionContext ctx, Pawn caster, AbilityDef def, string labelPrefix)
    {
        if (caster.abilities.GetAbility(def) != null)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + "[" + labelPrefix + "] 基线已持有 " + def.defName
                + "，无法安全使用新增后移除夹具");
        }

        caster.abilities.GainAbility(def);
        ctx.AutoRemoveAbility(caster, def);
        Ability ability = caster.abilities.GetAbility(def) ?? throw new InvalidOperationException(
                RimRunnerLog.Prefix + "[" + labelPrefix + "] " + def.defName + " 装配失败");
        return ability;
    }

    /// <summary>排施法 job 的唯一入口，用例侧须经本入口，禁裸调 QueueCastingJob。</summary>
    /// <remarks>排队前清冷却，使用充能的补满充能。</remarks>
    public static void QueueCast(Ability ability, LocalTargetInfo target)
    {
        ability.ResetCooldown();
        if (ability.UsesCharges)
        {
            ability.RemainingCharges = ability.maxCharges;
        }

        ability.QueueCastingJob(target, LocalTargetInfo.Invalid);
    }

    /// <summary>真实施放：排施法 job，逐拍钉住目标推进到 Activated，收集观测。</summary>
    /// <remarks>目标 pawn 窗内逐拍站住，殖民者施法者置 Ignore、返回前还原。Activated 后推到 job 结束、满 60 tick 或持续错误级日志熔断时返回。cell 目标不钉住，施放前在半径内的异阵营 pawn 漂出即记失败指纹。</remarks>
    /// <exception cref="InvalidOperationException">目标是施法者本人时抛（自施走 CastSelfThroughJob），目标 pawn 等待期间死亡或离图也抛。</exception>
    public static RimRunnerAbilityCastOutcome CastThroughJob(
        RimRunnerRegressionContext ctx, Pawn caster, Ability ability,
        LocalTargetInfo target, string label, int maxTicks)
    {
        if (target.Pawn == caster)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + "[" + label + "] CastThroughJob 的目标是施法者本人 "
                + caster.LabelShort + "：逐 tick 钉靶会顶替施放 job");
        }

        HostilityResponseMode? previousHostility = null;
        if (caster.playerSettings != null && caster.IsColonistPlayerControlled)
        {
            previousHostility = caster.playerSettings.hostilityResponse;
            caster.playerSettings.hostilityResponse = HostilityResponseMode.Ignore;
        }

        try
        {
            return CastThroughJobCore(ctx, caster, ability, target, label, maxTicks);
        }
        finally
        {
            if (previousHostility.HasValue && caster.playerSettings != null)
            {
                caster.playerSettings.hostilityResponse = previousHostility.Value;
            }
        }
    }

    private static RimRunnerAbilityCastOutcome CastThroughJobCore(
        RimRunnerRegressionContext ctx, Pawn caster, Ability ability,
        LocalTargetInfo target, string label, int maxTicks)
    {
        int jobStartIndex = ctx.ObservedJobsCore(warnIfFiltered: false).Count;
        int abilityStartIndex = ctx.ObservedAbilities().Count;
        List<Pawn>? cellTargetOccupants = SnapshotCellTargetOccupants(caster, ability, target);
        QueueCast(ability, target);

        int elapsed = 0;
        string lastAlivePos = target.Pawn != null ? target.Pawn.Position.ToString() : "?";
        string lastNeighbors = target.Pawn != null ? DescribeNeighbors(target.Pawn) : "";
        while (elapsed < maxTicks && !ctx.ObservedAbilities().Skip(abilityStartIndex).Any(
                   observation => observation.Stage == "Activated"
                                  && observation.DefName == ability.def.defName))
        {
            target.Pawn?.pather?.StopDead();
            if (target.Pawn != null)
            {
                if (target.Pawn.Dead || !target.Pawn.Spawned)
                {
                    throw new InvalidOperationException(
                        RimRunnerLog.Prefix + "[" + label + "] 目标 pawn 施法等待期间失效：Dead="
                        + target.Pawn.Dead + " Spawned=" + target.Pawn.Spawned + "；"
                        + DescribeTargetDeath(target.Pawn, lastAlivePos, lastNeighbors, elapsed));
                }

                lastAlivePos = target.Pawn.Position.ToString();
                lastNeighbors = DescribeNeighbors(target.Pawn);
                PawnUtility.ForceWait(target.Pawn, 120);
            }

            if (RimRunnerTimeDriver.AdvanceBurstCounted(1, shouldContinue: null) == 0)
            {
                break;
            }

            elapsed++;
        }

        for (int i = 0; i < 60 && caster.CurJob?.ability == ability; i++)
        {
            target.Pawn?.pather?.StopDead();
            if (target.Pawn != null && !target.Pawn.Dead && target.Pawn.Spawned)
            {
                PawnUtility.ForceWait(target.Pawn, 120);
            }

            if (RimRunnerTimeDriver.AdvanceBurstCounted(1, shouldContinue: null) == 0)
            {
                break;
            }

            elapsed++;
        }

        NoteCellTargetDrift(ctx, caster, ability, target, cellTargetOccupants);

        RimRunnerRuntimeObservation[] jobs = ctx.ObservedJobsCore(warnIfFiltered: false)
            .Skip(jobStartIndex)
            .Where(observation => observation.RelatedDefName == ability.def.defName)
            .ToArray();
        RimRunnerRuntimeObservation[] abilities = ctx.ObservedAbilities()
            .Skip(abilityStartIndex)
            .Where(observation => observation.DefName == ability.def.defName)
            .ToArray();
        bool activated = abilities.Any(observation => observation.Stage == "Activated"
                                                      && observation.Outcome == bool.TrueString);

        return new RimRunnerAbilityCastOutcome
        {
            Elapsed = elapsed,
            ActivationDeltas = RimRunnerActivationMeters.SumSucceeded(abilities),
            Jobs = jobs,
            Abilities = abilities,
            Activated = activated
        };
    }

    /// <summary>自施能力经 CastSelfThroughJob 施放一次的采集结果。</summary>
    public struct SelfCastOutcome
    {
        /// <summary>QueueCast 后施放 job 是否被接上。</summary>
        public bool Queued;
        /// <summary>施放窗内是否有成功的 Activate 观测。</summary>
        public bool Activated;
        /// <summary>施放窗内成功激活的各项登记读数差值之和，按读数名取。</summary>
        /// <remarks>描述符登记的读数都有键，没有成功激活时为 0，排不出 job 时为 null。</remarks>
        public IReadOnlyDictionary<string, float>? ActivationDeltas;
        /// <summary>从排 job 到 job 结束经过的 tick。</summary>
        public int Elapsed;
    }

    /// <summary>自施：以施法者本人为目标排施法 job，推进到 job 结束。</summary>
    /// <remarks>排不出 job 时返回 Queued 为假、不推进。job 在 maxTicks 内不结束判 SENTINEL_FAULT。与 QueueCast 一样先清冷却、补满充能。</remarks>
    public static SelfCastOutcome CastSelfThroughJob(
        RimRunnerRegressionContext ctx, Pawn pawn, Ability ability, int maxTicks)
    {
        int abilityStart = ctx.ObservedAbilities().Count;
        QueueCast(ability, new LocalTargetInfo(pawn));
        SelfCastOutcome outcome = default;
        outcome.Queued = pawn.CurJob?.ability == ability
            || ctx.ObservedAbilities().Skip(abilityStart).Any(o =>
                o.Stage == "CastingStarted" && o.DefName == ability.def.defName);
        if (!outcome.Queued) return outcome;

        outcome.Elapsed = RimRunnerTimeDriver.AdvanceUntil(maxTicks, () => pawn.CurJob?.ability != ability, 1);
        RimRunnerFault.Require(pawn.CurJob?.ability != ability,
            "自施能力施放 job " + maxTicks + "t 内未结束：curJob=" + (pawn.CurJobDef?.defName ?? "<null>"),
            ability.def.defName + " @" + pawn.LabelShort);

        RimRunnerRuntimeObservation[] own = ctx.ObservedAbilities().Skip(abilityStart)
            .Where(observation => observation.DefName == ability.def.defName)
            .ToArray();
        outcome.Activated = own.Any(observation => observation.Stage == "Activated"
                                                   && observation.Outcome == bool.TrueString);
        outcome.ActivationDeltas = RimRunnerActivationMeters.SumSucceeded(own);

        return outcome;
    }

    private static List<Pawn>? SnapshotCellTargetOccupants(
        Pawn? caster, Ability ability, LocalTargetInfo target)
    {
        float radius = ability.def.EffectRadius;
        if (target.Pawn != null || !target.Cell.IsValid || radius <= 0f || caster?.Map == null)
        {
            return null;
        }

        return caster.Map.mapPawns.AllPawnsSpawned
            .Where(pawn => pawn.Faction != caster.Faction
                           && pawn.Position.DistanceTo(target.Cell) <= radius)
            .ToList();
    }

    private static void NoteCellTargetDrift(
        RimRunnerRegressionContext ctx, Pawn caster, Ability ability,
        LocalTargetInfo target, List<Pawn>? occupants)
    {
        if (occupants == null)
        {
            return;
        }

        float radius = ability.def.EffectRadius;
        foreach (Pawn pawn in occupants)
        {
            bool present = pawn.Spawned && pawn.Map == caster.Map;
            float distance = present ? pawn.Position.DistanceTo(target.Cell) : -1f;
            if (present && distance <= radius)
            {
                continue;
            }

            ctx.AddFailureFingerprint(RimRunnerFindingKey.AbilityCellTargetDrift, pawn.LabelShort,
                "以 cell 目标施放 " + ability.def.defName + "：靶在 warmup 期间离开 EffectRadius(" + radius.ToString("0.##")
                + ")，结束时" + (present ? "距靶心 " + distance.ToString("0.#") + " 格" : "已离场"));
        }
    }

    /// <summary>施放生命周期断言：job 恰起一次、以 Succeeded 收尾、Activate 恰成功一次。</summary>
    public static void AssertCastLifecycle(
        RimRunnerRegressionContext ctx, RimRunnerAbilityCastOutcome outcome, string label)
    {
        ctx.AssertTrue(label + "-JobStarted",
            outcome.Jobs.Count(observation => observation.Stage == "Started") == 1,
            "能力 Job Started 数量="
            + outcome.Jobs.Count(observation => observation.Stage == "Started"),
            RimRunnerExpectationSource.Literal("Ability.QueueCastingJob → JobDriver.Notify_Starting"));
        ctx.AssertTrue(label + "-JobSucceeded",
            outcome.Jobs.Any(observation => observation.Stage == "Cleaned"
                                            && observation.Outcome == JobCondition.Succeeded.ToString()),
            "未观察到能力 Job Succeeded",
            RimRunnerExpectationSource.Literal("JobDriver.Cleanup 真实终态"));
        ctx.AssertTrue(label + "-ActivatedOnce",
            outcome.Abilities.Count(observation => observation.Stage == "Activated"
                                                   && observation.Outcome == bool.TrueString) == 1,
            "成功 Activate 数量=" + outcome.Abilities.Count(observation =>
                observation.Stage == "Activated" && observation.Outcome == bool.TrueString),
            RimRunnerExpectationSource.Literal("真实 verb/Job 进入 Ability.Activate"));
    }

    private static string DescribeNeighbors(Pawn target)
    {
        Map? map = target.Map;
        if (map == null)
        {
            return "map=null";
        }

        IntVec3 center = target.Position;
        return string.Join(" | ", GenAdj.AdjacentCellsAndInside
            .Select(offset => center + offset)
            .Where(cell => cell.InBounds(map))
            .SelectMany(cell => cell.GetThingList(map).OfType<Pawn>())
            .Where(pawn => pawn != target)
            .Distinct()
            .Select(pawn => pawn.LabelShort + "(" + pawn.def.defName + ")"
                            + " faction=" + (pawn.Faction?.Name ?? "null")
                            + " job=" + (pawn.CurJobDef?.defName ?? "null")
                            + " dist=" + center.DistanceTo(pawn.Position).ToString("0.#")));
    }

    private static string DescribeTargetDeath(
        Pawn target, string lastAlivePos, string lastNeighbors, int elapsed)
    {
        string injuries = string.Join(" | ", target.health.hediffSet.hediffs
            .Where(hediff => hediff is Hediff_Injury || hediff.sourceDef != null)
            .Select(hediff => hediff.def.defName
                              + "@" + (hediff.Part?.def.defName ?? "whole")
                              + " src=" + (hediff.sourceDef?.defName ?? "null")
                              + "/" + (hediff.sourceToolLabel ?? hediff.sourceHediffDef?.defName ?? "?")
                              + " sev=" + hediff.Severity.ToString("0.##")));
        return "elapsed=" + elapsed + "; lastAlivePos=" + lastAlivePos
               + "; lastNeighbors=[" + lastNeighbors + "]; injuries=[" + injuries + "]";
    }
}
