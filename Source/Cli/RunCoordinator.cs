using System.Diagnostics;
using System.Text.Json;

namespace RimRunnerCli;

public sealed class RunCoordinator
{
    private readonly OrchestratorDescriptor descriptor;
    private readonly string productRoot;
    private readonly ClaimStore store;
    private string purpose;
    private IReadOnlyList<string> omittedRequiredIds = [];
    private IReadOnlyList<AcknowledgeStartupErrorPolicy.Ack> startupAcks = [];
    private IReadOnlyList<KnownFailingPolicy.Ack> knownFailing = [];
    private IReadOnlyList<AcknowledgePolicy.Ack> batchAcks = [];
    private IReadOnlyList<ComponentExemptionPolicy.Exemption> componentExemptions = [];
    private readonly ProgressLine progress;
    private readonly AdmissionGate admissionGate;
    private readonly MachineLock machineLock;
    private RunSourceIdentity? source;
    private ShardChildSpec? shardChild;
    private ShardChildResult collected = new();
    private readonly int queueMinutes;
    private string compatOff = "";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0290")]
    public RunCoordinator(OrchestratorDescriptor descriptor, string productRoot, ClaimStore store,
        string purpose = ClaimRecord.PurposeBatch,
        Action<string>? progressWrite = null,
        int queueMinutes = QueuePolicy.DefaultMinutes)
    {
        this.queueMinutes = queueMinutes;
        this.descriptor = descriptor;
        this.productRoot = productRoot;
        this.store = store;
        this.purpose = purpose;
        progress = new ProgressLine(progressWrite);
        admissionGate = new AdmissionGate(AdmissionLedger.ForClaimRoot(store.Root));
        machineLock = MachineLock.ForClaimRoot(store.Root);
    }

    public async Task<int> LaunchAsync(Launch launch, LaunchCommon common,
        ArtifactGate.Output? built = null, Roster.Files? rosters = null)
    {
        if (launch is Launch.ShardPiece piece)
        {
            shardChild = piece.Spec;
            collected = piece.Spec.Result;
            purpose = store.Read(piece.Spec.Number)?.EffectivePurpose
                      ?? throw new StoreException($"本树没有登记 {ClaimStore.Named(piece.Spec.Number)}");
        }
        else
        {
            if (launch.Purpose != purpose
                && !(launch.Purpose == ClaimRecord.PurposeBatch && purpose == ClaimRecord.PurposeSpare))
                throw new InvalidOperationException(
                    $"构造时的用途 {purpose} 与启动形态的用途 {launch.Purpose} 不匹配");
            compatOff = launch.CompatOffArg;
            collected = new ShardChildResult();
        }

        if (launch.Dispatches)
        {
            source = RunSourceIdentity.Capture(productRoot);
            source.WarnIfUnavailable(common.Log);
        }
        built ??= launch is Launch.ShardPiece { Spec.Dispatch: true }
            ? null
            : new ArtifactGate(descriptor, productRoot).Check(common.Log, sources: launch is not Launch.ShardPiece);
        rosters ??= built == null
            ? Roster.Read(descriptor, productRoot)
            : Roster.Ensure(descriptor, productRoot, built);
        return await RunOnceAsync(launch, common, built, rosters);
    }

    private bool probingSave;
    private ReportValidationResult? probeResult;

