namespace RimRunner;

public partial class RimRunnerContext
{
    /// <summary>写一条诊断键值：用例态进报告，场面态进立场记录，不参与裁决。</summary>
    /// <remarks>随报告被保留窗滚掉；场面态由立场会话读成消息。任何档的用例与批次都能调，无需声明；分片枚举（ShardCells）里调用即抛。</remarks>
    public void AddDiagnostic(RimRunnerDiagnosticKind kind, string label, string value)
    {
        RejectInEnumeration("AddDiagnostic");
        AppendDiagnostic(kind, label, value, engineAuthored: false);
    }

    internal void AddEngineDiagnostic(RimRunnerDiagnosticKind kind, string label, string value)
    {
        RejectInEnumeration("AddDiagnostic");
        AppendDiagnostic(kind, label, value, engineAuthored: true);
    }

    private void AppendDiagnostic(RimRunnerDiagnosticKind kind, string label, string value, bool engineAuthored)
    {
        InCase.diagnostics.Add(new RimRunnerDiagnosticEntry
        {
            kind = RimRunnerDiagnosticKinds.ToWire(kind),
            label = label,
            value = value,
            gameTick = Find.TickManager?.TicksGame ?? -1,
            engineAuthored = engineAuthored
        });
    }

    /// <summary>数用例期内观测到的某文本 mote，可按地图过滤。</summary>
    /// <remarks>按位置数用 CountObservedMotesNear。</remarks>
    public int CountObservedMotes(string text, Map? map = null)
    {
        return RimRunnerObservationBuffer.SnapshotMotes().Count(observation =>
            string.Equals(observation.Text, text, StringComparison.Ordinal)
            && (map == null || observation.Map == map));
    }

    /// <summary>数 center 邻域 cellRadius 内的某文本 mote。</summary>
    /// <remarks>按 center 周围 cellRadius 格的方形邻域计数；同一条 mote 被两处非同心目标命中时出 MOTE-NEIGHBOR-OVERLAP 警告。</remarks>
    public int CountObservedMotesNear(string text, Map map, IntVec3 center, int cellRadius = 1)
    {
        return RimRunnerObservationBuffer.CountObservedMotesNear(text, map, center, cellRadius);
    }

    /// <summary>用例期内进入信堆的信件观测。</summary>
    public IReadOnlyList<RimRunnerLetterObservation> ObservedLetters()
    {
        RimRunnerObservationBuffer.NoteCaseRead("Letter");
        return RimRunnerObservationBuffer.SnapshotLetters();
    }

    /// <summary>用例期内弹出的消息观测。</summary>
    public IReadOnlyList<RimRunnerMessageObservation> ObservedMessages()
    {
        RimRunnerObservationBuffer.NoteCaseRead("Message");
        return RimRunnerObservationBuffer.SnapshotMessages();
    }

    /// <summary>已登记 pawn 的 job 起止观测，缺省只收玩家下令的与带能力的 job。</summary>
    /// <remarks>同一 tick 内起止的 job 也在；对该 pawn 调过 ObserveAllJobs 后收该 pawn 的全部 job；整表为空而本窗有 job 被缺省过滤掉时出警告。</remarks>
    public IReadOnlyList<RimRunnerRuntimeObservation> ObservedJobs()
    {
        RimRunnerObservationBuffer.NoteCaseRead("Job");
        List<RimRunnerRuntimeObservation> jobs = RimRunnerObservationBuffer.SnapshotJobs();
        if (jobs.Count == 0 && RimRunnerObservationBuffer.FilteredJobsTotal > 0)
        {
            IReadOnlyList<KeyValuePair<string, int>> filtered = RimRunnerObservationBuffer.PawnsWithFilteredJobs;
            IReadOnlyList<KeyValuePair<string, int>> defs = RimRunnerObservationBuffer.FilteredJobDefs();
            RimRunnerWarnings.Warn(RimRunnerWarningKey.JobsFilteredEmpty,
                "读完 job 观测整表为空：本窗有 " + RimRunnerObservationBuffer.FilteredJobsTotal
                + " 条 job 事件被缺省过滤（思维树签发、非玩家下令、不带能力，且所属 pawn 未经 ObserveAllJobs 声明），ObservedJobs 不包含它们。"
                + "涉及 pawn："
                + string.Join("、", filtered.Select(pair => pair.Key + "×" + pair.Value))
                + (defs.Count > 0 ? "；涉及 def：" + string.Join("、", defs.Select(pair => pair.Key + "×" + pair.Value)) : ""));
        }

        return jobs;
    }

    internal IReadOnlyList<RimRunnerRuntimeObservation> ObservedJobsCore(bool warnIfFiltered)
    {
        if (warnIfFiltered)
        {
            return ObservedJobs();
        }

        RimRunnerObservationBuffer.NoteCaseRead("Job");
        return RimRunnerObservationBuffer.SnapshotJobs();
    }

