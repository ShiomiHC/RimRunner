using Verse.AI;

namespace RimRunner;

/// <summary>pawn 生成根：按规格生成单个靶（含假人）或被测体。</summary>
/// <remarks>生成的 pawn 不带生成期老年病、旧伤与亲属关系。产品落图前钩子在取格之前执行，落图后钩子在落图与生成清理之后执行；落图后任一项施加失败即回收 pawn 再抛。</remarks>
public static class RimRunnerPawnGen
{
    /// <summary>靶生成：按靶规格生成靶，TrackPawn 登记到用例末回收。</summary>
    /// <remarks>靶规格在整个用例内参与判定，含 RegisterPawn 交棒后的续段。规格全文作为 Trace 诊断进生成 phase 的报告条目。</remarks>
    public static Pawn Target(RimRunnerContext ctx, RimRunnerTargetSpecBase spec, RimRunnerSpawnAt at)
        => ctx.TrackPawn(SpawnTarget(ctx, spec, at));

    /// <summary>探针靶生成：按靶规格生成靶，作用域结束即销毁。</summary>
    /// <remarks>靶规格在作用域内同 Target 参与判定。</remarks>
    public static IDisposable TargetProbe(RimRunnerContext ctx, RimRunnerTargetSpecBase spec, RimRunnerSpawnAt at, out Pawn target)
    {
        target = SpawnTarget(ctx, spec, at);
        ctx.WatchScopedPawn(target);
        return new ProbeScope(target);
    }

    /// <summary>被测体生成：按被测体规格生成被测体，TrackPawn 登记到用例末回收。</summary>
    /// <remarks>nick 未指定时按序取引擎的缺省昵称；at 未指定时为 RimRunnerSpawnAt.Anchored。</remarks>
    public static Pawn Subject(RimRunnerContext ctx, RimRunnerSubjectSpec spec, string? nick = null, RimRunnerSpawnAt? at = null)
        => ctx.TrackPawn(SpawnSubject(ctx, spec, nick, at));

    /// <summary>探针被测体生成：按被测体规格生成被测体，作用域结束即销毁。</summary>
    /// <remarks>nick 未指定时按序取引擎的缺省昵称；at 未指定时为 RimRunnerSpawnAt.Anchored。</remarks>
    public static IDisposable SubjectProbe(RimRunnerContext ctx, RimRunnerSubjectSpec spec, out Pawn subject,
        string? nick = null, RimRunnerSpawnAt? at = null)
    {
        subject = SpawnSubject(ctx, spec, nick, at);
        ctx.WatchScopedPawn(subject);
        return new ProbeScope(subject);
    }

    /// <summary>未登记被测体生成：按被测体规格生成被测体，回收归调用方。</summary>
    /// <remarks>nick 未指定时按序取引擎的缺省昵称；at 未指定时为 RimRunnerSpawnAt.Anchored。本入口只生成，观测与回收由调用方自己登记。</remarks>
    public static Pawn SubjectUntracked(RimRunnerContext ctx, RimRunnerSubjectSpec spec, string? nick = null, RimRunnerSpawnAt? at = null)
        => SpawnSubject(ctx, spec, nick, at);