    private async Task<int> RunOnceAsync(Launch launch, LaunchCommon common,
        ArtifactGate.Output? built, Roster.Files rosters)
    {
        var (modList, forceBusy, timeoutMinutes, log, cancellationToken, customSave) = common;
        bool loadOnly = !launch.Dispatches;
        bool buildSave = launch is Launch.BuildSave;
        FilmFlow.Options? film = (launch as Launch.Film)?.Options;
        bool filming = film != null;
        bool showcase = launch is Launch.Showcase;
        StructuredRunRequest? request = launch.BatchRequest;

        Stopwatch wall = Stopwatch.StartNew();
        RimRunnerConfig.MachineConfig machineConfig = RimRunnerConfig.Machine();
        string? modListPath = null;
        ModListDefinition? definition = null;
        if (!string.IsNullOrWhiteSpace(modList))
        {
            modListPath = Path.Combine(productRoot, descriptor.ModListsDir);
            definition = ModListDefinition.Load(modListPath, modList);
            if (buildSave && definition.SaveFrom != null)
            {
                throw new StoreException(
                    $"mod 名单 {modList} 指定 [{ModListDefinition.ExtensionSection}].{ModListDefinition.SaveFromKey} = {definition.SaveFrom}，进档使用 {definition.SaveFrom} 的档");
            }
            foreach (string note in definition.Notes)
            {
                log?.Invoke($"[名单] {note}");
            }
        }

        BaselineSave baseline = customSave
            ?? BaselineSave.Default(descriptor, productRoot, definition?.SaveOwner ?? modList);
        if (definition != null && !buildSave && !baseline.Custom)
            CliBatchWarnings.Checked(RimRunner.RimRunnerWarningKey.BaselineHeader);
        if (definition != null && !buildSave
            && BaselineHeaderGate.Verdict(descriptor, baseline.SourcePath, definition) is string mismatch)
        {
            if (baseline.Custom)
            {
                log?.Invoke("[指定存档] " + mismatch);
            }
            else
            {
                log?.Invoke("[WARN] " + mismatch);
                CliBatchWarnings.Add(RimRunner.RimRunnerWarningKey.BaselineHeader, mismatch);
            }
        }

        bool maySpare = launch is Launch.Batch or Launch.Warm && shardChild == null && !forceBusy;
        var passed = new HashSet<int>();
        string? residentUnavailable = null;
        if (maySpare)
        {
            residentUnavailable = ResidentUnavailable();
        }
        else
        {
            if ((GroupHoldRefusal(forceBusy, log) ?? LiveHoldRefusal(forceBusy, log)) is int groupHeld)
                return groupHeld;

            if (RefusedEarly(forceBusy, log))
                return ExitCodes.Rejected;
        }

        ClaimRecord Pick()
        {
            if (residentUnavailable == null) return FindOrClaimInstance(log);
            return TakeSpare(residentUnavailable, passed, log);
        }

        ClaimRecord PassOver(ClaimRecord passedClaim, string reason)
        {
            passed.Add(passedClaim.Number);
            if (passed.Count > SparePassLimit)
                throw new StoreException($"连续 {passed.Count} 个自动实例都无法接手，最后一个 {passedClaim.DisplayName} {reason}，不再另起");
            if (passedClaim.PurposeKind == InstancePurpose.Batch) residentUnavailable ??= reason;
            return Pick();
        }

        int timeout = TimeoutPolicy.Resolve(timeoutMinutes, log);
        if (timeoutMinutes == null)
            CliLog.Info($"[超时] 未指定超时上限，取 {timeout} 分钟");

        string activityLabel = MachineLabel(modList, launch.ActivityNoun);
        using CancellationTokenSource queued = QueueCancellation(cancellationToken);
        using CancellationTokenSource sharing = shardChild is { Queue: { } groupQueue }
            ? CancellationTokenSource.CreateLinkedTokenSource(queued.Token, groupQueue.DrainedToken)
            : CancellationTokenSource.CreateLinkedTokenSource(queued.Token);
        MachineLock.Hold machine;
        var machineWait = new MachineLock.WaitNote();
        try
        {
            machine = await machineLock.AcquireSharedAsync(
                activityLabel,
                queueMinutes, log, sharing.Token, group: shardChild?.LockGroup,
                cpuAdmission: shardChild?.Dispatch != true, productRoot: productRoot, note: machineWait);
        }
        catch (OperationCanceledException) when (!queued.IsCancellationRequested && shardChild?.QueueDrained() == true)
        {
            return QueueDrainedAtCap(machineWait.Gate, log);
        }
        using MachineLock.Hold machineHold = machine;

        if (shardChild?.QueueDrained() == true)
            return QueueDrainedAtCap(machineWait.Gate, log);

        Stopwatch? readyClock = null;
        if (shardChild is { Dispatch: false, Queue: not null })
        {
            ShardForecast.Record forecast = shardChild.Forecast(DateTime.UtcNow);
            collected.Forecast = forecast;
            collected.DecidedAt = DateTimeOffset.Now;
            if (forecast.Skip)
            {
                log?.Invoke($"[分片] 实例 {ClaimStore.Named(shardChild.Number)}：{ShardForecast.Describe(forecast)}，未启动实例");
                return 0;
            }
        }

        if (shardChild is { Dispatch: false })
            readyClock = Stopwatch.StartNew();

        ClaimRecord claim = Pick();
        SynthMarker.SweepUnownedIfFree(descriptor.ModsFolder, store);
        IReadOnlyList<ModsFolderScan.ModsEntry> modsEntries =
            ModsFolderScan.Scan(descriptor.ModsFolder);
        ModsFolderHealthGate.Warn(modsEntries, descriptor.ModsFolder, store, log);

        string runId = RimRunner.RimRunnerRunId.New();
        string command = buildSave
            ? RimRunner.RimRunnerEngineArgs.BuildSaveCommand
            : filming ? RimRunner.RimRunnerEngineArgs.FilmCommand
            : showcase ? RimRunner.RimRunnerEngineArgs.ShowcaseCommand
            : loadOnly ? RimRunner.RimRunnerEngineArgs.LoadOnlyCommand : request!.ExpectedCommand;

        startupAcks = AcknowledgeStartupErrorPolicy.Resolve(definition?.DirectiveSource(AcknowledgeStartupErrorPolicy.DirectiveKey) ?? modList,
            definition?.DirectiveList(AcknowledgeStartupErrorPolicy.DirectiveKey));
        knownFailing = KnownFailingPolicy.Resolve(definition?.DirectiveSource(KnownFailingPolicy.DirectiveKey) ?? modList,
            definition?.DirectiveList(KnownFailingPolicy.DirectiveKey));
        batchAcks = AcknowledgePolicy.Resolve(definition?.DirectiveSource(AcknowledgePolicy.DirectiveKey) ?? modList,
            definition?.DirectiveList(AcknowledgePolicy.DirectiveKey));
        componentExemptions = ComponentExemptionPolicy.Resolve(definition?.DirectiveSource(ComponentExemptionPolicy.DirectiveKey) ?? modList,
            definition?.DirectiveList(ComponentExemptionPolicy.DirectiveKey));

        PurposePolicy policy = PurposePolicies.ForWire(purpose);
        bool demandFromBatch = request != null;
        TextureTierPolicy.Resolution textures = TextureTierPolicy.Resolve(
            definition?.DirectiveSource(TextureTierPolicy.DirectiveKey) ?? modList,
            definition?.DirectiveOrNull(TextureTierPolicy.DirectiveKey), policy.ForcesNativeTextures,
            demandFromBatch
                ? TextureTierPolicy.Demand(request!, rosters.Cases)
                : TextureTierPolicy.CaseDemand.None, film: policy.Purpose == InstancePurpose.Film);
        if (shardChild is { Dispatch: false }) RecordShardNeeds(modList, baseline, textures.Tier);
        bool createdProcess = false;
        bool loaded = false;
        InstanceLink? coldLink = null;
        ColdStartLock? startLock = null;
        int preflight = 0;
        try
        {
            ColdStartLock.Holder? takenOver = null;
            int requeries = 0;
            while (true)
            {
                ColdStartLock.Holder? attemptTakenOver = forceBusy
                    ? ColdStartLock.OwnersOf(store.Root, claim.Number).Starting
                    : null;
                if (attemptTakenOver != null
                    && !ColdStartLock.TakeOver(store.Root, claim.Number, attemptTakenOver, log))
                    return ExitCodes.Incomplete;
                takenOver ??= attemptTakenOver;

                ColdStartLock.Result lockResult = ColdStartLock.TryAcquire(store.Root, claim.Number,
                    productRoot, DateTimeOffset.Now, activityLabel);
                if (lockResult.Lock == null)
                {
                    if (maySpare)
                    {
                        claim = PassOver(claim, StartLockHeld(lockResult.HeldBy));
                        continue;
                    }

                    log?.Invoke(ColdStartLock.RefusalLine(store.Root, claim.Number, lockResult.HeldBy!, bypass: shardChild == null));
                    return ExitCodes.Rejected;
                }

                startLock = lockResult.Lock;
                ClaimRecord? current = store.Read(claim.Number);
                if (current is not null
                    && PathIdentity.Same(current.ProductRoot, claim.ProductRoot)
                    && current.PurposeKind == claim.PurposeKind)
                {
                    if (maySpare && UnavailableReason(claim, holdingStartLock: true) is { } reason)
                    {
                        startLock.Dispose();
                        startLock = null;
                        claim = PassOver(claim, reason);
                        continue;
                    }

                    break;
                }

                startLock.Dispose();
                startLock = null;
                if (shardChild != null || !claim.Policy.ProcessScoped || requeries++ >= 2)
                {
                    log?.Invoke(current is null
                        ? $"[FAIL] 无法继续执行本命令：实例 {claim.DisplayName} 的登记在本命令取得冷启动锁之前已被卸载"
                        : $"[FAIL] 无法继续执行本命令：实例 {claim.DisplayName} 的登记在本命令取得冷启动锁之前已换成另一条登记");
                    return ExitCodes.Incomplete;
                }

                CliLog.Info($"[claim] 实例 {claim.DisplayName} 的登记已随进程终止回收，重新查登记");
                claim = Pick();
            }
            if (!maySpare && (GroupHoldRefusal(forceBusy, log) ?? LiveHoldRefusal(forceBusy, log)) is int held)
                return held;
            LiveHold? heldOver = forceBusy && shardChild == null ? InstanceProbe.Hold(claim.Number) : null;

            if (!maySpare && !forceBusy && BusyRefusal(claim) is { } busy)
            {
                log?.Invoke(busy);
                return ExitCodes.Rejected;
            }

            claim = EnsureManagedSaveData(claim, RecordBuilder.SaveDataRoot, store, log);
            bool dispatch = shardChild?.Dispatch == true;
            GameFingerprint.Warn(descriptor, log);
            string fingerprint = dispatch
                ? shardChild!.ContentFingerprint ?? throw new StoreException("片的规格缺少一致性闸的内容指纹")
                : ContentFingerprint.Compute(descriptor, productRoot, modList, CommandContext.GetEnvironmentVariable,
                    baseline.SourcePath);

            List<GameProcessProbe.GameProcess> processes = GameProcessProbe.Enumerate();
            List<GameProcessProbe.GameProcess> owned =
            [
                .. processes.Where(p =>
                    IsOwnedAutoTestProcess(p, claim.Number))
            ];
            int unreadable = processes.Count(p => string.IsNullOrWhiteSpace(p.CommandLine));
            string noProcess = unreadable == 0
                ? "实例没有在运行的游戏进程"
                : $"实例没有可确认归属的游戏进程（另有 {unreadable} 个 RimWorld 进程无法读取命令行，归属未知）";
            if (owned.Count == 0 && unreadable > 0)
                log?.Invoke($"[WARN] {unreadable} 个 RimWorld 进程无法读取命令行，归属未知");
            string? state = InstanceProbe.RunState(claim.Number);
            string verdict = BusyGate.Verdict(owned.Count, state, forceBusy);
            if (verdict == "refuse")
            {
                log?.Invoke(BusyRefusalLine(store.Root, claim, state));
                return ExitCodes.Rejected;
            }

            bool preempted = verdict == "stop-preempted" || takenOver != null || heldOver != null;
            bool lent = shardChild is { Borrowed: true, Dispatch: false };
            if (!buildSave && !preempted && owned.Count > 0)
            {
                InstanceLink? reusable = InstanceLinks.Get(claim.Number);
                InstanceRuntime runtime = reusable?.Runtime ?? new InstanceRuntime();
                string? refusal = await ReuseRefusal(claim, reusable, owned, modList, fingerprint, cancellationToken)
                                  ?? TextureTierPolicy.ReuseRefusal(runtime.TextureTier, textures.Tier)
                                  ?? ExperimentPolicy.ReuseRefusal(runtime.Experiments, ExperimentPolicy.Current(productRoot))
                                  ?? StartupRejectionPolicy.ReuseRefusal(runtime.StartupRejection)
                                  ?? CompatReuseRefusal(claim);
                if (refusal == null)
                {
                    startLock.Dispose();
                    startLock = null;
                    collected.Reused = true;
                    collected.Pid = owned[0].Pid;
                    reusable?.UpdateRuntime(runtime => runtime with { CurrentRunId = runId });
                    if (film != null)
                    {
                        log?.Invoke($"[复用] 拍摄实例 {claim.DisplayName} · PID {owned[0].Pid} · runId {runId}");
                        CliLog.Follow(reusable!);
                        return await FilmSession(claim, reusable!, film, runId, modList, log, cancellationToken);
                    }

                    if (loadOnly)
                    {
                        ActivityLog.Touch(ActivityLog.Ready, claim.Number, owned[0].Pid, "reuse");
                        log?.Invoke(showcase
                            ? $"[showcase] 复用展台实例 {claim.DisplayName}，现场未回载"
                            : $"[结果] LOAD ONLY：实例 {claim.DisplayName} 已进档，未重启、未回载基线档 · runId {runId}{RunIdHint(claim, runId)}");
                        return 0;
                    }

                    StructuredRunRequest reused = EnvCheckCoverage.ForReuse(runtime, request!, out bool envChecksStripped);
                    launch.BatchAnnounce?.Invoke(reused, envChecksStripped);
                    log?.Invoke($"[复用] 实例 {claim.DisplayName} · PID {owned[0].Pid} · 贴图 {runtime.TextureTier} · runId {runId}{RunIdHint(claim, runId)}");
                    CliLog.Follow(InstanceLinks.Get(claim.Number));
                    return await DispatchAndJudge(claim.Number, reused, runId, timeout,
                        log, cancellationToken, wall, rosters.Cases,
                        cleanSave: true, claim: claim, machine: machine);
                }

                CliLog.Info($"[冷启动] 不复用实例：{refusal}");
                if (lent)
                {
                    collected.BorrowRefused = refusal;
                    return 0;
                }

                if (shardChild?.Dispatch == true)
                {
                    collected.ReuseRefused = refusal;
                    throw new StoreException($"分片实例 {claim.DisplayName} 在备片之后无法复用：{refusal}");
                }
            }
            else if (lent)
            {
                string reason = owned.Count == 0 ? noProcess : string.Join("、",
                    new[]
                        {
                            (buildSave, "建档"),
                            (verdict == "stop-preempted", "实例正在执行批次"),
                            (takenOver != null, "已接管别的 CLI 的冷启动锁"),
                            (heldOver != null, "按 --force 停止 live 保留中的实例"),
                        }
                        .Where(entry => entry.Item1).Select(entry => entry.Item2));
                CliLog.Info($"[冷启动] 不复用实例：{reason}");
                collected.BorrowRefused = reason;
                return 0;
            }
            else if (shardChild?.Dispatch == true)
            {
                throw new StoreException(owned.Count == 0
                    ? $"分片实例 {claim.DisplayName} 的进程在备片之后已退出"
                    : $"分片实例 {claim.DisplayName} 在备片之后无法复用（runState={state ?? "无应答"}）");
            }
            else
            {
                CliLog.Info("[冷启动] 不尝试复用实例：" + string.Join("、",
                    new[]
                        {
                            (buildSave, "建档"),
                            (verdict == "stop-preempted", "停止正在执行批次的实例"),
                            (takenOver != null, "已接管别的 CLI 的冷启动锁"),
                            (heldOver != null, "按 --force 停止 live 保留中的实例"),
                            (owned.Count == 0, noProcess),
                        }
                        .Where(cond => cond.Item1).Select(cond => cond.Item2)));
            }

            preflight = shardChild != null ? 0 : RunColdPreflight(log);

            if (!loadOnly)
                launch.BatchAnnounce?.Invoke(request!, false);
            if (owned.Count == 0 && InstanceGoneNotes(claim) is { Count: > 0 } gone)
                CliLog.Info("[冷启动] " + string.Join(" · ", gone));

            if (loadOnly || verdict is "stop-idle" or "stop-preempted" or "stop-unresponsive")
            {
                if (heldOver != null && owned.Count > 0)
                    log?.Invoke($"[冷启动] 按 --force 停止实例 {claim.DisplayName}：{heldOver.Describe()}");
                List<int> survivors = StopProcesses(owned, log, "cold-replace", "[冷启动] ", _ => claim.Noun,
                    overrideHold: heldOver != null);
                if (survivors.Count > 0)
                    throw new StoreException(
                        "无法停止既有实例，拒绝再启动一个实例");
            }
            claim = EnsureDescriptorDerived(claim, descriptor, store, log);
            Func<ClaimRecord, GameCache.Holder?> liveProbe = GameCache.LiveProbe(store.Root);
            new LoadDirPool(descriptor, productRoot, store).Acquire(claim, descriptor.DeployedUnits(definition?.ModIds),
                other => liveProbe(other) != null, log);
            modsEntries = ModsFolderScan.Scan(claim.ModsFolder);
            if (claim.OnWorktree)
            {
                WorktreeOutputLink.Ensure(descriptor, productRoot, log);
            }

            omittedRequiredIds = OmitRequiredPolicy.Resolve(definition?.DirectiveSource(OmitRequiredPolicy.DirectiveKey) ?? modList,
                definition?.DirectiveList(OmitRequiredPolicy.DirectiveKey),
                descriptor.RequiredPackageIds, definition?.ModIds ?? [], log, definition?.Name);
            string savesDir = descriptor.ResolveSavesDir(productRoot);
            var saveDataInitializer = new SaveDataInitializer(descriptor, productRoot);
            using (store.SweepLock())
                saveDataInitializer.Initialize(claim, null,
                    definition?.ModIds ?? [], baseline.Name,
                    baseline.SourceDir, Path.GetDirectoryName(claim.SaveData)!,
                    buildSave ? SaveDataInitializer.SavesMode.Empty
                        : SaveDataInitializer.SavesMode.CopyBaseline,
                    definition?.Name, policy.UsesPlayerConfigBase);
            CliLog.Info($"[冷启动] 已重建实例 savedata：{claim.SaveData}");
            InstanceConfig.Outcome configOutcome = saveDataInitializer.ConfigOutcome!;
            if (configOutcome.IsDefault)
                CliLog.Info("[冷启动] " + configOutcome.Announcement());
            else
                log?.Invoke("[冷启动] " + configOutcome.Announcement());
            string testModId = new TestModSynthesizer(descriptor, store).Sync(
                claim.ModsFolder, claim.Number, built!.TestDll, built.EngineDll, claim.PackageIds,
                EnginePackage.Locate().ContentDir, log, descriptor.ResolveTestModContentDir(productRoot), built.SelfTests);
            string modsConfig = Path.Combine(claim.SaveData, "Config", "ModsConfig.xml");
            TestModSynthesizer.AddPackageIdToActiveMods(modsConfig, testModId);
            CliLog.Info($"[冷启动] 已合成测试 mod {testModId}，加入 {modsConfig}");

            string[] activeIds = ReadActiveModIds(modsConfig);
            ModsConfigCaseGate.Assert(activeIds);

            List<string> missingIds = MissingRequiredIdList(
                descriptor, activeIds, claim.PackageIds, omittedRequiredIds);
            if (missingIds.Count > 0)
            {
                string available = ModListDefinition.AvailableNames(
                    Path.Combine(productRoot, descriptor.ModListsDir));
                throw new StoreException(
                    $"生效 mod 名单里缺少必需 packageId：{string.Join("、", missingIds)}（{modsConfig}）；"
                      + $"可用 mod 名单：{available}");
            }

            saveDataInitializer.RewritePrefs(
                claim.SaveData, policy.PrefsProfile, silent: policy.PrefsProfile == SaveDataInitializer.PrefsProfile.Batch);
            if (textures.Default)
                CliLog.Info(textures.Announcement);
            else
                log?.Invoke(textures.Announcement);

            BuildSaveFlow.Prepared? prepared = buildSave
                ? BuildSaveFlow.Prepare(descriptor, productRoot, claim, modList)
                : null;

            machine.Suspend();
            using CancellationTokenSource admitting = shardChild is { Dispatch: false, Queue: { } drainable }
                ? CancellationTokenSource.CreateLinkedTokenSource(queued.Token, drainable.DrainedToken)
                : CancellationTokenSource.CreateLinkedTokenSource(queued.Token);
            AdmissionGate.Reservation admitted;
            try
            {
                admitted = await AdmitInstance(claim, modList, baseline, activeIds, textures.Tier,
                    queueMinutes, log, admitting.Token);
            }
            catch (OperationCanceledException) when (!queued.IsCancellationRequested && shardChild?.QueueDrained() == true)
            {
                return DrainedWhileQueued(claim, "内存准入", log);
            }

            using AdmissionGate.Reservation vram = admitted;
            try
            {
                await machine.ResumeSharedAsync(queueMinutes, log, admitting.Token);
            }
            catch (OperationCanceledException) when (!queued.IsCancellationRequested && shardChild?.QueueDrained() == true)
            {
                return DrainedWhileQueued(claim, machineWait.Gate ?? MachineLock.MachineTitle, log);
            }
            new GameCache().Bind(claim, privateTextureCache: textures.Tier == RimRunner.RimRunnerEngineArgs.TextureTierPlaceholder);
            CommandContext.ThrowIfRevoked();
            DateTime launchedAtUtc = DateTime.UtcNow;
            InstanceLink link = coldLink = InstanceLinks.Open(claim.Number,
                TimeSpan.FromMinutes(IdleExit.Resolve(machineConfig, claim.PurposeKind)),
                GameArgsBuilder.LogPathFor(claim),
                descriptor.ResolveOutputDir(productRoot), RecordStop(store, claim.Number),
                claim.Policy.SurvivesDrain);
            string experiments = ExperimentPolicy.Current(productRoot);
            string[] args = GameArgsBuilder.Build(descriptor, productRoot, claim, runId,
                command is RimRunner.RimRunnerEngineArgs.BuildSaveCommand
                or RimRunner.RimRunnerEngineArgs.ShowcaseCommand
                or RimRunner.RimRunnerEngineArgs.FilmCommand
                    ? command
                    : RimRunner.RimRunnerEngineArgs.LoadOnlyCommand,
                prepared?.SaveName ?? baseline.Name, modList,
                prepared?.DescriptorPath, fingerprint, textures.Tier, definition?.ModIds, link.PipeName, experiments,
                LoadDirPool.KeptRoots(store), compatOff, baseline.Custom ? baseline.SourcePath : null);
            RimTooling.Processes.LaunchedProcess started;
            try
            {
                started = GameLauncher.Start(descriptor.GameExe, productRoot, args, GameArgsBuilder.LogPathFor(claim));
            }
            catch
            {
                link.Stop("游戏进程未启动");
                throw;
            }

            using RimTooling.Processes.LaunchedProcess ownedProcess = started;
            if (started.Process is not { } process)
                throw new GameUnavailableException($"游戏进程句柄不可用：{descriptor.GameExe}");
            ProcessIdentity.Stamp game = new(started.Pid, started.StartedAt);
            link.Attach(process, game);
            createdProcess = true;
            using CancellationTokenSource loading =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CommandContext.RevokedToken);
            collected.Reused = false;
            collected.Pid = process.Id;
            vram.Attach(game);
            link.UpdateRuntime(runtime => runtime with
            {
                TextureTier = textures.Tier, Experiments = experiments, LaunchRunId = runId, CurrentRunId = runId,
            });
            claim.StoppedAt = null;
            claim.StopReason = null;
            store.Write(claim);
#pragma warning disable CS8524
            Action? watchReap = claim.Policy.ReapWait switch
            {
                ReapWaitMode.Settled => () => ShardReap.WatchSettled(claim.Number, process.Id, link.Settled, store.Root),
                ReapWaitMode.StopRecorded => () => ShardReap.Watch(claim.Number, process.Id, link.StopRecorded, store.Root),
                ReapWaitMode.None => null,
            };
#pragma warning restore CS8524
            watchReap?.Invoke();
            log?.Invoke("[冷启动] "
                + (preflight > 0 ? $"前置闸 {preflight} 条 · " : "")
                + $"实例 {claim.DisplayName} · PID {process.Id} · runId {runId}{RunIdHint(claim, runId)}");

            CliLog.Follow(link);
            var loadTrace = new LoadTrace(process, GameArgsBuilder.LogPathFor(claim), link);
            loading.Token.ThrowIfCancellationRequested();
            await WaitForIdle(link, timeout, vram.MarkStartupLoaded, loading.Token,
                readyState: policy.ReadyRunState, trace: loadTrace);
            ActivityLog.LoadTrace(claim.Number, process.Id, loadTrace.Summary());

            InstanceProbe.LoadedPacks loadedPacks = InstanceProbe.LoadedPackIds(claim.Number);
            IReadOnlyCollection<string> effectiveRequired = EffectiveRequiredIds(descriptor, omittedRequiredIds);
            string? loadFault = LoadedModsGate.Verdict(loadedPacks, effectiveRequired, claim.Number, claim.PackageIds, modsEntries);
            if (loadFault != null)
                throw new StoreException(loadFault);
            if (definition != null)
                CliBatchWarnings.Checked(RimRunner.RimRunnerWarningKey.ModListNotLoaded);
            if (definition != null
                && LoadedModsGate.ModListShortfall(loadedPacks, definition.ModIds, effectiveRequired,
                    claim.PackageIds, definition.Name, modsEntries) is string shortfall)
            {
                log?.Invoke("[WARN] " + shortfall);
                CliBatchWarnings.Add(RimRunner.RimRunnerWarningKey.ModListNotLoaded, shortfall);
            }
            link.UpdateRuntime(runtime => runtime with
            {
                LoadedAt = DateTimeOffset.Now,
                ContentFingerprint = buildSave ? null : fingerprint,
            });
            int? uncached = link.UncachedTextures;
            if (uncached > 0)
                log?.Invoke($"[显存] 进档时 {uncached} 张贴图在贴图缓存里没有有效条目、常驻显存（本实例的显存峰值不进准入历史）");
            vram.MarkLoaded(uncached > 0);
            loaded = true;
            if (readyClock != null)
                collected.ReadySeconds = Math.Round(readyClock.Elapsed.TotalSeconds, 1);

            startLock.Dispose();
            startLock = null;

            if (!loadOnly)
            {
                log?.Invoke("[冷启动] 进档");
            }

            if (prepared != null)
            {
                string instanceSaves = Path.Combine(claim.SaveData, "Saves");
                BuildSaveFlow.VerifyLanding(prepared, instanceSaves, launchedAtUtc);
                if (prepared.Descriptor.Output.ScrubEngineComponent)
                {
                    BuildSaveFlow.Scrub(instanceSaves, prepared.SaveName);
                }

                try
                {
                    BuildSaveFlow.VerifyOutwardModIds(instanceSaves, prepared.SaveName,
                        TestModSynthesizer.PackageIdFor(descriptor, claim.Number), claim.PackageIds,
                        definition?.ModIds);
                }
                catch (BuildSaveFlow.OutwardModIdsException exception) when (exception.RewriteNotApplied)
                {
                    bool? same = EngineArtifact.LoadedIsPackageCopy(
                        InstanceProbe.Ping(claim.Number)?.AssemblyMvids, EnginePackage.Locate());
                    if (same == null) throw;
                    throw new StoreException(exception.Message + Environment.NewLine
                                             + "  " + EngineArtifact.PackageCopyNote(same.Value));
                }

                BuildSaveFlow.PruneDanglingRelations(instanceSaves, prepared.SaveName);
                await ProbeSave(claim.Number, instanceSaves, prepared.SaveName, timeout, log, cancellationToken, wall,
                    rosters.Cases, machine);
                BuildSaveFlow.Publish(instanceSaves, savesDir, prepared.SaveName);
                string published = ContentFingerprint.Compute(descriptor, productRoot, modList,
                    CommandContext.GetEnvironmentVariable);
                link.UpdateRuntime(runtime => runtime with { ContentFingerprint = published });

                ActivityLog.Touch(ActivityLog.Ready, claim.Number, detail: "build-save");
                log?.Invoke($"[结果] BUILD SAVE：{prepared.SaveName}.rws 已保存至 {savesDir}，"
                            + "已暂停并进入空闲状态");
                return 0;
            }

            if (film != null)
                return await FilmSession(claim, link, film, runId, modList, log, cancellationToken);

            if (loadOnly)
            {
                ActivityLog.Touch(ActivityLog.Ready, claim.Number, detail: "load-only");
                log?.Invoke(showcase ? "[showcase] 已进档并暂停" : "[结果] LOAD ONLY：已进档暂停并进入空闲状态");
                return 0;
            }

            return await DispatchAndJudge(claim.Number, request!, runId, timeout,
                log, cancellationToken, wall, rosters.Cases,
                claim: claim, machine: machine);
        }
        catch (OperationCanceledException)
        {
            if (createdProcess && !loaded) StopUnloaded(claim, log);
            throw;
        }
        catch (QueueTimeoutException exception) when (shardChild is { Dispatch: false, Queue: not null } && !createdProcess)
        {
            log?.Invoke($"[备片] {exception.Message}");
            collected.QueueTimedOut = true;
            return ExitCodes.Incomplete;
        }
        catch (ReportFlowException exception)
        {
            log?.Invoke($"[FAIL] {exception.Message}");
            if (createdProcess && !loaded)
            {
                WriteLaunchFailureDiagnosis(claim, runId, coldLink, log);
                StopUnloaded(claim, log);
            }
            return ExitPolicy.FromException(exception);
        }
        catch (Exception exception)
        {
            log?.Invoke($"[FAIL] {exception.Message}");
            if (createdProcess && !loaded)
            {
                WriteLaunchFailureDiagnosis(claim, runId, coldLink, log);
                StopUnloaded(claim, log);
            }
            return ExitPolicy.FromException(exception);
        }
        finally
        {
            _ = createdProcess;
            startLock?.Dispose();
            admissionGate.ObserveQuietly();
        }
    }

    internal async Task<string> PrepareShardGroupAsync(string? modList, int? timeoutMinutes, Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(modList))
            ModListDefinition.Load(Path.Combine(productRoot, descriptor.ModListsDir), modList);
        TimeoutPolicy.Resolve(timeoutMinutes);
        new ArtifactGate(descriptor, productRoot).Check(log);
        using CancellationTokenSource queued = QueueCancellation(cancellationToken);
        using MachineLock.Hold machine = await machineLock.AcquireSharedAsync(MachineLabel(modList, "分片组前置闸"),
            queueMinutes, log, queued.Token, productRoot: productRoot);
        int preflight = RunColdPreflight(log);
        return preflight > 0 ? $"前置闸 {preflight} 条" : "";
    }

    private static CancellationTokenSource QueueCancellation(CancellationToken token) =>
        CancellationTokenSource.CreateLinkedTokenSource(token, CommandContext.ClientGone, CommandContext.RevokedToken);

    private string MachineLabel(string? modList, string what) => InstanceLabel(modList) + " · " + what;

    private string InstanceLabel(string? modList) =>
        $"{descriptor.Product}@{TreeScope.TreeName(productRoot)} · {modList ?? "（无名单）"}";

    private List<string> HistorySources(string? modList)
    {
        string relative = modList == null
            ? OrchestratorDescriptor.FileName
            : Path.Combine(descriptor.ModListsDir, modList + RimTooling.RimWorld.RimWorldModList.Extension);
        List<string> paths = [Path.Combine(productRoot, relative)];
        if (TreeScope.Of(productRoot).MainProductRoot is string main)
            paths.Add(Path.Combine(main, relative));
        return paths;
    }

    private int QueueDrainedAtCap(string? gate, Action<string>? log)
    {
        collected.QueueDrained = true;
        collected.DrainedGate = gate;
        log?.Invoke($"[分片] 实例 {ClaimStore.Named(shardChild!.Number)}："
            + (gate == null ? "" : MachineLock.WhileQueued(gate)) + "组队列的格已全被认领，"
            + (shardChild.Dispatch ? "未执行批次" : "未启动实例"));
        return 0;
    }

    private int DrainedWhileQueued(ClaimRecord claim, string gate, Action<string>? log)
    {
        collected.QueueDrained = true;
        collected.DrainedGate = gate;
        log?.Invoke($"[分片] 实例 {claim.DisplayName}：{MachineLock.WhileQueued(gate)}组队列的格已全被认领，未启动实例");
        return 0;
    }

    private (string Vram, string Memory) AdmissionKeys(string? modList, BaselineSave baseline, string tier)
    {
        string gameDir = Path.GetDirectoryName(CommandContext.FullPath(descriptor.GameExe)) ?? "";
        string game = GameFingerprint.Identity(gameDir);
        string? group = baseline.Custom ? RimRunner.RimRunnerDataLayout.SaveLedgerKey(modList, baseline.SourcePath) : modList;
        string purposeSegment = purpose == ClaimRecord.PurposeSpare ? ClaimRecord.PurposeBatch : purpose;
        return (AdmissionGate.Key(descriptor.Product, group, purposeSegment, tier, game),
            MemoryAccount.Key(descriptor.Product, group, purposeSegment, tier, game));
    }

    private void RecordShardNeeds(string? modList, BaselineSave baseline, string tier)
    {
        (string key, string memoryKey) = AdmissionKeys(modList, baseline, tier);
        try
        {
            collected.MemoryNeed = admissionGate.EstimateMemory(memoryKey).Bytes;
            collected.VramNeed = collected.MemoryNeed > 0 ? admissionGate.KnownVram(key) : 0;
        }
        catch (StoreException exception)
        {
            CliLog.Info($"[分片] 无法读取本片的预期峰值：{exception.Message}");
        }
    }

    private async Task<AdmissionGate.Reservation> AdmitInstance(ClaimRecord claim, string? modList, BaselineSave baseline,
        IReadOnlyList<string> activeIds, string tier, int queueMinutes, Action<string>? log, CancellationToken token)
    {
        (string key, string memoryKey) = AdmissionKeys(modList, baseline, tier);
        admissionGate.RecordSourceQuietly(key, baseline.Custom ? [baseline.SourcePath] : HistorySources(modList));
        long need;
        string source;
        try
        {
            (need, source) = admissionGate.Estimate(key, activeIds, () => ContentFingerprint.ModDirectories(descriptor),
                VramModel.GameVersion(descriptor.GameExe), tier, log);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[显存] 估算失败（{exception.Message}），本轮按显存不溢出准入");
            (need, source) = (0, "估算失败");
        }

        AdmissionGate.MemoryEstimate memory = admissionGate.EstimateMemory(memoryKey);
        string label = InstanceLabel(modList);
        return await admissionGate.AdmitAsync(claim.Number, key, label, need, source, queueMinutes, log, token,
            Eviction(store, label), memoryKey, memory.Bytes, memory.Source, memory.Fallback);
    }

    private async Task<int> DispatchAndJudge(int number, StructuredRunRequest request,
        string runId, int timeout, Action<string>? log,
        CancellationToken cancellationToken, Stopwatch wall, CaseManifest roster, bool cleanSave = false,
        ClaimRecord? claim = null, MachineLock.Hold? machine = null)
    {
        using CancellationTokenSource queued = QueueCancellation(cancellationToken);
        OutputLockKind outputLockKind = shardChild != null
            ? OutputLockKind.None
            : PurposePolicies.ForWire(purpose).BatchOutputLock;
#pragma warning disable CS8524
        Func<Task<MachineLock.Hold?>> acquireOutput = outputLockKind switch
        {
            OutputLockKind.Shared => async () => await TreeScope.Of(productRoot).OutputLock(store.Root).AcquireSharedAsync(
                $"{descriptor.Product}@{ClaimStore.Named(number)} · 批次",
                queueMinutes, log, queued.Token, queueForCap: false),
            OutputLockKind.Exclusive => async () => await TreeScope.Of(productRoot).OutputLock(store.Root).AcquireExclusiveAsync(
                $"{descriptor.Product}@{ClaimStore.Named(number)} · 批次", "兼容关闭的批次",
                queueMinutes, log, queued.Token),
            OutputLockKind.None => static () => Task.FromResult<MachineLock.Hold?>(null),
        };
#pragma warning restore CS8524
        using MachineLock.Hold? output = await acquireOutput();
        ActivityLog.Touch(ActivityLog.BatchStart, number, detail: runId);
        var last = new LastReport();
        try
        {
            return await DispatchAndJudgeCore(number, request, runId, timeout,
                log, cancellationToken, queued.Token, wall, roster, cleanSave, claim, machine, last);
        }
        finally
        {
            ActivityLog.Touch(ActivityLog.BatchEnd, number, detail: runId, report: last.Report);
            if (last.Report is { } judged
                && judged.MemoryEnd?.GetValueOrDefault(RimRunner.RimRunnerWireKeys.Report.MemoryPrivateBytes) is long privateBytes)
                admissionGate.ObserveMemoryQuietly(number, privateBytes);
        }
    }

    private async Task ProbeSave(int number, string instanceSaves, string saveName, int timeout, Action<string>? log,
        CancellationToken cancellationToken, Stopwatch wall, CaseManifest roster, MachineLock.Hold? machine)
    {
        HashSet<string> excluded = new(descriptor.DefaultExcluded.Select(item => item.Id), StringComparer.Ordinal);
        List<string> ids = [.. CasesCommand.CheckIds(EnginePackage.Locate()).Where(id => !excluded.Contains(id))];
        if (ids.Count == 0)
        {
            log?.Invoke("[建档] 随包用例全被描述符排除，未执行检查批，边角文件不写入首次填值表");
            return;
        }

        log?.Invoke($"[建档] 检查批：用新档执行随包用例 {ids.Count} 条，量非被测产品组件的首次填值");
        probingSave = true;
        probeResult = null;
        try
        {
            await DispatchAndJudge(number, StructuredRunRequest.Named(ids), RimRunner.RimRunnerRunId.New(), timeout,
                log, cancellationToken, wall, roster, cleanSave: true, machine: machine);
        }
        catch (ReportFlowException exception)
        {
            log?.Invoke($"[WARN] 建档检查批未执行完：{exception.Message}；边角文件不写入首次填值表");
            return;
        }
        finally
        {
            probingSave = false;
        }

        ReportValidationResult? result = probeResult;
        if (result?.Report?.ComponentFirstFills is not { } fills)
        {
            log?.Invoke($"[WARN] 建档检查批没有交出首次填值表：{result?.Message}；边角文件不写入这张表"
                        + (result == null ? "" : $"（报告 {result.ReportPath}）"));
            return;
        }

        BuildSaveFlow.RecordFirstFills(instanceSaves, saveName, fills);
        foreach (KeyValuePair<string, string> fill in fills.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            log?.Invoke($"[建档] 自动豁免：组件 {fill.Key} 的 {string.Join("、", fill.Value.Split(','))}"
                        + "——档存在该组件首次填值之前，载这份档的批不比对这些元素");
        }

        BuildSaveFlow.ProbeFindings findings = BuildSaveFlow.ReadProbe(result.Report, result.ExitCode);
        if (findings.Issues.Count > 0)
        {
            log?.Invoke($"[WARN] 建档检查批：{string.Join("，", findings.Issues)}；报告 {result.ReportPath}");
            foreach (string line in findings.Lines) log?.Invoke(line);
            foreach (string line in findings.LogOnly) CliLog.Info(line);
        }
        else
            CliLog.Info($"[建档] 检查批通过：报告 {result.ReportPath}");
    }

    private sealed class LastReport
    {
        public EngineReport? Report;
    }

    private async Task<int> DispatchAndJudgeCore(int number, StructuredRunRequest request,
        string runId, int timeout, Action<string>? log,
        CancellationToken cancellationToken, CancellationToken queuedToken, Stopwatch wall, CaseManifest roster,
        bool cleanSave, ClaimRecord? claim, MachineLock.Hold? machine, LastReport last)
    {
        bool exclusive = false;
        if (machine != null)
        {
            InstanceLink link = LinkOf(number);
            using IDisposable inUse = link.InUse();
            await SettleOutput(link, log);
            IReadOnlyList<string> quiet = MachineLock.QuietCases(request, roster);
            if (quiet.Count > 0)
            {
                await machine.ToExclusiveAsync(quiet, queueMinutes, log, queuedToken);
                exclusive = true;
            }
        }

        await SendStructuredRun(number, request, runId, cancellationToken, cleanSave, exclusive);
        if (!probingSave) collected.RunId = runId;
        ReportValidationResult result = await WaitForReport(runId,
            request.ExpectedCommand, timeout, number, cancellationToken,
            request.ExpectedSections);
        IReadOnlyList<string> missed = machine != null && !exclusive ? MachineLock.MissedQuiet(result.Report) : [];
        if (missed.Count > 0)
        {
            runId = RimRunner.RimRunnerRunId.New();
            if (!probingSave)
                InstanceLinks.Get(number)?.UpdateRuntime(runtime => runtime with { CurrentRunId = runId });
            log?.Invoke($"[机器锁] 用例名册未登记 {string.Join(" ", missed)} 要求独占机器，引擎拒绝本批；独占重发（runId {runId}）");
            using (LinkOf(number).InUse())
                await machine!.ToExclusiveAsync(missed, queueMinutes, log, cancellationToken);
            exclusive = true;
            await WaitIdleAgain(number, timeout, cancellationToken);
            await SendStructuredRun(number, request, runId, cancellationToken, cleanSave, exclusive);
            result = await WaitForReport(runId,
                request.ExpectedCommand, timeout, number, cancellationToken,
                request.ExpectedSections);
        }
        if (probingSave)
        {
            last.Report = result.Report;
            probeResult = result;
            return result.ExitCode;
        }

        collected.RunId = runId;
        collected.ReportPath = result.ReportPath;
        collected.Validation = result;
        PrintValidation(result, log, wall.Elapsed);
        last.Report = result.Report;
        InstanceLinks.Get(number)?.UpdateRuntime(runtime =>
            StartupRejectionPolicy.Record(EnvCheckCoverage.Record(runtime, request, result.Report), result.Report));
        return result.ExitCode;
    }

    private int RunColdPreflight(Action<string>? log)
    {
        if (descriptor.ColdPreflightCommands.Count == 0)
            return 0;

        progress.Begin("[前置] 正在执行前置闸……");
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            int count = PreflightGate.Run(descriptor.ColdPreflightCommands, productRoot, log);
            progress.End($"完毕（{ReportPresentation.Elapsed((long)watch.Elapsed.TotalMilliseconds)}）");
            return count;
        }
        catch
        {
            progress.End($"中止（{ReportPresentation.Elapsed((long)watch.Elapsed.TotalMilliseconds)}）");
            throw;
        }
    }

    private async Task<string?> ReuseRefusal(ClaimRecord claim, InstanceLink? link,
        IReadOnlyList<GameProcessProbe.GameProcess> owned, string? modList, string fingerprint,
        CancellationToken token)
    {
        if (owned.Count != 1)
            return $"本树有 {owned.Count} 个自动实例进程";
        if (link?.Pid != owned[0].Pid)
            return "实例不在本编排进程名下";
        string? instanceModList = GameProcessProbe.ArgValue(
            owned[0].CommandLine, RimRunner.RimRunnerEngineArgs.ModList);
        if (!string.Equals(instanceModList, modList, StringComparison.Ordinal))
            return $"实例 mod 名单 {instanceModList ?? "（无名单）"}，本轮 {modList ?? "（无名单）"}";
        string? instanceSaveData = GameProcessProbe.ArgValue(owned[0].CommandLine, "savedatafolder");
        if (instanceSaveData != null && !PathIdentity.Same(instanceSaveData, claim.SaveData))
            return $"实例的 savedata 在 {instanceSaveData}，登记的 savedata 是 {claim.SaveData}";
        if (link.Runtime.ContentFingerprint is not { } loaded)
            return "实例未记录内容指纹";
        if (!string.Equals(loaded, fingerprint, StringComparison.Ordinal))
            return ContentFingerprint.MismatchNote;

        InstanceProbe.PingSnapshot? ping = InstanceProbe.Ping(claim.Number, touch: true);
        if (ping == null)
            return "实例无应答";
        if (ping.StartupFault != null)
            return $"实例在进档前中止：{ping.StartupFault}";
        if (!ping.InGame)
            return "实例未进档";
        if (ping.Drives > 0)
            return $"实例受过 {ping.Drives} 次 live 驱动";
        PurposePolicy policy = PurposePolicies.ForWire(purpose);
        string expectedMode = policy.EngineMode;
        string instanceMode = ping.Mode ?? RimRunner.RimRunnerInstanceMode.Batch;
        if (!string.Equals(instanceMode, expectedMode, StringComparison.Ordinal))
            return $"实例模式是 {instanceMode}，本轮需要 {expectedMode}";
        if (ping.RunState != "idle")
            return $"实例 runState={ping.RunState}";
        if (policy.ChecksFilmClockOnReuse && FilmClockSpent(owned[0].Pid) is string spent)
            return spent;

        if (shardChild is { Dispatch: true } spec)
            return SameMvids(ping.AssemblyMvids, spec.AssemblyMvids) ? null : "程序集 MVID 与一致性闸核过的不匹配";

        DllFreshnessResult freshness = DllFreshnessGate.Check(
            ping.AssemblyMvids, FreshnessSurface(), productRoot);
        if (freshness.FlowError != null)
            return freshness.FlowError;
        if (!freshness.Fresh)
            return string.Join("、", freshness.Stale) + " 与盘上不匹配";
        return null;
    }

    internal static string? FilmClockSpent(int pid)
    {
        DateTimeOffset? started;
        try
        {
            using Process process = Process.GetProcessById(pid);
            started = ProcessIdentity.StartedAt(process);
        }
        catch (ArgumentException)
        {
            return "实例进程已退出";
        }

        if (started is not DateTimeOffset at)
            return "无法读取实例进程的起始时刻";
        TimeSpan limit = TimeSpan.FromSeconds(RimRunner.RimRunnerEngineArgs.FilmPinnedTimeStart / 2);
        TimeSpan uptime = DateTimeOffset.Now - at;
        return uptime >= limit
            ? $"实例进程已运行 {(int)uptime.TotalMinutes} 分钟，拍摄复用上限 {(int)limit.TotalMinutes} 分钟"
            : null;
    }

    internal static bool SameMvids(IReadOnlyDictionary<string, string>? instance, IReadOnlyDictionary<string, string>? expected) =>
        instance != null && expected != null && instance.Count == expected.Count
        && expected.All(pair => instance.TryGetValue(pair.Key, out string? mvid) && mvid == pair.Value);

    private IReadOnlyDictionary<string, string> FreshnessSurface() =>
        EngineArtifact.FreshnessSurface(descriptor, EnginePackage.Locate());

    private int? GroupHoldRefusal(bool force, Action<string>? log)
    {
        if (shardChild != null) return null;
        List<ClaimRecord> claims = store.FindByProductRoot(productRoot, purpose);
        if (force)
            return claims.Any(claim => ColdStartLock.OwnersOf(store.Root, claim.Number).Group != null)
                   && !TakeOverGroups(store, productRoot, log)
                ? ExitCodes.Incomplete
                : null;
        foreach (ClaimRecord claim in claims)
        {
            if (ColdStartLock.OwnersOf(store.Root, claim.Number).Group is not { } held) continue;
            log?.Invoke(ColdStartLock.GroupRefusalLine(claim.DisplayName, held, DateTimeOffset.Now)
                + "；--force 撤销该分片组并停止它正在执行的片");
            return ExitCodes.Rejected;
        }

        return null;
    }

    internal static bool TakeOverGroups(ClaimStore store, string productRoot, Action<string>? log)
    {
        List<ClaimRecord> claims = store.FindByProductRoot(productRoot);
        if (ShardHold.TakeOver(store.Root, claims, log) is not { } taken) return false;
        List<ClaimRecord> busy = [.. claims.Where(claim => taken.Contains(claim.Number) && InstanceProbe.RunState(claim.Number) != "idle")];
        return busy.Count == 0 || EnsureStopped(store.Root, busy, forceBusy: true, log).Verdict == null;
    }

    internal static string LiveHoldRefusalLine(string name, LiveHold hold) =>
        $"[拒绝/live 保留] 实例 {name} {hold.Describe()}；--force 停止该实例并冷启动";

    private int? LiveHoldRefusal(bool force, Action<string>? log)
    {
        if (shardChild != null || force) return null;
        foreach (ClaimRecord claim in store.FindByProductRoot(productRoot, purpose))
        {
            if (InstanceProbe.Hold(claim.Number) is not { } hold) continue;
            log?.Invoke(LiveHoldRefusalLine(claim.DisplayName, hold));
            return ExitCodes.Rejected;
        }

        return null;
    }

    private bool RefusedEarly(bool forceBusy, Action<string>? log)
    {
        if (forceBusy || shardChild != null) return false;
        List<ClaimRecord> records = store.FindByProductRoot(productRoot, purpose);
        if (records.Count != 1 || records[0].State == ClaimRecord.StatePreparing) return false;
        int number = records[0].Number;
        if (ColdStartLock.OwnersOf(store.Root, number).Starting is { } holder)
        {
            log?.Invoke(ColdStartLock.RefusalLine(store.Root, number, holder));
            return true;
        }

        string? busy = BusyRefusal(records[0]);
        if (busy != null) log?.Invoke(busy);
        return busy != null;
    }

    private string? BusyRefusal(ClaimRecord claim) =>
        BusyState(claim) is { Busy: true } busy ? BusyRefusalLine(store.Root, claim, busy.State) : null;

    private (bool Busy, string? State) BusyState(ClaimRecord claim)
    {
        int owned = GameProcessProbe.Enumerate().Count(process => IsOwnedAutoTestProcess(process, claim.Number));
        if (owned == 0) return (false, null);
        string? state = InstanceProbe.RunState(claim.Number);
        return (BusyGate.Verdict(owned, state, forceBusy: false) == "refuse", state);
    }

    internal static string BusyRefusalLine(string claimRoot, ClaimRecord claim, string? state) =>
        $"[拒绝/实例忙] {claim.Noun} {claim.DisplayName} 正在执行：runState={state}"
        + (state == SavingState ? "；对方正在写盘"
            : state == DrivingState ? "；对方是一条 live 驱动命令"
            : BusyOrigin(claimRoot, claim));

    private const string SavingState = "saving";
    private const string DrivingState = "driving";

    private static string BusyOrigin(string claimRoot, ClaimRecord claim)
    {
        List<MachineLock.Ticket> holders =
        [
            .. MachineLock.ForClaimRoot(claimRoot).LiveHolderTickets()
                .Where(ticket => ticket.Mode == MachineLock.Shared
                                 && ticket.ProductRoot != null && PathIdentity.Same(ticket.ProductRoot, claim.ProductRoot)),
        ];
        return holders.Count != 1
            ? ""
            : $"；由 {holders[0].Label} 发起，{holders[0].Since.ToLocalTime():HH:mm:ss} 起";
    }

    private const int SparePassLimit = 4;

    private static string StartLockHeld(ColdStartLock.Holder? holder) =>
        holder == null ? "冷启动锁被持" : $"冷启动锁被持（{holder.Label ?? holder.ProductRoot}）";

    private string? ResidentUnavailable()
    {
        List<ClaimRecord> records = store.FindByProductRoot(productRoot, ClaimRecord.PurposeBatch);
        return records.Count == 1 && records[0].State != ClaimRecord.StatePreparing
            ? UnavailableReason(records[0], holdingStartLock: false)
            : null;
    }

    private string? UnavailableReason(ClaimRecord claim, bool holdingStartLock)
    {
        ColdStartLock.Owners owners = ColdStartLock.OwnersOf(store.Root, claim.Number);
        if (owners.Group != null) return "被分片组持有";
        if (!holdingStartLock && owners.Starting != null) return StartLockHeld(owners.Starting);
        if (InstanceProbe.Hold(claim.Number) != null) return "被 live 保留";
        return BusyState(claim) is { Busy: true } busy ? $"正在执行：runState={busy.State}" : null;
    }

    private ClaimRecord TakeSpare(string residentReason, IReadOnlySet<int> passed, Action<string>? log)
    {
        ClaimRecord spare = PickSpare(store.FindByProductRoot(productRoot, ClaimRecord.PurposeSpare), passed,
                                number => InstanceLinks.Get(number) != null,
                                record => UnavailableReason(record, holdingStartLock: false))
                            ?? MountSpare();
        purpose = ClaimRecord.PurposeSpare;
        string resident = store.FindByProductRoot(productRoot, ClaimRecord.PurposeBatch).FirstOrDefault()?.DisplayName
                          ?? "本树的常驻实例";
        log?.Invoke($"[另起] 常驻自动实例 {resident} {residentReason}，改用临时自动实例 {spare.DisplayName}");
        return spare;
    }

    internal static ClaimRecord? PickSpare(IEnumerable<ClaimRecord> spares, IReadOnlySet<int> passed,
        Func<int, bool> linkedHere, Func<ClaimRecord, string?> unavailable) =>
        spares.Where(record => record.State != ClaimRecord.StatePreparing && !passed.Contains(record.Number)
                               && linkedHere(record.Number))
            .OrderBy(record => record.ShardOrdinal)
            .FirstOrDefault(record => unavailable(record) == null);

    private ClaimRecord MountSpare()
    {
        var lines = new List<string>();
        try
        {
            return new MountFlow(descriptor, productRoot, store, RecordBuilder.SaveDataRoot,
                    CommandContext.GetEnvironmentVariable("RIMRUNNER_LOCALAPPDATA"))
                .Mount(lines.Add, ClaimRecord.PurposeSpare);
        }
        finally
        {
            foreach (string line in lines) CliLog.Info(line);
        }
    }

    private string RunIdHint(ClaimRecord claim, string runId) =>
        PurposePolicies.IsAuto(claim.PurposeKind) && AutoInstances.Live(store, productRoot).Count > 1
            ? $" · 后续点名：--run-id {runId}"
            : "";

    private ClaimRecord FindOrClaimInstance(Action<string>? log)
    {
        if (shardChild != null)
        {
            return store.FindByProductRoot(productRoot)
                       .FirstOrDefault(record => record.Number == shardChild.Number)
                   ?? throw new StoreException($"本树没有登记 {ClaimStore.Named(shardChild.Number)}");
        }

        return FindOrClaim(descriptor, productRoot, store, purpose, RecordBuilder.SaveDataRoot,
            CommandContext.GetEnvironmentVariable("RIMRUNNER_LOCALAPPDATA"), log);
    }

    internal static ClaimRecord FindOrClaim(OrchestratorDescriptor descriptor, string productRoot, ClaimStore store,
        string purpose, string saveDataRoot, string? localAppData, Action<string>? log)
    {
        for (int attempt = 0; ; attempt++)
        {
            List<ClaimRecord> records = store.FindSettled(productRoot, purpose);
            if (records.Count > 1) throw new StoreException($"本树存在 {records.Count} 条 {purpose} 登记，拒绝猜测");
            if (records.Count == 1) return records[0];

            if (MainWorktree.Of(productRoot) != null)
            {
                CliLog.Info($"[mount] 本树是 git worktree 且尚无 {purpose} 登记，自动挂载");
                try
                {
                    ClaimRecord mounted = new MountFlow(descriptor, productRoot, store, saveDataRoot, localAppData)
                        .Mount(log, purpose);
                    CliLog.Info($"[mount] 实例 {mounted.DisplayName} 已挂载");
                    return mounted;
                }
                catch (ProductRootClaimedException claimed) when (attempt < 2)
                {
                    CliLog.Info($"[mount] 另一条命令已为本树挂载 {purpose} 登记：实例 {claimed.Existing.DisplayName}，改用该实例");
                    continue;
                }
            }

            (ClaimRecord record, bool created) = store.ClaimForProductRoot(productRoot, purpose,
                number => RecordBuilder.Build(descriptor, productRoot, number,
                    ClaimRecord.StateReady, saveDataRoot, purpose));
            CliLog.Info(created
                ? $"[claim] 已为主树创建 {purpose} 登记：实例 {record.DisplayName}"
                : $"[claim] 另一条命令已为本树创建 {purpose} 登记：实例 {record.DisplayName}，改用该实例");
            return record;
        }
    }

    public static StopOutcome EnsureStopped(string claimRoot, IEnumerable<ClaimRecord> claims, bool forceBusy,
        Action<string>? log = null) =>
        StopClaims(claimRoot, [.. claims], forceBusy, partial: false, log);

    public sealed record StopOutcome(int Stopped, int Refused, bool Failed)
    {
        internal int? Verdict => Refused == 0 && !Failed
            ? null
            : Refused > 0 ? ExitCodes.Rejected : ExitCodes.Incomplete;
    }

    private static StopOutcome StopClaims(string claimRoot, List<ClaimRecord> targets, bool forceBusy, bool partial,
        Action<string>? log)
    {
        List<GameProcessProbe.GameProcess> processes = GameProcessProbe.Enumerate();
        var states = new Dictionary<int, string?>();
        var owners = new Dictionary<int, ColdStartLock.Owners>();
        var holds = new Dictionary<int, LiveHold?>();
        foreach (ClaimRecord claim in targets)
        {
            states[claim.Number] = InstanceProbe.RunState(claim.Number);
            owners[claim.Number] = ColdStartLock.OwnersOf(claimRoot, claim.Number);
            holds[claim.Number] = InstanceProbe.Hold(claim.Number);
        }

        StopPlan plan = PlanStop(targets, forceBusy, processes, number => states[number], number => owners[number].Starting,
            number => owners[number].Group, number => holds[number]);
        foreach ((ClaimRecord claim, string? state, ColdStartLock.Holder? holder, ColdStartLock.Holder? group, LiveHold? hold)
                 in plan.Refused)
        {
            log?.Invoke(holder != null ? ColdStartLock.RefusalLine(claimRoot, claim.Number, holder)
                : group != null ? ColdStartLock.GroupRefusalLine(claim.DisplayName, group, DateTimeOffset.Now)
                : hold != null ? LiveHoldRefusalLine(claim.DisplayName, hold)
                : BusyRefusalLine(claimRoot, claim, state));
        }

        int refused = plan.Refused.Count;
        if (refused > 0 && !partial) return new StopOutcome(0, refused, false);
        List<int> terminated = [];
        if (plan.TakenOver.Count > 0)
        {
            foreach ((int number, ColdStartLock.Holder holder) in plan.TakenOver)
            {
                if (!ColdStartLock.TakeOver(claimRoot, number, holder, log)) return new StopOutcome(0, refused, true);
                terminated.Add(number);
            }

            List<ClaimRecord> rest = [.. targets.Where(claim => plan.Refused.All(entry => entry.Claim.Number != claim.Number))];
            plan = PlanStop(rest, forceBusy, GameProcessProbe.Enumerate(), number => states[number]);
        }

        Dictionary<int, string> nouns = targets.ToDictionary(claim => claim.Number, claim => claim.Noun);
        if (StopProcesses(plan.ToStop, log, "stop", nounOf: number => nouns.GetValueOrDefault(number, "自动实例"),
                overrideHold: forceBusy).Count > 0)
            return new StopOutcome(0, refused, true);
        return new StopOutcome(plan.ToStop.Count + terminated.Count(number =>
            !plan.ToStop.Any(process => InstanceProbe.IsOwnedAutoTestProcess(process, number))), refused, false);
    }

    public static StopPlan PlanStop(IEnumerable<ClaimRecord> claims, bool forceBusy,
        List<GameProcessProbe.GameProcess> processes, Func<int, string?> stateOf,
        Func<int, ColdStartLock.Holder?>? startingOf = null, Func<int, ColdStartLock.Holder?>? groupOf = null,
        Func<int, LiveHold?>? holdOf = null)
    {
        var toStop = new List<GameProcessProbe.GameProcess>();
        var refused = new List<(ClaimRecord Claim, string? State, ColdStartLock.Holder? Starting, ColdStartLock.Holder? Group,
            LiveHold? Hold)>();
        var takenOver = new List<(int Number, ColdStartLock.Holder Holder)>();

        foreach (ClaimRecord claim in claims)
        {
            List<GameProcessProbe.GameProcess> owned =
            [
                .. processes
                    .Where(process => InstanceProbe.IsOwnedAutoTestProcess(process, claim.Number))
            ];
            string? state = stateOf(claim.Number);
            ColdStartLock.Holder? holder = startingOf?.Invoke(claim.Number);
            if (holder != null)
            {
                if (!forceBusy)
                {
                    refused.Add((claim, state, holder, null, null));
                    continue;
                }

                takenOver.Add((claim.Number, holder));
                toStop.AddRange(owned);
                continue;
            }

            if (groupOf?.Invoke(claim.Number) is { } group && !forceBusy)
            {
                refused.Add((claim, state, null, group, null));
                continue;
            }

            if (holdOf?.Invoke(claim.Number) is { } hold && !forceBusy)
            {
                refused.Add((claim, state, null, null, hold));
                continue;
            }

            switch (BusyGate.Verdict(owned.Count, state, forceBusy))
            {
                case "no-instance": break;
                case "refuse": refused.Add((claim, state, null, null, null)); break;
                case "stop-idle":
                case "stop-unresponsive":
                case "stop-preempted":
                    toStop.AddRange(owned);
                    break;
                default:
                    throw new InvalidOperationException(
                        "BusyGate 多出一支这里没处理的判定");
            }
        }

        return new StopPlan(toStop, refused, takenOver);
    }

    public sealed record StopPlan(
        List<GameProcessProbe.GameProcess> ToStop,
        List<(ClaimRecord Claim, string? State, ColdStartLock.Holder? Starting, ColdStartLock.Holder? Group, LiveHold? Hold)> Refused,
        List<(int Number, ColdStartLock.Holder Holder)> TakenOver);

    public int Stop(bool forceBusy, Action<string>? log = null) =>
        Stop(store.Root, store.FindByProductRoot(productRoot), forceBusy, "本树无运行中的实例", log);

    public static int StopAll(bool forceBusy, Action<string>? log = null) => StopAll(new ClaimStore(), forceBusy, log);

    internal static int StopAll(ClaimStore store, bool forceBusy, Action<string>? log) =>
        forceBusy && !CommandRevocation.RevokeAll("全机停止（stop --all --force）", log)
            ? ExitCodes.Incomplete
            : Stop(store.Root, store.All(), forceBusy, "全机无运行中的实例", log);

    public static int Stop(string claimRoot, IEnumerable<ClaimRecord> claims, bool forceBusy, string noneLine,
        Action<string>? log = null)
    {
        StopOutcome outcome = StopClaims(claimRoot, [.. claims], forceBusy, partial: true, log);
        if (outcome.Stopped > 0) log?.Invoke($"[done] 已停止 {outcome.Stopped} 个实例");
        if (outcome.Refused > 0) log?.Invoke($"[FAIL] {outcome.Refused} 个实例被拒，未停止");
        if (outcome.Verdict is int failure) return failure;
        if (outcome.Stopped == 0) log?.Invoke(noneLine);
        return 0;
    }

    private bool IsOwnedAutoTestProcess(GameProcessProbe.GameProcess process, int number) =>
        InstanceProbe.IsOwnedAutoTestProcess(process, number);

    internal static IdleEviction Eviction(ClaimStore store, string requester) =>
        new(store, requester,
            (processes, stopLog, stopReason) => StopProcesses(processes, stopLog, "evict", announce: false,
                reasonText: stopReason));

    internal static Action<string> RecordStop(ClaimStore store, int number) => reason =>
    {
        if (InstanceLinks.Get(number) != null || MachineSnapshot.Remote(number) != null) return;
        ClaimRecord? claim = store.Read(number);
        if (claim == null) return;
        claim.StoppedAt = DateTimeOffset.Now;
        claim.StopReason = reason;
        store.Write(claim);
        Func<ClaimRecord, GameCache.Holder?> liveProbe = GameCache.LiveProbe(store.Root);
        ReleaseSynthDirs(store, claim, other => liveProbe(other) != null, line => CliLog.Info("[stop] " + line));
    };

    internal static void ReleaseSynthDirs(ClaimStore store, ClaimRecord claim, Func<ClaimRecord, bool> isLive,
        Action<string> log)
    {
        if (TestModSynthesizer.ReleaseUnheld(store, claim.Number, isLive) is { } released)
            log($"实例 {claim.DisplayName} {released}");
        if (SaveDataInitializer.TrimStopped(store, claim.Number, isLive) is { } trimmed)
            log($"实例 {claim.DisplayName} {trimmed}");
        if (claim.LoadDirs is not { Count: > 0 })
            return;
        List<string> removed = LoadDirPool.ReleaseUnreferenced(store, claim.ModsFolder, claim.ProductRoot, isLive);
        if (removed.Count == 1)
            log($"已删除本树不再有实例在用的载入目录：{removed[0]}");
        else if (removed.Count > 1)
            log($"已删除本树不再有实例在用的载入目录 {removed.Count} 个，在 {Path.GetDirectoryName(removed[0])}：{string.Join("、", removed.Select(Path.GetFileName))}");
    }

    internal static IReadOnlyList<string> InstanceGoneNotes(ClaimRecord claim) =>
        IdleExit.StopNote(claim) is { } note ? [note] : [];

    internal static string InstanceGoneNote(ClaimRecord claim) =>
        string.Concat(InstanceGoneNotes(claim).Select(note => "；" + note));

    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan LinkExitWait = TimeSpan.FromSeconds(3);

    private static string StopReasonText(string reason) => reason switch
    {
        "stop" => "由 rimrunner stop 停止",
        "cold-replace" => "冷启动时被替换",
        "film-failed" => "拍摄未全部成功，停止",
        "film-end-failed" => "拍摄收尾失败，停止",
        "launch-failed" => "冷启动失败后停止",
        "evict" => "为腾出资源停止",
        _ => "由编排进程停止（" + reason + "）",
    };

    private static List<int> StopProcesses(List<GameProcessProbe.GameProcess> processes, Action<string>? log,
        string reason, string prefix = "", Func<int, string>? nounOf = null, bool announce = true,
        string? reasonText = null, bool overrideHold = false)
    {
        var heldBack = new HashSet<int>();
        string recorded = reasonText ?? StopReasonText(reason);
        if (announce && processes.Count > 0)
        {
            string[] named =
            [
                .. processes.Select(process =>
                    (GameProcessProbe.ClaimedNumber(process.CommandLine, RimRunner.RimRunnerEngineArgs.Instance) is int known
                     && nounOf != null ? nounOf(known) : "自动实例") + $" PID {process.Pid}")
            ];
            log?.Invoke(named.Length == 1 ? $"{prefix}停止{named[0]}" : $"{prefix}停止 {named.Length} 个进程：{string.Join("、", named)}");
        }

        foreach (GameProcessProbe.GameProcess process in processes)
        {
            int? stoppedNumber = GameProcessProbe.ClaimedNumber(process.CommandLine, RimRunner.RimRunnerEngineArgs.Instance);
            if (stoppedNumber is int touchedNumber)
                ActivityLog.Touch(ActivityLog.Stop, touchedNumber, process.Pid, reason);
            try
            {
                using Process target = Process.GetProcessById(process.Pid);
                if (stoppedNumber is int linkedNumber)
                {
                    bool asked;
                    InstanceLink? stopped = null;
                    LiveHold? held = null;
                    if (InstanceLinks.Get(linkedNumber) is { } link)
                    {
                        asked = link.Pid == process.Pid;
                        if (asked && !overrideHold && link.Hold is { } hold)
                        {
                            held = hold;
                        }
                        else if (asked)
                        {
                            link.Stop(recorded);
                            stopped = link;
                        }
                    }
                    else (asked, held) = RoutedRequests.Stop(linkedNumber, process.Pid, recorded, overrideHold);

                    if (held != null)
                    {
                        log?.Invoke($"{prefix}[拒绝/live 保留] 实例 {ClaimStore.Named(linkedNumber)} {held.Describe()}，未停止");
                        heldBack.Add(process.Pid);
                        continue;
                    }

                    if (asked && target.WaitForExit(LinkExitWait))
                    {
                        stopped?.StopRecorded.Wait(StopWait);
                        continue;
                    }
                }

                target.Kill(entireProcessTree: true);
                target.WaitForExit(StopWait);
            }
            catch { }
        }

        if (processes.Count == 0) return [];
        var alive = new HashSet<int>(GameProcessProbe.Enumerate().Select(p => p.Pid));
        List<int> survivors = [.. processes.Select(p => p.Pid).Where(alive.Contains)];
        List<int> stuck = [.. survivors.Where(pid => !heldBack.Contains(pid))];
        if (stuck.Count > 0)
            log?.Invoke($"[FAIL] {stuck.Count} 个进程停止后仍在（已等 {(int)StopWait.TotalSeconds} 秒）：PID "
                        + string.Join("、", stuck));

        return survivors;
    }

    private static InstanceLink LinkOf(int number) =>
        InstanceLinks.Get(number)
        ?? throw new ReportFlowException($"本编排进程名下没有实例 {ClaimStore.Named(number)} 的实例管道");

    private static async Task SettleOutput(InstanceLink link, Action<string>? log)
    {
        await link.Output.Settled();
        IReadOnlyList<InstanceOutput.Fault> quiet = link.Output.TakeQuietFaults();
        if (log != null)
            ReportPrinter.Write(ReportPresentation.OutputFaultLines(quiet
                .Select(fault => new EngineReportFinding { Key = null, Subject = fault.Path, Text = fault.Message }), "[WARN]"), log);
    }

    private static async Task<ReportValidationResult> WithOutputFaults(ReportValidationResult result,
        InstanceLink link)
    {
        await link.Output.Settled();
        IReadOnlyList<InstanceOutput.Fault> faults = link.Output.TakeFaults();
        return faults.Count == 0
            ? result
            : result with
            {
                ExitCode = ExitPolicy.Worst([result.ExitCode, ExitCodes.Incomplete]),
                Message = result.Report != null ? ReportValidator.Summary(result.Report, outputFaults: true)
                    : result.Message + "；" + ReportVerdict.OutputFaults,
                OutputFaults =
                [
                    .. result.OutputFaults,
                    .. faults.Select(fault => new EngineReportFinding { Key = null, Subject = fault.Path, Text = fault.Message }),
                ],
            };
    }

    internal static async Task WaitForIdle(InstanceLink link, int timeout, Action startupLoaded, CancellationToken token,
        string readyState = "idle", LoadTrace? trace = null)
    {
        DateTime launched = DateTime.UtcNow;
        WaitWatch? watch = null;
        bool observedLoading = false;
        while (true)
        {
            trace?.Take();
            if (link.Closed)
            {
                string reason = $"实例管道在载档完成前断开：{await link.ExitReasonAsync()}";
                if (link.ExitedBeforeConnect)
                    throw new GameUnavailableException(reason);
                throw new ReportFlowException(reason);
            }
            Task changed = link.Changed;
            if (!link.EverConnected)
            {
                if (timeout != 0 && DateTime.UtcNow - launched >= PreConnectLimit)
                    throw LoadTimeout(trace, $"游戏进程启动后 {PreConnectLimit.TotalMinutes:0} 分钟内引擎未连上实例管道");
            }
            else
            {
                watch ??= WaitWatch.For(timeout, DateTime.UtcNow);
                InstanceProbe.PingSnapshot? ping = InstanceProbe.Snapshot(link);
                if (ping != null)
                {
                    if (ping.StartupFault != null)
                        throw new ReportFlowException($"实例在进档前中止：{ping.StartupFault}");
                    if (ping.RunState != "idle" && !observedLoading)
                    {
                        observedLoading = true;
                        startupLoaded();
                    }

                    if (ping.InGame && observedLoading && ping.RunState == readyState) return;
                }

                if (watch?.Observe(DateTime.UtcNow, watch.NextPulse(link), null,
                        $"{(ping == null ? "启动加载" : "载档")}；实例 {ClaimStore.Named(link.Number)}") is string failure)
                    throw LoadTimeout(trace, "载档超时：" + failure);
            }

            await Task.WhenAny(changed, Task.Delay(500, token));
            token.ThrowIfCancellationRequested();
        }
    }

    private static readonly TimeSpan PreConnectLimit = TimeSpan.FromMinutes(5);

    private static ReportFlowException LoadTimeout(LoadTrace? trace, string message)
    {
        trace?.Take();
        foreach (string line in trace?.TimeoutLines() ?? []) CliLog.Info(line);
        return new ReportFlowException(message);
    }

    private static async Task WaitIdleAgain(int number, int timeout, CancellationToken token)
    {
        WaitWatch? watch = WaitWatch.For(timeout, DateTime.UtcNow);
        InstanceLink link = LinkOf(number);
        while (true)
        {
            if (link.Closed)
                throw new ReportFlowException($"实例管道在首轮结束后断开：{await link.ExitReasonAsync()}");
            Task changed = link.Changed;
            if (link.State?.RunState == "idle") return;
            if (watch?.Observe(DateTime.UtcNow, watch.NextPulse(link), null,
                    $"首轮结束后回到空闲；实例 {ClaimStore.Named(number)}") is string failure)
                throw new ReportFlowException(failure);
            await Task.WhenAny(changed, Task.Delay(500, token));
            token.ThrowIfCancellationRequested();
        }
    }

    internal static void PlanDivert(InstanceOutput output, ShardChildSpec? shardChild, string runId)
    {
        if (shardChild is { Dispatch: true, Sink: { } sink }) output.Divert(sink, shardChild.Index, runId);
        else output.Undivert();
    }

    private async Task SendStructuredRun(
        int number, StructuredRunRequest request, string runId, CancellationToken token, bool cleanSave = false,
        bool exclusive = false)
    {
        source ??= RunSourceIdentity.Capture(productRoot);
        RunSourceIdentity identity = source;
        IReadOnlyList<CliBatchWarnings.Entry> warnings = CliBatchWarnings.Snapshot();
        IReadOnlyList<string> warningChecks = CliBatchWarnings.CheckedSnapshot();
        InstanceLink link = InstanceLinks.Get(number) ?? throw new ReportFlowException($"实例 {ClaimStore.Named(number)} 不在本编排进程名下");
        SampleStore.Kind samples = SampleStore.KindFor(productRoot);
        if (SampleStore.MixedFault(descriptor.ResolveOutputDir(productRoot), samples) is string mixed)
            throw new ReportFlowException(mixed);
        link.Output.UseSamples(samples);
        PlanDivert(link.Output, shardChild, runId);
        if (shardChild is { Dispatch: true, Queue: { } queue }) link.ServeShardClaims(queue, shardChild.Index);
        InstanceLink.Reply reply = await link.RequestAsync(RimRunner.RimRunnerPipeProtocol.Ops.Run, writer =>
        {
            request.WriteParams(writer, runId, startupAcks, cleanSave, exclusive, warnings, batchAcks, knownFailing,
                saveProbe: probingSave, componentExemptions: componentExemptions, warningChecks: warningChecks);
            identity.WriteParams(writer);
            if (shardChild?.Dispatch == true) shardChild.WriteParams(writer);
        }, RunRequestTimeout, token, request.OversizeNote());
        if (reply.Status == 409) throw new ReportFlowException("实例正在执行批次，拒绝下发新批次");
        if (!reply.Ok)
            throw new ReportFlowException($"下发批次失败：{reply.ErrorText()}");
        using JsonDocument doc = JsonDocument.Parse(reply.Body!);
        string? accepted = doc.RootElement.GetProperty("accepted").GetString();
        if (accepted != request.ExpectedCommand)
            throw new ReportFlowException($"下发批次的回包 command 不匹配：{accepted} ≠ {request.ExpectedCommand}");
    }

    private void PrintValidation(
        ReportValidationResult result, Action<string>? log, TimeSpan wallClock)
    {
        if (log == null) return;
        if (result.Valid && result.Report != null)
        {
            SkewNote? skew = SkewDiagnosis.Describe(
                result.Report,
                InstanceConfig.ModSettingsBaseOf(descriptor, PurposePolicies.ForWire(purpose).UsesPlayerConfigBase),
                FreshnessSurface().Keys.ToList(),
                () => SkewDiagnosis.ReadBehindMainWorktree(productRoot));
            ReportPrinter.Write(ReportPresentation.FormatTiered(
                result.Report, result.ExitCode, skew: skew, wallClock: wallClock,
                identityChecked: true,
                otherLedgers: SiblingLedgers.ForReport(descriptor, productRoot, result.Report),
                hasOutputFaults: result.OutputFaults.Count > 0), log);
        }
        else
            log(result.Message);
        ReportPrinter.Write(ReportPresentation.OutputFaultLines(result.OutputFaults), log);
        if (result.Report == null) return;
        if (result.ExitCode != 0) log($"[报告] {result.ReportPath}");
        else CliLog.Info($"[报告] {result.ReportPath}");
    }

    private async Task<ReportValidationResult> WaitForReport(string runId,
        string command, int timeout, int number, CancellationToken token,
        IReadOnlyList<string> expectedSections)
    {
        WaitWatch? watch = WaitWatch.For(timeout, DateTime.UtcNow);
        InstanceLink link = LinkOf(number);
        Task<InstanceOutput.Report> arrival = link.Output.Arrival(runId);
        DateTime? retryingSince = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (arrival.IsCompleted)
            {
                InstanceOutput.Report report = arrival.Result;
                if (report.Fault != null)
                    throw new ReportFlowException($"报告未写入磁盘：{report.ArchivePath}；{report.Fault}（runId={runId}）");
                (ReportValidationResult result, string path) = ValidateArrived(report, runId, command, expectedSections);
                if (result.Retryable)
                {
                    DateTime now = DateTime.UtcNow;
                    retryingSince ??= now;
                    if (timeout != 0 && now - retryingSince >= TimeSpan.FromMinutes(timeout))
                        throw new ReportFlowException($"报告已到达，{timeout} 分钟内仍无法读取本轮报告：{result.Message}"
                                                        + $"（runId={runId}；实例 {ClaimStore.Named(number)}）");
                    await Task.Delay(500, token); continue;
                }
                return await WithOutputFaults(result with { ReportPath = path }, link);
            }
            if (link.Closed)
            {
                await link.Output.Drained.WaitAsync(token);
                if (arrival.IsCompleted) continue;
                throw new ReportFlowException($"实例管道在报告交出前断开：{await link.ExitReasonAsync()}（runId={runId}，"
                                                + $"实例 {ClaimStore.Named(number)}）");
            }
            if (watch != null)
            {
                if (watch.Observe(DateTime.UtcNow, watch.NextPulse(link), runId, number) is string failure)
                    throw new ReportFlowException(failure + TimeoutProgressNote(await ProgressAtFailure(link, token)));
            }
            await Task.WhenAny(arrival, link.Changed, Task.Delay(1000, token));
        }
    }

    internal static (ReportValidationResult Result, string Path) ValidateArrived(InstanceOutput.Report report,
        string runId, string command, IReadOnlyList<string> expectedSections)
    {
        string path = report.LatestPath ?? report.ArchivePath;
        ReportValidationResult result = ReportValidator.Validate(path, runId, command, expectedSections);
        if (result.Valid || path == report.ArchivePath) return (result, path);
        return (ReportValidator.Validate(report.ArchivePath, runId, command, expectedSections), report.ArchivePath);
    }

    private static async Task<string?> ProgressAtFailure(InstanceLink link, CancellationToken token)
    {
        InstanceLink.Reply reply = await link.RequestAsync(RimRunner.RimRunnerPipeProtocol.Ops.Progress,
            timeout: ProgressAtFailureTimeout, token: token);
        return reply.Ok ? reply.Body : link.LatestPulse?.Body;
    }

    private static readonly TimeSpan ProgressAtFailureTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RunRequestTimeout = TimeSpan.FromSeconds(30);

    internal static string TimeoutProgressNote(string? body)
    {
        const string head = "\n  失败时的进度：";
        if (body == null)
            return head + "无（实例未回包，也未推送过脉冲）";

        IReadOnlyList<string> lines;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string? mismatch = LivePresentation.SchemaMismatch(
                doc.RootElement, RimRunner.RimRunnerLiveContract.Schema.Progress);
            if (mismatch != null)
                return head + mismatch;
            lines = LivePresentation.FormatProgress(doc.RootElement, null, latestSegmentOnly: true);
        }
        catch (JsonException)
        {
            return head + "回包不是 JSON";
        }

        return head + string.Concat(lines.Skip(1).Select(line => "\n  " + line));
    }

    private async Task<int> FilmSession(ClaimRecord claim, InstanceLink link, FilmFlow.Options film, string runId,
        string? modList, Action<string>? log, CancellationToken token)
    {
        int code;
        try
        {
            code = await FilmFlow.RunAsync(link, film,
                Path.Combine(descriptor.ResolveOutputDir(productRoot), RimRunner.RimRunnerDataLayout.FilmDirName),
                new FilmFlow.Provenance(runId, DateTimeOffset.Now, FilmSource(), modList),
                InstanceProbe.Ping(claim.Number)?.AssemblyMvids,
                line => log?.Invoke(line), token);
        }
        catch
        {
            StopOwned(claim, log, "film-failed", "[film] ");
            throw;
        }

        if (code != 0)
        {
            StopOwned(claim, log, "film-failed", "[film] ");
            return code;
        }

        string? endFailure;
        try
        {
            endFailure = await FilmFlow.EndAsync(link, film, line => log?.Invoke(line), token);
        }
        catch
        {
            StopOwned(claim, log, "film-end-failed", "[film] ");
            throw;
        }

        if (endFailure != null)
        {
            log?.Invoke($"[WARN] 拍摄实例收尾失败：{endFailure}");
            StopOwned(claim, log, "film-end-failed", "[film] ");
            return code;
        }

        ActivityLog.Touch(ActivityLog.Ready, claim.Number, detail: "film");
        log?.Invoke($"[film] 实例 {claim.DisplayName} 已收尾并进入空闲状态");
        return code;
    }

    private RunSourceIdentity FilmSource() => RunSourceIdentity.Capture(productRoot);

    private void StopUnloaded(ClaimRecord claim, Action<string>? log) =>
        StopOwned(claim, log, "launch-failed", "[冷启动] ");

    private void StopOwned(ClaimRecord claim, Action<string>? log, string reason, string prefix)
    {
        List<GameProcessProbe.GameProcess> owned;
        try
        {
            owned = [.. GameProcessProbe.Enumerate().Where(p => IsOwnedAutoTestProcess(p, claim.Number))];
        }
        catch (StoreException exception)
        {
            log?.Invoke($"{prefix}无法枚举游戏进程，本轮启动的实例未停止：{exception.Message}");
            return;
        }

        StopProcesses(owned, log, reason, prefix);
    }

    private void WriteLaunchFailureDiagnosis(ClaimRecord claim, string runId, InstanceLink? link, Action<string>? log)
    {
        log?.Invoke("[现场] 本轮未产出结果：");
        List<GameProcessProbe.GameProcess> processes =
        [
            .. GameProcessProbe.Enumerate()
                .Where(p => IsOwnedAutoTestProcess(p, claim.Number))
        ];
        log?.Invoke(processes.Count > 0
            ? $"  游戏进程：运行中 PID={string.Join(",", processes.Select(p => p.Pid))}"
            : "  游戏进程：已退出");
        if (SteamInstall() && !SteamClientRunning())
            log?.Invoke("  Steam 客户端进程：未运行");
        string modsConfig = Path.Combine(claim.SaveData, "Config", "ModsConfig.xml");
        if (!File.Exists(modsConfig))
        {
            log?.Invoke($"  生效 mod 名单：{modsConfig} 不存在");
        }
        else
        {
            try
            {
                var doc = new System.Xml.XmlDocument(); doc.Load(modsConfig);
                string[] ids =
                [
                    .. doc.SelectNodes("//activeMods/li")!.Cast<System.Xml.XmlNode>()
                        .Select(n => n.InnerText.Trim()).Where(s => s.Length > 0)
                ];
                bool missing = MissingRequiredIdList(descriptor, ids, claim.PackageIds, omittedRequiredIds).Count > 0;
                log?.Invoke($"  生效 mod 名单：{ids.Length} 项" +
                    (missing ? "，缺少必需 packageId" : "，必需 packageId 都在"));
            }
            catch (Exception exception) { log?.Invoke($"  生效 mod 名单：无法解析（{exception.Message}）"); }
        }
        string logPath = GameArgsBuilder.LogPathFor(claim);
        if (!File.Exists(logPath)) log?.Invoke($"  实例日志：{logPath} 不存在");
        else
        {
            try
            {
                using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string text = reader.ReadToEnd();
                string? identity = LaunchLogIdentity.Mismatch(text, runId);
                if (identity != null)
                {
                    log?.Invoke($"  实例日志：{logPath}");
                    log?.Invoke("    " + identity);
                }
                else
                {
                    log?.Invoke($"  实例日志：{logPath}（{text.SplitLineCount()} 行）");
                    if (link?.EverConnected != true)
                    {
                        IReadOnlyList<string> earliest = FirstErrorLines(text, 5);
                        if (earliest.Count > 0)
                        {
                            log?.Invoke("    日志最早的错误行：");
                            foreach (string line in earliest) log?.Invoke("      " + line);
                        }

                        string? mention = FirstLineMentioning(
                            text, AssemblyStem(descriptor.TestMod.AssemblyName));
                        if (mention != null && !earliest.Contains(mention))
                            log?.Invoke($"    日志里提到测试 mod DLL 的第一行：{mention}");
                    }
                }
            }
            catch (Exception exception) { log?.Invoke($"  实例日志：无法读取（{exception.Message}）"); }
        }

        if (link != null)
        {
            string shook = link.Hello != null ? "已握手" : "未握手";
            log?.Invoke(link.Connected ? $"  实例管道：引擎已连接，{shook}"
                : link.EverConnected ? $"  实例管道：引擎曾连接、已断开，{shook}"
                : "  实例管道：引擎未连接");
        }
    }

    private bool SteamInstall()
    {
        string exe = descriptor.GameExe.Replace('/', '\\');
        return exe.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)
               || (Path.GetDirectoryName(exe) is { Length: > 0 } dir
                   && File.Exists(Path.Combine(dir, "steam_api64.dll")));
    }

    private static bool SteamClientRunning()
    {
        try
        {
            return GameProcessProbe.EnumerateByImageName("steam.exe").Count > 0
                   || GameProcessProbe.EnumerateByImageName("steamwebhelper.exe").Count > 0;
        }
        catch (StoreException)
        {
            return true;
        }
    }

    public static ClaimRecord EnsureManagedSaveData(
        ClaimRecord claim, string saveDataRoot, ClaimStore store, Action<string>? log)
    {
        string expected = Path.Combine(saveDataRoot, claim.Number.ToString());
        if (PathIdentity.Same(claim.SaveData, expected))
        {
            return claim;
        }

        CliLog.Info(
            $"[claim] 实例 {claim.DisplayName} 的 savedata 迁至托管根：{claim.SaveData} → {expected}");
        claim.SaveData = expected;
        store.Write(claim);
        return claim;
    }

    private string? CompatReuseRefusal(ClaimRecord claim) =>
        !PurposePolicies.ForWire(purpose).ChecksCompatMirrorOnReuse || PathIdentity.Same(claim.ModsFolder, descriptor.ModsFolder)
            ? null
            : $"兼容关闭镜像：实例启动自 {CompatMirror.MirrorOf(claim.ModsFolder) ?? claim.ModsFolder}，本轮为 {CompatMirror.MirrorOf(descriptor.ModsFolder)}";

    public static ClaimRecord EnsureDescriptorDerived(
        ClaimRecord claim, OrchestratorDescriptor descriptor, ClaimStore store, Action<string>? log)
    {
        ClaimRecord derived = RecordBuilder.Build(descriptor, claim.ProductRoot, claim.Number, claim.State,
            Path.GetDirectoryName(claim.SaveData), claim.EffectivePurpose);
        var changes = new List<string>();
        if (!string.Equals(claim.Product, derived.Product, StringComparison.Ordinal))
            changes.Add($"product：{claim.Product} → {derived.Product}");
        if (!string.Equals(claim.ModsFolder, derived.ModsFolder, StringComparison.Ordinal))
            changes.Add($"modsFolder：{claim.ModsFolder} → {derived.ModsFolder}");
        string? staleTestMod = claim.TestMod != null && !PathIdentity.Same(claim.TestMod, derived.TestMod!)
            ? claim.TestMod
            : null;
        if (staleTestMod != null)
            changes.Add($"testMod：{claim.TestMod} → {derived.TestMod}");
        List<string> legacyWrappers = claim.Wrappers ?? [];
        if (changes.Count == 0 && claim.TestMod != null && legacyWrappers.Count == 0)
        {
            return claim;
        }

        if (legacyWrappers.Count > 0 || staleTestMod != null)
        {
            using (store.SweepLock())
            {
                foreach (string wrapper in legacyWrappers)
                {
                    LegacyWrappers.RemoveWrapper(wrapper);
                    CliLog.Info($"[claim] 实例 {claim.DisplayName} 旧登记记着的旧形态载入目录已删除：{wrapper}");
                }

                if (staleTestMod != null && TestModSynthesizer.Unlink(store, staleTestMod) is { } form)
                    CliLog.Info($"[claim] 实例 {claim.DisplayName} 不再使用的测试 mod "
                        + (form == "链接" ? "链接已摘除" : "已删除") + $"：{staleTestMod}");
            }
        }

        claim.Product = derived.Product;
        claim.ModsFolder = derived.ModsFolder;
        claim.Wrappers = null;
        claim.TestMod = derived.TestMod;
        store.Write(claim);
        foreach (string change in changes)
        {
            CliLog.Info($"[claim] 实例 {claim.DisplayName} 的登记随编排描述符更新 {change}");
        }

        return claim;
    }

    internal static string AssemblyStem(string assemblyName) =>
        Path.GetFileNameWithoutExtension(assemblyName);

    internal static IReadOnlyList<string> FirstErrorLines(string text, int max)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (found.Count >= max) break;
            string line = GameLogLine.Body(raw).Trim();
            if (line.Length == 0 || line.StartsWith("at ", StringComparison.Ordinal)) continue;
            if (!LooksLikeError(line)) continue;
            string clipped = line.Length > 300 ? line[..300] + "…" : line;
            if (seen.Add(clipped)) found.Add(clipped);
        }

        return found;
    }

    private static bool LooksLikeError(string line) =>
        line.Contains("Exception", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Error", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Could not", StringComparison.OrdinalIgnoreCase);

    internal static string? FirstLineMentioning(string text, string needle)
    {
        foreach (string raw in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            string line = GameLogLine.Body(raw).Trim();
            if (line.Length > 0 && line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return line.Length > 300 ? line[..300] + "…" : line;
        }
        return null;
    }

    public static bool MissingRequiredIds(
        OrchestratorDescriptor descriptor, IEnumerable<string> activeIds, IReadOnlyList<string> suffixedIds) =>
        MissingRequiredIdList(descriptor, activeIds, suffixedIds).Count > 0;

    public static List<string> MissingRequiredIdList(
        OrchestratorDescriptor descriptor, IEnumerable<string> activeIds, IReadOnlyList<string> suffixedIds) =>
        MissingRequiredIdList(descriptor, activeIds, suffixedIds, []);

    public static List<string> MissingRequiredIdList(
        OrchestratorDescriptor descriptor, IEnumerable<string> activeIds, IReadOnlyList<string> suffixedIds,
        IReadOnlyList<string> omittedIds) =>
        [.. LoadedModsGate.Missing(activeIds, EffectiveRequiredIds(descriptor, omittedIds), suffixedIds)];

    public static IReadOnlyList<string> EffectiveRequiredIds(
        OrchestratorDescriptor descriptor, IReadOnlyList<string> omittedIds) =>
        [.. descriptor.RequiredPackageIds.Where(id => !omittedIds.Contains(id))];

    public static string[] ReadActiveModIds(string modsConfigPath) =>
        [.. RimTooling.RimWorld.RimWorldModsConfig.Read(CommandContext.FullPath(modsConfigPath)).ActiveModIds];
}

internal static class RunTextExtensions
{
    public static int SplitLineCount(this string text) =>
        text.Length == 0 ? 0 : text.Split(["\r\n", "\n"], StringSplitOptions.None).Length;
}

public sealed class ReportFlowException(string message) : Exception(message)
{
}