    /// <summary>某个已登记 pawn 的 job 起止观测。</summary>
    /// <remarks>结果为空且本窗有该 pawn 的 job 被过滤掉时出警告。</remarks>
    /// <param name="pawn">观测名册内的 pawn。</param>
    public IReadOnlyList<RimRunnerRuntimeObservation> ObservedJobs(Pawn pawn)
    {
        RimRunnerObservationBuffer.NoteCaseRead("Job");
        List<RimRunnerRuntimeObservation> jobs = RimRunnerObservationBuffer.SnapshotJobs()
            .Where(observation => observation.Subject == pawn?.ThingID).ToList();
        int filtered = RimRunnerObservationBuffer.FilteredJobsFor(pawn);
        if (jobs.Count == 0 && filtered > 0)
        {
            IReadOnlyList<KeyValuePair<string, int>> defs = RimRunnerObservationBuffer.FilteredJobDefsFor(pawn);
            RimRunnerWarnings.Warn(RimRunnerWarningKey.JobsFilteredEmpty,
                "pawn " + (pawn?.ThingID ?? "null") + " 有 " + filtered
                + " 条 job 事件被缺省过滤（思维树签发、非玩家下令、不带能力，且所属 pawn 未经 ObserveAllJobs 声明），ObservedJobs 不包含它们"
                + (defs.Count > 0 ? "；涉及 def：" + string.Join("、", defs.Select(pair => pair.Key + "×" + pair.Value)) : ""));
        }

        return jobs;
    }

    /// <summary>声明该 pawn 的 job 全收进 ObservedJobs，含 think tree 签发的。</summary>
    /// <remarks>声明随交棒保留，续段取回 pawn 后沿用；wander / idle 与命令式起的 job 一并收。</remarks>
    /// <param name="pawn">观测名册内的 pawn（先 RegisterPawn / TrackPawn / WatchScopedPawn）。</param>
    /// <exception cref="InvalidOperationException">pawn 不在观测名册时抛。</exception>
    public void ObserveAllJobs(Pawn pawn)
    {
        if (!RimRunnerObservationBuffer.ObserveAllJobs(pawn))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " ObserveAllJobs："
                + (pawn?.ThingID ?? "<null>") + " 不在观测名册");
        }

        if (CarriesRegisteredToNextPhase && checkpointState != null && pawn.ThingID != null)
        {
            checkpointState[AllJobsStateKeyPrefix + pawn.ThingID] = "1";
        }
    }

    private void ReplayCarriedAllJobs()
    {
        if (checkpointState == null)
        {
            return;
        }

        foreach (Pawn pawn in pawns.Values)
        {
            if (pawn?.ThingID != null && checkpointState.ContainsKey(AllJobsStateKeyPrefix + pawn.ThingID))
            {
                RimRunnerObservationBuffer.ObserveAllJobs(pawn);
            }
        }
    }

    /// <summary>已登记 pawn 的能力施放观测。</summary>
    /// <remarks>只有观测名册内 pawn 的施放入列（RegisterPawn / TrackPawn / WatchScopedPawn）；名册外有施放时，读取处记一条诊断。</remarks>
    public IReadOnlyList<RimRunnerRuntimeObservation> ObservedAbilities()
    {
        RimRunnerObservationBuffer.FlushUnobservedAbilityWarnings();
        ReportUnobservedAbilityCasts();
        RimRunnerObservationBuffer.NoteCaseRead("Ability");
        return RimRunnerObservationBuffer.SnapshotAbilities();
    }

    private bool unobservedCastsReported;

    private void ReportUnobservedAbilityCasts()
    {
        if (unobservedCastsReported)
        {
            return;
        }

        string? note = RimRunnerObservationBuffer.UnobservedAbilityCastsNote();
        if (note == null)
        {
            return;
        }

        unobservedCastsReported = true;
        AddEngineDiagnostic(RimRunnerDiagnosticKind.Coverage, "名册外的施放", note);
    }

    /// <summary>已登记 pawn 发射的弹丸观测，含发射与落地两个阶段。</summary>
    /// <remarks>Impacted 只表示进入碰撞处理、不等于命中，命中对象看 HitThing。一发弹的两条记录按 Subject 配对。</remarks>
    public IReadOnlyList<RimRunnerRuntimeObservation> ObservedProjectiles()
    {
        RimRunnerObservationBuffer.FlushUnobservedProjectileWarnings();
        RimRunnerObservationBuffer.NoteCaseRead("Projectile");
        return RimRunnerObservationBuffer.SnapshotProjectiles();
    }

    /// <summary>把一发不经 Launch 生成的弹补进观测名册。</summary>
    /// <remarks>续段开头图上的在飞弹由引擎补登记，不必再调。</remarks>
    public void ObserveProjectile(Projectile projectile)
    {
        RimRunnerObservationBuffer.ObserveProjectile(projectile);
    }

    internal void AppendRuntimeObservationEvidence()
    {
        AppendRuntimeObservationEvidence(
            "Job", RimRunnerObservationBuffer.SnapshotJobs());
        AppendRuntimeObservationEvidence(
            "Ability", RimRunnerObservationBuffer.SnapshotAbilities());
        AppendRuntimeObservationEvidence(
            "Projectile", RimRunnerObservationBuffer.SnapshotProjectiles());
    }

    private void AppendRuntimeObservationEvidence(
        string kind, IReadOnlyList<RimRunnerRuntimeObservation> observations)
    {
        for (int i = 0; i < observations.Count; i++)
        {
            RimRunnerRuntimeObservation observation = observations[i];
            InCase.diagnostics.Add(new RimRunnerDiagnosticEntry
            {
                kind = RimRunnerDiagnosticKinds.ToWire(RimRunnerDiagnosticKind.Observation),
                label = kind + " " + observation.Stage + "[" + i + "] " + observation.Subject,
                value = observation.Detail,
                gameTick = observation.GameTick,
                engineAuthored = true
            });
        }
    }
}