    /// <summary>未落图被测体生成：按被测体规格生成未落图的被测体，TrackPawn 登记到用例末回收。</summary>
    /// <remarks>只做生成清理、产品落图前钩子与倒地即死 Off 的置零；落图后钩子与钉住只施加于落图的 pawn。</remarks>
    /// <exception cref="ArgumentException">规格的 agency 不是 Free 时抛。</exception>
    public static Pawn SubjectUnspawned(RimRunnerContext ctx, RimRunnerSubjectSpec spec, string? nick = null)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec), RimRunnerLog.Prefix + " RimRunnerPawnGen.SubjectUnspawned 须给被测体规格");
        spec.Validate();
        if (spec.Agency != RimRunnerDisposableAgency.Free)
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " 不落图的被测体无法钉住：" + spec);
        }

        Pawn pawn = GenerateSubject(spec, nick);
        try
        {
            RimRunnerPawns.ApplyGeneration(pawn, spec.Generation);
            if (spec.Affiliation.DeathOnDowned == RimRunnerTargetDeathOnDowned.Off) pawn.health.overrideDeathOnDownedChance = 0f;
        }
        catch
        {
            RimRunnerPawns.Cleanup(pawn);
            throw;
        }

        return ctx.TrackPawn(pawn);
    }

    private static Pawn SpawnTarget(RimRunnerContext ctx, RimRunnerTargetSpecBase spec, RimRunnerSpawnAt at)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec), RimRunnerLog.Prefix + " RimRunnerPawnGen.Target 须给靶规格");
        if (at == null) throw new ArgumentNullException(nameof(at), RimRunnerLog.Prefix + " RimRunnerPawnGen.Target 须给落点");
        spec.Validate();

        Faction? faction = ResolveFaction(spec.Stance);
        Pawn pawn = GenerateTarget(spec, faction);

        if (pawn.Faction != faction) pawn.SetFaction(faction);

        RunGeneratedHook(pawn);
        SpawnOrRecycle(ctx, pawn, at);
        try
        {
            ApplyTarget(ctx, pawn, spec);
        }
        catch
        {
            RimRunnerPawns.Cleanup(pawn);
            throw;
        }

        return pawn;
    }

    private static Pawn SpawnSubject(RimRunnerContext ctx, RimRunnerSubjectSpec spec, string? nick, RimRunnerSpawnAt? at)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec), RimRunnerLog.Prefix + " RimRunnerPawnGen.Subject 须给被测体规格");
        spec.Validate();

        Pawn pawn = GenerateSubject(spec, nick);
        SpawnOrRecycle(ctx, pawn, at ?? RimRunnerSpawnAt.Anchored);
        try
        {
            RimRunnerPawns.ApplyGeneration(pawn, spec.Generation);
            RimRunnerPawns.RunSpawnHook(pawn);
            if (spec.Affiliation.DeathOnDowned == RimRunnerTargetDeathOnDowned.Off) pawn.health.overrideDeathOnDownedChance = 0f;
            RimRunnerPawns.ApplyDisposableAgency(ctx, pawn, spec.Agency);
        }
        catch
        {
            RimRunnerPawns.Cleanup(pawn);
            throw;
        }

        return pawn;
    }

    [RimRunnerDirectGeneration("生成根本身")]
    private static Pawn GenerateSubject(RimRunnerSubjectSpec spec, string? nick)
    {
        string resolvedNick = nick ?? RimRunnerPawns.NextDefaultNick();
        RimRunnerSubjectAffiliation affiliation = spec.Affiliation;
        Faction? faction = affiliation.Faction ?? (affiliation.Stance is { } stance ? ResolveFaction(stance) : null);
        XenotypeDef? xenotype = spec.Xenotype.Resolve(spec.Humanlike);
        Pawn pawn = RimRunnerPawns.GenerateIsolatedPawn(
            spec.Kind,
            faction,
            xenotype,
            mustBeCapableOfViolence: spec.MustBeCapableOfViolence,
            forceNoBackstory: spec.ForceNoBackstory);
        RimRunnerPawns.SetNick(pawn, resolvedNick);
        if (pawn.Faction != faction) pawn.SetFaction(faction);

        RunGeneratedHook(pawn);
        return pawn;
    }

    private static void RunGeneratedHook(Pawn pawn)
    {
        try
        {
            RimRunnerPawns.RunGeneratedHook(pawn);
        }
        catch
        {
            RimRunnerPawns.Cleanup(pawn);
            throw;
        }
    }

    private static void SpawnOrRecycle(RimRunnerContext ctx, Pawn pawn, RimRunnerSpawnAt at)
    {
        IntVec3 cell;
        try
        {
            cell = at.Resolve(ctx);
        }
        catch
        {
            RimRunnerPawns.Cleanup(pawn);
            throw;
        }

        GenSpawn.Spawn(pawn, cell, at.MapFor(ctx));
    }

    private static Pawn GenerateTarget(RimRunnerTargetSpecBase spec, Faction? faction)
    {
        return spec switch
        {
            RimRunnerTargetSpec real => GenerateReal(real, faction),
            RimRunnerDummySpec dummy => GenerateDummy(dummy, faction),
            _ => throw new ArgumentException(RimRunnerLog.Prefix + " RimRunnerPawnGen 未知的靶规格类型 " + spec.GetType().Name, nameof(spec)),
        };
    }

    [RimRunnerDirectGeneration("生成根本身")]
    private static Pawn GenerateReal(RimRunnerTargetSpec spec, Faction? faction)
    {
        bool has(RimRunnerTargetGeneration item) => (spec.Generation & item) != 0;

        Pawn pawn = RimRunnerPawns.GenerateIsolatedPawn(
            spec.Kind,
            faction,
            spec.Humanlike && has(RimRunnerTargetGeneration.Baseliner) ? XenotypeDefOf.Baseliner : null,
            mustBeCapableOfViolence: spec.Violence == RimRunnerPawnViolenceProfile.Capable);
        if (has(RimRunnerTargetGeneration.FixedAge))
        {
            try
            {
                RimRunnerPawns.SetFixedAge(pawn);
            }
            catch
            {
                RimRunnerPawns.Cleanup(pawn);
                throw;
            }

            LifeStageDef adult = pawn.RaceProps.lifeStageAges[^1].def;
            if (!RimRunnerPawns.PinsMinorAge(spec.Kind) && pawn.ageTracker.CurLifeStage != adult)
            {
                RimRunnerPawns.Cleanup(pawn);
                throw new InvalidOperationException(
                    RimRunnerLog.Prefix + " " + spec.Kind.defName + " 钉成年年龄后不在末生命阶段（"
                    + pawn.ageTracker.CurLifeStage?.defName + "，末段为 " + adult.defName + "）：该物种成年晚于固定年龄");
            }
        }

        return pawn;
    }

    [RimRunnerDirectGeneration("生成根本身")]
    private static Pawn GenerateDummy(RimRunnerDummySpec spec, Faction? faction)
    {
        RimRunnerDummyDefAudit.WarnOnce(spec.Kind);
        Pawn pawn = RimRunnerPawns.GenerateIsolatedPawn(spec.Kind, faction);
        if (!RimRunnerDummies.TryGet(pawn, out RimRunnerDummyComp? comp))
        {
            string reason = RimRunnerDummies.RaceSetEmpty
                ? "假人种族集为空"
                : !RimRunnerDummies.IsDummyRace(pawn.def)
                    ? "种族 " + pawn.def.defName + " 不在假人种族集内"
                    : "实例上没有假人 comp";
            RimRunnerPawns.Cleanup(pawn);
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 假人 " + spec.Kind.defName + " 生成后没有假人 comp：" + reason);
        }

        Hediff_Injury? injury = pawn.health.hediffSet.hediffs.OfType<Hediff_Injury>().FirstOrDefault();
        if (injury != null)
        {
            RimRunnerPawns.Cleanup(pawn);
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 假人 " + spec.Kind.defName + " 生成期已带伤口 " + injury.def.defName
                + "（" + injury.Part?.def.defName + "）：血量系数须在首次受伤前施加");
        }

        comp.Configure(spec.HealthScale, spec.ThinkTree, spec.ConstantThinkTree, spec.StatBases, spec.StatsFinalized);
        return pawn;
    }

    private static void ApplyTarget(RimRunnerContext ctx, Pawn pawn, RimRunnerTargetSpecBase spec)
    {
        switch (spec)
        {
            case RimRunnerTargetSpec real:
                ApplyReal(ctx, pawn, real);
                break;
            case RimRunnerDummySpec dummy:
                ApplyDummy(pawn, dummy);
                break;
            default:
                throw new ArgumentException(RimRunnerLog.Prefix + " RimRunnerPawnGen 未知的靶规格类型 " + spec.GetType().Name, nameof(spec));
        }

        if (!spec.Threat) ctx.DisableThreat(pawn);
        switch (spec.Damage)
        {
            case RimRunnerTargetDamage.Absorbed:
                RimRunnerInvulnerability.AddAbsorbed(pawn);
                break;
            case RimRunnerTargetDamage.Woundless:
                RimRunnerInvulnerability.AddWoundless(pawn);
                break;
        }

        if (spec.DeathOnDowned == RimRunnerTargetDeathOnDowned.Off) pawn.health.overrideDeathOnDownedChance = 0f;
        if (spec.Death.Excused) ctx.ExcuseDeath(pawn, spec.Death.Reason);
        ctx.AddDiagnostic(RimRunnerDiagnosticKind.Trace, pawn.LabelShort + "/靶规格", pawn.ThingID + " " + spec);
    }

    private static void ApplyReal(RimRunnerContext ctx, Pawn pawn, RimRunnerTargetSpec spec)
    {
        RimRunnerPawns.ApplyGeneration(pawn, spec.Generation & ~RimRunnerTargetGeneration.FixedAge);
        RimRunnerPawns.RunSpawnHook(pawn);

        RimRunnerPawns.MarkSpecTarget(pawn, spec);
        RimRunnerPawns.ApplyViolenceProfile(pawn, spec.Violence);
        RimRunnerPawns.ApplyMobilityProfile(pawn, spec.Mobility);
        if (spec.Mobility == RimRunnerPawnMobilityProfile.Mobile)
        {
            if (spec.Agency == RimRunnerTargetAgency.Pinned) ctx.PinAgency(pawn);
            else if (spec.Agency == RimRunnerTargetAgency.StandThenFree) StandUntilInterrupted(pawn);
        }

        if (spec.Needs == RimRunnerTargetNeeds.Frozen) ctx.SuspendNeeds(pawn);
        if (spec.HediffBreaks == RimRunnerTargetHediffBreaks.Blocked) ctx.BlockHediffBreaks(pawn);
    }

    private static void ApplyDummy(Pawn pawn, RimRunnerDummySpec spec)
    {
        RimRunnerPawns.RunSpawnHook(pawn);
        RimRunnerPawns.MarkSpecTarget(pawn, spec);
        RimRunnerPawns.ApplyMobilityProfile(pawn, spec.Mobility);
        if (spec.StandFirst) StandUntilInterrupted(pawn);
    }

    /// <summary>令 pawn 站定等待直到被打断，期间邻格自动近战照常，不设固定到期拍数。</summary>
    /// <remarks>以 -1 拍数启动 Wait job，由伤害重判或新 job 顶掉，取代固定到期拍数的 ForceWait。</remarks>
    public static void StandUntilInterrupted(Pawn pawn)
    {
        pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait), JobCondition.InterruptForced);
    }

    private static Faction? ResolveFaction(RimRunnerTargetStance stance)
    {
        switch (stance)
        {
            case RimRunnerTargetStance.Hostile:
                return RimRunnerFactions.FindHostileIn(Find.FactionManager.AllFactionsListForReading);
            case RimRunnerTargetStance.Neutral:
                return RimRunnerFactions.FindNeutral();
            case RimRunnerTargetStance.Player:
                return Faction.OfPlayer;
            default:
                throw new ArgumentOutOfRangeException(nameof(stance), stance, null);
        }
    }

    private sealed class ProbeScope : IDisposable
    {
        private Pawn? target;

        internal ProbeScope(Pawn target)
        {
            this.target = target;
        }

        public void Dispose()
        {
            Pawn? pending = target;
            target = null;
            RimRunnerPawns.Cleanup(pending);
        }
    }
}

