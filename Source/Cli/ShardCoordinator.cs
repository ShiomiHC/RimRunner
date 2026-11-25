using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace RimRunnerCli;

internal sealed class ShardCoordinator(OrchestratorDescriptor descriptor, string productRoot, ClaimStore store, Action<string> log,
    BaselineSave? customSave = null)
{
    private static readonly TimeSpan EnumerateTimeout = TimeSpan.FromMinutes(2);

    internal const int KeepGroups = 20;
    internal const int KeepRedGroups = 10;

    private readonly OrchestratorDescriptor descriptor = descriptor;
    private readonly string productRoot = productRoot;
    private readonly ClaimStore store = store;
    private readonly Action<string> log = log;
    private readonly Dictionary<int, ColdStartLock> holds = [];
    private ShardGroupsLive.Entry? live;

    private string HoldLabel => $"{descriptor.Product}@{TreeScope.TreeName(productRoot)} · 分片组";

    public async Task<int> RunAsync(StructuredRunRequest request, string caseId, string? modList, bool forceBusy,
        int? timeoutMinutes, int queueMinutes, CancellationToken token)
    {
        try
        {
            return await RunGroupAsync(request, caseId, modList, forceBusy, timeoutMinutes, queueMinutes, token);
        }
        finally
        {
            if (live != null) ShardGroupsLive.Remove(live);
            ReleaseHolds();
        }
    }

    private void ReleaseHolds()
    {
        List<int> numbers = [.. holds.Keys];
        foreach (ColdStartLock hold in holds.Values) hold.Dispose();
        holds.Clear();
        foreach (int number in numbers)
        {
            if (store.Read(number) is { } claim && ShardReap.ProcessGone(claim)) Reap(number);
        }
    }

    private void Reap(int number)
    {
        try
        {
            ShardReap.TryReap(store, number, RecordBuilder.SaveDataRoot, log);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or StoreException or DescriptorException)
        {
            log($"[分片] 实例 {store.Name(number)} 的分片登记未卸载：{exception.Message}");
        }
    }

    private void Hold(ClaimRecord claim, ColdStartLock hold) => holds[claim.Number] = hold;

    private void Release(ClaimRecord claim)
    {
        if (holds.Remove(claim.Number, out ColdStartLock? hold)) hold.Dispose();
    }

    private async Task<int> RunGroupAsync(StructuredRunRequest request, string caseId, string? modList, bool forceBusy,
        int? timeoutMinutes, int queueMinutes, CancellationToken token)
    {
        Stopwatch wall = Stopwatch.StartNew();
        RimRunnerConfig.Machine();
        log($"[分片] 本批只有可分片用例 {caseId}，按分片组执行");

        WorktreeOutputLink.Ensure(descriptor, productRoot, log);

        if (forceBusy && !RunCoordinator.TakeOverGroups(store, productRoot, log)) return ExitCodes.Incomplete;

        string started = await new RunCoordinator(descriptor, productRoot, store, queueMinutes: queueMinutes)
            .PrepareShardGroupAsync(modList, timeoutMinutes, log, token);
        CliLog.Info(started.Length == 0 ? "[分片] 分片组在本树执行" : $"[分片] 分片组在本树执行 · {started}");

        List<ClaimRecord> available = Available();
        ClaimRecord? lent = Borrow();
        if (lent != null) available.Add(lent);
        var refusedBorrows = new List<ShardGroupManifest.UndispatchedEntry>();
        ClaimRecord first;
        ChildOutcome firstReady;
        while (true)
        {
            ClaimRecord candidate = available.Count > 0 ? TakeFirst(available) : Mount();
            ChildOutcome ready = await RunChildAsync(
                Spec(request, candidate.Number, modList, forceBusy, timeoutMinutes, queueMinutes,
                    borrowed: lent?.Number == candidate.Number), token);
            if (ready.Result.BorrowRefused is not string refusal)
            {
                first = candidate;
                firstReady = ready;
                break;
            }

            Release(candidate);
            refusedBorrows.Add(new ShardGroupManifest.UndispatchedEntry
                { Number = candidate.Number, Tag = ShardChildSpec.Tag(candidate.Number), Reason = CannotReuse, Unplanned = true });
            log($"[分片] 自动实例 {candidate.DisplayName} 空闲但无法复用（{refusal}），不借片");
        }

        PrintPrepared(firstReady);
        if (!firstReady.Ok) return ShardGroupView.FailedPrepareExitCode(firstReady.ExitCode);

        Func<double?>? cpuProbe = BusyReserve.Resolve() > 0 ? CpuHeadroom.NewProbe() : null;
        (Enumeration? enumeration, int enumerateExit) = await EnumerateAsync(first.Number, caseId, request, token);
        if (enumeration == null) return enumerateExit;
        double total = enumeration.Cells.Sum(cell => cell.Weight);
        MachineLock slotLock = MachineLock.ForClaimRoot(store.Root);
        int slots = slotLock.FreeSlots();
        int physical = BusyCap.PerformanceCores();
        int slotBudget = ShardPlan.SlotBudget(slots, slotLock.CapUnlimited, physical);
        string slotsText = slotLock.CapUnlimited
            ? $"不限（定片按 P 核数 {Math.Max(1, physical)} 封顶）"
            : slots.ToString();
        (int Fit, string Detail)? memory = MemoryFit(firstReady.Result);
        (int Fit, string Detail)? cpu = await CpuFitAsync(cpuProbe, token);
        ShardPlan.Count count = ShardPlan.Decide(slotBudget, enumeration.Cells.Count, total, enumeration.MinShardWeight,
            memory?.Fit, cpu?.Fit);
        log($"[分片] 片数 {count.N}，取自{count.BoundBy}（空闲名额 {slotsText} · "
            + (memory is { } fit ? $"内存可容 {fit.Fit} 片：{fit.Detail} · " : "")
            + (cpu is { } cores ? $"CPU 可容 {cores.Fit} 片：{cores.Detail} · " : "")
            + $"格数 {enumeration.Cells.Count} · "
            + (enumeration.MinShardWeight > 0
                ? $"总权重 {ShardPlan.FormatWeight(total)} ÷ 每片下限 {ShardPlan.FormatWeight(enumeration.MinShardWeight)}）"
                : $"总权重 {ShardPlan.FormatWeight(total)}，未设每片下限）"));

        (Signature? reference, string? referenceMissing) = SignatureOf(first);
        if (reference == null)
        {
            log($"[FAIL] 一致性闸：基准片 {ShardChildSpec.Tag(first.Number)}（实例 {first.DisplayName}）{referenceMissing}，本组未发片");
            return ExitCodes.Incomplete;
        }

        List<ShardPlan.Cell> queue = ShardPlan.Order(enumeration.Cells);
        Dictionary<string, double> weights = queue.ToDictionary(cell => cell.Key, cell => cell.Weight, StringComparer.Ordinal);
        string group = ShardPlan.NewGroupId(DateTimeOffset.Now);
        string groupDir = ShardLayout.GroupDir(descriptor, productRoot, group);
        string manifestPath = Path.Combine(groupDir, ShardLayout.ManifestFileName);
        string queuePath = ShardLayout.QueuePath(groupDir);
        Directory.CreateDirectory(groupDir);
        RimTooling.IO.AtomicFile.Write(queuePath,
            JsonSerializer.Serialize(queue.Select(cell => cell.Key).ToList(), ShardGroupManifest.Options));
        var sink = new ShardGroupSink(groupDir);
        var claims = new ShardGroupQueue([.. queue.Select(cell => cell.Weight)]);
        var manifest = new ShardGroupManifest
        {
            Schema = ShardGroupManifest.CurrentSchema,
            Group = group,
            CreatedAt = DateTimeOffset.Now,
            CaseId = caseId,
            ModList = modList,
            Fingerprint = enumeration.Fingerprint,
            MinShardWeight = enumeration.MinShardWeight,
            TotalWeight = total,
            QueueCells = queue.Count,
            Planned = count.N,
            Consistency = new ShardGroupManifest.ConsistencyEntry
            {
                AssemblyMvids = reference.Mvids,
                ContentFingerprint = reference.Fingerprint,
            },
        };
        manifest.Prepares.Add(PrepareEntry(first.Number, firstReady));
        manifest.Undispatched.AddRange(refusedBorrows);
        manifest.Save(manifestPath);
        live = ShardGroupsLive.Add(productRoot, manifest, claims);
        CliLog.Info($"[分片] 分片组 {group}：{queue.Count} 格");

        int dispatched = 0;
        async Task<ChildOutcome?> DispatchAsync(ClaimRecord claim)
        {
            ShardGroupManifest.ShardEntry entry;
            string tag = ShardChildSpec.Tag(claim.Number);
            lock (manifest)
            {
                if (claims.Drained)
                {
                    manifest.Undispatched.Add(new ShardGroupManifest.UndispatchedEntry
                        { Number = claim.Number, Tag = tag, Reason = DrainedReason(null) });
                    manifest.Save(manifestPath);
                    CliLog.Info($"[发片] 实例 {claim.DisplayName}：组队列的格已全被认领，不发片");
                    return null;
                }

                entry = new ShardGroupManifest.ShardEntry { Index = ++dispatched, Number = claim.Number, Tag = tag };
                manifest.Shards.Add(entry);
                manifest.Save(manifestPath);
            }

            ShardChildSpec spec = Spec(request, claim.Number, modList, forceBusy,
                timeoutMinutes, queueMinutes, borrowed: lent?.Number == claim.Number);
            spec.Group = group;
            spec.LockGroup = group;
            spec.Index = entry.Index;
            spec.Count = count.N;
            spec.QueuePath = queuePath;
            spec.Queue = claims;
            spec.Fingerprint = enumeration.Fingerprint;
            spec.AssemblyMvids = reference.Mvids;
            spec.ContentFingerprint = reference.Fingerprint;
            spec.Sink = sink;
            ChildOutcome outcome = await RunChildAsync(spec, token);
            if (outcome.Result.ReuseRefused is string refusal)
            {
                lock (manifest)
                {
                    manifest.Shards.Remove(entry);
                    manifest.Save(manifestPath);
                }

                log($"[FAIL] 一致性闸：实例 {claim.DisplayName} 未发片，实例无法复用：{refusal}");
                Undispatched(claim.Number, tag, "未过一致性闸", failed: true);
                return null;
            }

            if (outcome.Result.QueueDrained)
            {
                lock (manifest)
                {
                    manifest.Shards.Remove(entry);
                    manifest.Save(manifestPath);
                }

                CliLog.Info($"[发片] 实例 {claim.DisplayName}：组队列的格已全被认领，不发片");
                Undispatched(claim.Number, tag, DrainedReason(outcome.Result.DrainedGate), failed: false);
                return null;
            }

            lock (manifest)
            {
                entry.Cells = [.. claims.OwnedBy(entry.Index).Select(ordinal => queue[ordinal].Key)];
                entry.Weight = entry.Cells.Sum(key => weights[key]);
                entry.RunId = outcome.Result.RunId;
                entry.Report = outcome.Result.ReportPath is string report ? Path.GetRelativePath(groupDir, report) : null;
                entry.ExitCode = outcome.ExitCode;
                entry.Seconds = Math.Round(outcome.Elapsed.TotalSeconds, 1);
                manifest.Save(manifestPath);
                CliLog.Info($"[发片] 第 {entry.Index} 片 · 实例 {store.Name(entry.Number)} · runId {entry.RunId ?? "无"} · "
                    + $"认领 {entry.Cells.Count} 格 · 权重 {ShardPlan.FormatWeight(entry.Weight)} · "
                    + $"用时 {ReportPresentation.Elapsed((long)outcome.Elapsed.TotalMilliseconds)} · 退出码 {outcome.ExitCode}");
            }

            return outcome;
        }

        void Undispatched(int number, string tag, string reason, bool failed, int? exitCode = null)
        {
            lock (manifest)
            {
                manifest.Undispatched.Add(new ShardGroupManifest.UndispatchedEntry
                    { Number = number, Tag = tag, Reason = reason, Failed = failed, ExitCode = exitCode });
                manifest.Save(manifestPath);
            }
        }

        var mismatches = new System.Collections.Concurrent.ConcurrentQueue<EngineReportFinding>();

        async Task<ChildOutcome?> PrepareAndDispatchAsync(ClaimRecord claim)
        {
            string tag = ShardChildSpec.Tag(claim.Number);
            ShardChildSpec prepare = Spec(request, claim.Number, modList, forceBusy, timeoutMinutes, queueMinutes,
                borrowed: lent?.Number == claim.Number);
            prepare.LockGroup = group;
            prepare.QueuePath = queuePath;
            prepare.Queue = claims;
            prepare.ColdStartSeconds = firstReady.Result.ReadySeconds;
            ChildOutcome prepared = await RunChildAsync(prepare, token);
            lock (manifest)
            {
                manifest.Prepares.Add(PrepareEntry(claim.Number, prepared));
                manifest.Save(manifestPath);
            }

            if (prepared.Result.BorrowRefused is string refusal)
            {
                Release(claim);
                log($"[分片] 自动实例 {claim.DisplayName} 空闲但无法复用（{refusal}），不借片");
                Undispatched(claim.Number, tag, CannotReuse, failed: false);
                return null;
            }

            if (prepared.Result.Forecast is { Skip: true } forecast)
            {
                CliLog.Info($"[备片] 实例 {claim.DisplayName} · {ShardForecast.Describe(forecast)}，未启动实例 · "
                    + $"用时 {ReportPresentation.Elapsed((long)prepared.Elapsed.TotalMilliseconds)}");
                Undispatched(claim.Number, tag, "预计取空", failed: false);
                return null;
            }
            if (prepared.Result.QueueDrained == true)
            {
                CliLog.Info($"[备片] 实例 {claim.DisplayName} · 组队列的格已全被认领，未启动实例 · "
                    + $"用时 {ReportPresentation.Elapsed((long)prepared.Elapsed.TotalMilliseconds)}");
                Undispatched(claim.Number, tag, DrainedReason(prepared.Result.DrainedGate), failed: false);
                return null;
            }

            if (prepared.Result.QueueTimedOut)
            {
                log($"[备片] 实例 {claim.DisplayName} 排队到上限仍未准入，未启动实例 · "
                    + $"用时 {ReportPresentation.Elapsed((long)prepared.Elapsed.TotalMilliseconds)}");
                Undispatched(claim.Number, tag, "排队超时", failed: false);
                return null;
            }

            PrintPrepared(prepared);
            if (!prepared.Ok)
            {
                Undispatched(claim.Number, tag, "备片失败", failed: true, exitCode: prepared.ExitCode);
                return null;
            }

            (Signature? signature, string? missing) = SignatureOf(claim);
            if (signature == null)
            {
                log($"[FAIL] 一致性闸：实例 {claim.DisplayName} 未发片，{missing}");
                Undispatched(claim.Number, tag, "未过一致性闸", failed: true);
                return null;
            }

            List<string> differences = reference.Differences(signature);
            if (differences.Count > 0)
            {
                log($"[FAIL] 一致性闸：实例 {claim.DisplayName} 未发片，{differences.Count} 项与基准片不匹配");
                foreach (string difference in differences)
                    mismatches.Enqueue(new EngineReportFinding { Key = null, Subject = "实例 " + claim.DisplayName, Text = difference });
                Undispatched(claim.Number, tag, "未过一致性闸", failed: true);
                return null;
            }

            CliLog.Info($"[分片] 一致性闸：实例 {claim.DisplayName} 装载的程序集、内容指纹与基准片（第 1 片，实例 {first.DisplayName}）匹配");

            return await DispatchAsync(claim);
        }

        Task<ChildOutcome?> firstDispatch = DispatchAsync(first);
        List<ClaimRecord> others = [.. available.Where(claim => claim.Number != first.Number).Take(count.N - 1)];
        foreach (ClaimRecord spare in available.Where(claim => claim.Number != first.Number && !others.Contains(claim)))
            Release(spare);
        while (others.Count < count.N - 1) others.Add(Mount());
        ChildOutcome?[] outcomes = await Task.WhenAll([firstDispatch, .. others.Select(PrepareAndDispatchAsync)]);
        if (!mismatches.IsEmpty)
        {
            log($"[一致性闸] 与基准片 {ShardChildSpec.Tag(first.Number)}（实例 {first.DisplayName}）不匹配的项：");
            List<string> screen = [];
            List<string> logOnly = [];
            FindingPresentation.Facts(mismatches, "", "[一致性闸]", screen, logOnly, indent: "       ");
            screen.ForEach(log);
            logOnly.ForEach(CliLog.Info);
        }

        manifest.Unclaimed = Math.Max(0, queue.Count - claims.Claimed);
        List<ShardGroupQueue.Claim> claimed = claims.Claims();
        manifest.Claims = [.. claimed.Select(claim => new ShardGroupManifest.ClaimEntry
            { Shard = claim.Shard, ClaimedAt = claim.At.ToLocalTime(), DoneAt = claim.DoneAt?.ToLocalTime() })];
        if (manifest.Unclaimed == 0 && queue.Count > 0)
            manifest.DrainedAt = claimed.Max(claim => claim.At).ToLocalTime();
        manifest.Save(manifestPath);
        ShardForecast.LogCalibration(manifest.Prepares, claimed, manifest.Shards, manifest.DrainedAt, manifest.Unclaimed ?? 0);

        log($"[分片] 分片组 {group} · runId {string.Join(",", manifest.Shards.Select(shard => shard.RunId ?? "无"))}");
        string[] archived = [.. manifest.Shards.Where(entry => entry.Report != null).Select(entry => entry.Report!)];
        string reportsWhere = archived.Length == 0
            ? ""
            : " · 各片报告 " + string.Join("、", archived.GroupBy(path => Path.GetDirectoryName(path) ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(dir => dir.Key.Length == 0 ? $"{dir.Count()} 份" : $"{dir.Count()} 份在 {dir.Key}"));
        log($"       组目录 {groupDir}：组清单 {Path.GetFileName(manifestPath)}{reportsWhere}");
        if (archived.Length > 0)
            CliLog.Info("       各片报告：" + string.Join("、", archived));

        log("       本组报告未写入本树 reports/latest.json，也不在 rimrunner reports 列出的归档里");

        List<string> ledgerFaults = sink.Fold();
        if (ledgerFaults.Count > 0)
        {
            manifest.LedgerFaults = ledgerFaults;
            manifest.Save(manifestPath);
        }

        PruneGroups(descriptor, productRoot);

        manifest.Seconds = Math.Round(wall.Elapsed.TotalSeconds, 1);
        manifest.Save(manifestPath);
        (IReadOnlyList<ReportLine> lines, int exitCode) = ShardGroupView.Format(
            ShardGroupView.SlicesOf(manifest, shard => outcomes.FirstOrDefault(outcome => outcome?.Number == shard.Number)?.Result.Validation),
            ShardGroupView.FactsOf(manifest));
        ReportPrinter.Write(lines, log);
        return exitCode;
    }

    private static ShardGroupManifest.PrepareEntry PrepareEntry(int number, ChildOutcome outcome) => new()
    {
        Number = number,
        DecidedAt = outcome.Result.DecidedAt,
        Reused = outcome.Result.Reused,
        Forecast = outcome.Result.Forecast,
        ReadySeconds = outcome.Result.ReadySeconds,
        Seconds = Math.Round(outcome.Elapsed.TotalSeconds, 1),
    };

    private ShardChildSpec Spec(StructuredRunRequest request, int number, string? modList, bool forceBusy,
        int? timeoutMinutes, int queueMinutes, bool borrowed = false) =>
        ShardChildSpec.From(productRoot, request, number, modList, forceBusy, timeoutMinutes, queueMinutes, borrowed,
            customSave?.SourcePath);

    internal static string DrainedReason(string? gate) =>
        gate == null ? "组队列已空" : MachineLock.WhileQueued(gate) + "组队列已空";

    private const string CannotReuse = "无法复用";

    private static ClaimRecord TakeFirst(List<ClaimRecord> idle)
    {
        ClaimRecord first = idle[0];
        idle.RemoveAt(0);
        return first;
    }

    private (int Fit, string Detail)? MemoryFit(ShardChildResult first)
    {
        if (first.MemoryNeed <= 0 || MemoryAccount.Read() is not { } memory) return null;
        List<AdmissionLedger.Ticket> tickets;
        try
        {
            AdmissionLedger ledger = AdmissionLedger.ForClaimRoot(store.Root);
            using (ledger.Lock(TimeSpan.FromSeconds(60))) tickets = ledger.LiveTickets();
        }
        catch (StoreException exception)
        {
            CliLog.Info($"[分片] 无法读取准入账，不按内存定片数：{exception.Message}");
            return null;
        }

        HashSet<int> mine = [.. holds.Keys];
        List<MemoryAccount.Resident> freed = [.. memory.Residents.Where(resident =>
            GameProcessProbe.ClaimedNumber(resident.CommandLine, RimRunner.RimRunnerEngineArgs.Instance) is int number
            && (mine.Contains(number)
                || (!ColdStartLock.OwnersOf(store.Root, number).Any && IdleEviction.Eligible(InstanceProbe.Ping(number)))))];
        HashSet<int> freedPids = [.. freed.Select(resident => resident.Pid)];
        List<AdmissionLedger.Ticket> outside = [.. tickets.Where(ticket => !mine.Contains(ticket.Number))];
        long pending = outside.Where(ticket => ticket.MemoryExpected > 0)
            .Sum(ticket => Math.Max(0, ticket.MemoryExpected - (ticket.GamePid is int pid ? memory.PrivateOf(pid) : 0)));
        long budget = memory.Available - MemoryAccount.Headroom() + freed.Sum(resident => resident.PrivateBytes) - pending;

        long vramNeed = 0, vramOutside = 0, vramCap = 0;
        if (first.VramNeed > 0 && GpuMemory.TryRead(out _) is { } gpu)
        {
            vramNeed = first.VramNeed;
            vramCap = Math.Max(0, gpu.Total - AdmissionGate.Headroom());
            vramOutside = gpu.AdapterUsed - freedPids.Sum(pid => gpu.ProcessUsed.GetValueOrDefault(pid))
                          + outside.Sum(ticket => Math.Max(0,
                              ticket.Expected - (ticket.GamePid is int pid ? gpu.ProcessUsed.GetValueOrDefault(pid) : 0)));
        }

        int fit = ShardPlan.MemoryFit(first.MemoryNeed, budget, vramNeed, vramOutside, vramCap);
        long spill = Math.Max(0, vramOutside + vramNeed - vramCap) - Math.Max(0, vramOutside - vramCap);
        return (fit, $"每片 {AdmissionGate.Gb(first.MemoryNeed)}"
            + (spill > 0 ? $" + 显存溢出至少 {AdmissionGate.Gb(spill)}" : "")
            + $"，可给片用 {AdmissionGate.Gb(Math.Max(0, budget))}");
    }

    private List<ClaimRecord> Available()
    {
        var idle = new List<ClaimRecord>();
        foreach (ClaimRecord claim in store.FindByProductRoot(productRoot, ClaimRecord.PurposeShard).OrderBy(claim => claim.Number))
        {
            if (ShardReap.ProcessGone(claim))
            {
                Reap(claim.Number);
                continue;
            }

            if (ShardHold.TryAcquire(store.Root, claim.Number, productRoot, HoldLabel) is not { } hold) continue;
            if (!StillSelectable(claim))
            {
                hold.Dispose();
                continue;
            }
            if (InstanceProbe.Hold(claim.Number) == null && InstanceProbe.RunState(claim.Number) == "idle")
            {
                Hold(claim, hold);
                idle.Add(claim);
            }
            else
            {
                hold.Dispose();
            }
        }

        return idle;
    }

    internal ClaimRecord? Borrow()
    {
        foreach (ClaimRecord claim in store.FindByProductRoot(productRoot).Where(record => record.Policy.BorrowableForShard))
        {
            if (ShardHold.TryAcquire(store.Root, claim.Number, productRoot, HoldLabel) is not { } hold) continue;
            if (!StillSelectable(claim))
            {
                hold.Dispose();
                continue;
            }
            if (InstanceProbe.Hold(claim.Number) == null && InstanceProbe.RunState(claim.Number) == "idle")
            {
                Hold(claim, hold);
                return claim;
            }

            hold.Dispose();
        }

        return null;
    }

    private bool StillSelectable(ClaimRecord claim)
    {
        try
        {
            return ColdStartLock.AliveHolder(store.Root, claim.Number) == null
                   && store.Read(claim.Number) is { } fresh
                   && PathIdentity.Same(fresh.ProductRoot, productRoot);
        }
        catch (StoreException)
        {
            return false;
        }
    }

    private ClaimRecord Mount()
    {
        var lines = new List<string>();
        ClaimRecord record;
        try
        {
            record = new MountFlow(descriptor, productRoot, store, RecordBuilder.SaveDataRoot,
                    CommandContext.GetEnvironmentVariable("RIMRUNNER_LOCALAPPDATA"))
                .Mount(lines.Add, ClaimRecord.PurposeShard);
        }
        finally
        {
            foreach (string line in lines) CliLog.Info(line);
        }
        Hold(record, ShardHold.TryAcquire(store.Root, record.Number, productRoot, HoldLabel)
                     ?? throw new StoreException($"实例 {record.DisplayName} 刚挂载的分片登记被另一条命令持有"));
        CliLog.Info($"[分片] 已挂载分片实例：{record.DisplayName}");
        return record;
    }

    private void PrintPrepared(ChildOutcome outcome)
    {
        string elapsed = ReportPresentation.Elapsed((long)outcome.Elapsed.TotalMilliseconds);
        if (outcome.Ok)
        {
            string forecast = outcome.Result.Forecast is { } record && ShardForecast.Describe(record) is string described
                ? " · " + described
                : "";
            CliLog.Info($"[备片] 实例 {store.Name(outcome.Number)} · {(outcome.Result.Reused == true ? "复用" : "冷启动")} · 用时 {elapsed}{forecast}");
            return;
        }

        log($"[FAIL] 备片失败：实例 {store.Name(outcome.Number)} · 退出码 {outcome.ExitCode} · 用时 {elapsed} · "
            + LinesOf(outcome.Number));
    }

    private static async Task<(int Fit, string Detail)?> CpuFitAsync(Func<double?>? probe, CancellationToken token)
    {
        if (probe == null) return null;
        double? idle = probe();
        var clock = Stopwatch.StartNew();
        while (idle == null && clock.Elapsed < CpuHeadroom.Window + CpuWindowSlack)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            idle = probe();
        }

        if (idle is not double free)
        {
            CliLog.Info("[分片] 无法读取 P 核空闲，不按 CPU 定片数");
            return null;
        }

        double reserve = BusyReserve.Resolve();
        double need = BusyReserve.InstanceCores;
        return (ShardPlan.CpuFit(free, reserve, need),
            $"P 核空闲 {free:0.#} 核 − 保留 {reserve:0.#} 核，每片 {need:0.#} 核");
    }

    private static readonly TimeSpan CpuWindowSlack = TimeSpan.FromSeconds(3);

    private static string LinesOf(int number) => ShardGroupView.LinesOf(ShardChildSpec.Tag(number));

    internal static void PruneGroups(OrchestratorDescriptor descriptor, string productRoot)
    {
        string groupsRoot = ShardLayout.GroupsRoot(descriptor, productRoot);
        if (!Directory.Exists(groupsRoot)) return;
        var groups = new List<(DirectoryInfo Dir, ShardGroupManifest? Manifest)>();
        foreach (DirectoryInfo dir in new DirectoryInfo(groupsRoot).GetDirectories()
                     .OrderByDescending(dir => dir.Name, StringComparer.Ordinal))
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string manifestPath = Path.Combine(dir.FullName, ShardLayout.ManifestFileName);
            ShardGroupManifest? manifest = null;
            try
            {
                if (File.Exists(manifestPath)) manifest = ShardGroupManifest.Load(manifestPath);
            }
            catch (StoreException)
            {
            }

            groups.Add((dir, manifest));
        }

        var keep = new HashSet<string>(groups.Take(KeepGroups).Select(group => group.Dir.FullName),
            StringComparer.OrdinalIgnoreCase);
        keep.UnionWith(groups.Where(group => group.Manifest?.Red == true).Take(KeepRedGroups)
            .Select(group => group.Dir.FullName));
        foreach ((DirectoryInfo dir, ShardGroupManifest? manifest) in groups)
        {
            if (keep.Contains(dir.FullName) || manifest == null) continue;
            dir.Delete(recursive: true);
        }
    }

    private sealed record Enumeration(List<ShardPlan.Cell> Cells, double MinShardWeight, string Fingerprint);

    private async Task<(Enumeration?, int)> EnumerateAsync(int number, string caseId, StructuredRunRequest request,
        CancellationToken token)
    {
        InstanceLink.Reply reply = await InstanceLinks.RequestAsync(number, RimRunner.RimRunnerPipeProtocol.Ops.ShardCells,
            writer =>
            {
                writer.WriteString(RimRunner.RimRunnerLiveContract.ShardCells.Case, caseId);
                request.WriteInputs(writer);
            },
            EnumerateTimeout, token, request.OversizeNote());
        if (reply.Status <= InstanceLink.Reply.NoLinkStatus)
        {
            log($"[FAIL] 实例 {store.Name(number)} 枚举 {caseId} 的格无应答：{reply.ErrorText()}");
            return (null, ExitCodes.Incomplete);
        }

        if (!reply.Ok)
        {
            if (reply.Status == RimRunner.RimRunnerLiveContract.ShardCells.UnprocessableStatus)
            {
                log($"[结果] REJECTED —— {reply.ErrorText()}");
                return (null, ExitCodes.Rejected);
            }

            log($"[FAIL] 实例 {store.Name(number)} 枚举 {caseId} 的格失败：{reply.ErrorText()}");
            return (null, ExitCodes.Incomplete);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(reply.Body!);
            JsonElement root = document.RootElement;

            var cells = new List<ShardPlan.Cell>();
            foreach (JsonElement item in root.GetProperty(RimRunner.RimRunnerLiveContract.ShardCells.Cells).EnumerateArray())
            {
                cells.Add(new ShardPlan.Cell(
                    item.GetProperty(RimRunner.RimRunnerLiveContract.ShardCells.Key).GetString()!,
                    item.GetProperty(RimRunner.RimRunnerLiveContract.ShardCells.Weight).GetDouble()));
            }

            if (root.TryGetProperty(RimRunner.RimRunnerLiveContract.ShardCells.Warnings, out JsonElement warnings))
            {
                List<string> lines = [];
                FindingPresentation.FactsInFull(warnings.EnumerateArray().Select(item => new EngineReportFinding
                {
                    Key = item.GetProperty(RimRunner.RimRunnerLiveContract.ShardCells.WarningKey).GetString(),
                    Subject = null,
                    Text = item.GetProperty(RimRunner.RimRunnerLiveContract.ShardCells.WarningKey).GetString() + "："
                           + item.GetProperty(RimRunner.RimRunnerLiveContract.ShardCells.WarningText).GetString(),
                }), lines, $"[WARN] 枚举 {caseId}：");
                lines.ForEach(log);
            }

            if (cells.Count == 0)
            {
                log($"[结果] REJECTED —— 枚举 {caseId} 得到零格");
                return (null, ExitCodes.Rejected);
            }

            return (new Enumeration(cells,
                root.GetProperty(RimRunner.RimRunnerLiveContract.ShardCells.MinShardWeight).GetDouble(),
                root.GetProperty(RimRunner.RimRunnerLiveContract.ShardCells.Fingerprint).GetString()!), 0);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
                                              or InvalidOperationException)
        {
            log($"[FAIL] 实例 {store.Name(number)} 枚举 {caseId} 的格，回包无法解析：{exception.Message}");
            return (null, ExitCodes.Incomplete);
        }
    }

    private sealed record Signature(Dictionary<string, string> Mvids, string Fingerprint)
    {
        public List<string> Differences(Signature other)
        {
            var lines = new List<string>();
            if (Fingerprint != other.Fingerprint)
                lines.Add($"内容指纹：基准片 {Fingerprint} · 本片 {other.Fingerprint}");
            foreach (string name in Mvids.Keys.Union(other.Mvids.Keys).Order(StringComparer.Ordinal))
            {
                string? mine = Mvids.GetValueOrDefault(name);
                string? theirs = other.Mvids.GetValueOrDefault(name);
                if (mine != theirs)
                    lines.Add($"{name} 程序集 MVID：基准片 {mine ?? "未装载"} · 本片 {theirs ?? "未装载"}");
            }

            return lines;
        }
    }

    private (Signature? Signature, string? Missing) SignatureOf(ClaimRecord claim)
    {
        if (InstanceLinks.Get(claim.Number) is not { } link) return (null, "不在本编排进程名下");
        if (InstanceProbe.Snapshot(link, touch: true) is not { } ping) return (null, "无应答");
        if (ping.AssemblyMvids is not { } mvids) return (null, "未返回程序集 MVID");
        if (link.Runtime.ContentFingerprint is not { } fingerprint) return (null, "未记录内容指纹");
        return (new Signature(new Dictionary<string, string>(mvids), fingerprint), null);
    }

    private sealed record ChildOutcome(int Number, int ExitCode, ShardChildResult Result, TimeSpan Elapsed)
    {
        public bool Ok => ExitCode == 0;
    }

    private static async Task<ChildOutcome> RunChildAsync(ShardChildSpec spec, CancellationToken token)
    {
        spec.GroupLog ??= CliLog.Current;
        ConsoleRouter.EnsureInstalled();
        var context = new CommandContext(CommandContext.NextId(), spec.ProductRoot,
            CommandContext.EnvironmentSnapshot(), TextWriter.Null, token, $"分片组的片，实例 {ClaimStore.Named(spec.Number)}（{spec.ProductRoot}）");
        CommandContext? parent = CommandContext.Current;
        parent?.Adopt(context);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Stopwatch watch = Stopwatch.StartNew();
        var thread = new Thread(() =>
        {
            try
            {
                using (CommandContext.Enter(context)) Program.RunShardChildCommand(spec);
            }
            finally
            {
                Leases.ReleaseCommand(context.Id);
                parent?.Disown(context);
                done.SetResult();
            }
        })
        {
            IsBackground = true,
            Name = $"rimrunner-shard-{spec.Number}",
        };
        thread.Start();
        await done.Task.ConfigureAwait(false);
        return new ChildOutcome(spec.Number, spec.Result.ExitCode, spec.Result, watch.Elapsed);
    }
}