[HarmonyPatch(typeof(Pawn_MeleeVerbs), nameof(Pawn_MeleeVerbs.TryMeleeAttack))]
internal static class Patch_Pawn_MeleeVerbs_TryMeleeAttack_RimRunnerSentinel
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(Pawn_MeleeVerbs __instance, Thing target, bool __result)
    {
        if (!__result || RimRunnerObservationBuffer.ActiveCaseId == null)
        {
            return;
        }

        Pawn? attacker = __instance?.Pawn;
        if (attacker == null || attacker.jobs?.curDriver is not JobDriver_Wait)
        {
            return;
        }

        bool involvesTestPawn = RimRunnerPawns.IsTestPawn(attacker)
            || (target is Pawn victim && RimRunnerPawns.IsTestPawn(victim));
        if (!involvesTestPawn)
        {
            return;
        }

        string targetLabel = target is Pawn targetPawn
            ? targetPawn.LabelShort + "@" + targetPawn.Position
            : (target?.LabelShort ?? "<null>");
        RimRunnerSentinel.Warn(RimRunnerWarningKey.SpawnAdjacentHostile,
            attacker.LabelShort + " 在 JobDriver_Wait 期间触发邻格自动近战，污染受击态",
            attacker.LabelShort + "@" + attacker.Position + " 攻击 " + targetLabel
            + "（tick " + Find.TickManager.TicksGame + "）");
    }
}
