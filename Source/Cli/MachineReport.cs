namespace RimRunnerCli;

public static class MachineReport
{
    internal const int EstimatesShown = 3;

    public static int Print(string claimRoot, bool allEstimates)
    {
        PrintConfig();
        PrintCoordinator();
        PrintMachineLock(claimRoot);

        AdmissionLedger ledger = AdmissionLedger.ForClaimRoot(claimRoot);
        List<AdmissionLedger.Ticket> tickets = ledger.LiveTickets(out int unreadableTickets);
        List<AdmissionLedger.QueueEntry> queue = ledger.LiveQueue(out int unreadableQueue);
        AdmissionLedger.History history = ledger.ReadHistory();
        if (unreadableTickets + unreadableQueue > 0)
            Console.WriteLine($"[WARN] 准入账有 {unreadableTickets} 条在途记录、{unreadableQueue} 个排队行无法读取，下面按不存在计");
        if (history.Unreadable != null)
            Console.WriteLine($"[WARN] 准入历史无法读取（{history.Unreadable}），下面按无实测计");

        GpuMemory.Snapshot? snapshot = GpuMemory.TryRead(out string? reason);
        List<AdmissionGate.RimWorldInstance> instances = [];
        string? enumerateFault = null;
        AdmissionGate.Spill spill = AdmissionGate.Spill.None;
        if (snapshot == null)
        {
            Console.WriteLine($"显存：无法读取（{reason}），准入按不溢出处理");
        }
        else
        {
            instances = AdmissionGate.RimWorldInstances(snapshot, tickets, out enumerateFault);
            long headroom = AdmissionGate.Headroom();
            spill = AdmissionGate.SpillOf(0, snapshot, tickets, 0, headroom);
            long pending = instances.Sum(instance => instance.Remaining);
            Console.WriteLine($"显存（{snapshot.AdapterName}）：已用 {Gb(snapshot.AdapterUsed)} + 在途 {Gb(pending)}"
                              + $"，整卡 {Gb(snapshot.Total)} − 预留 {Gb(headroom)}{RimRunnerConfig.SourceTag(RimRunnerConfig.Machine().VramHeadroomMb.FromFile)}"
                              + $" = {Gb(spill.Cap)}，超出的部分计入内存");
            Console.WriteLine($"  已用里未归到 RimWorld 进程的 {Gb(snapshot.AdapterUsed - instances.Sum(i => i.Bytes))}"
                              + UnattributedNote(enumerateFault) + "；在途 = 已启动实例离预期峰值还差的量");
        }

        MemoryAccount.Snapshot? memory = MemoryAccount.Read();
        if (memory == null)
        {
            Console.WriteLine("物理内存：无法读取");
        }
        else
        {
            AdmissionGate.MemoryCheck check = AdmissionGate.CheckMemory(0, memory, tickets, 0, MemoryAccount.Headroom(), false, spill);
            Console.WriteLine($"物理内存：可用 {Gb(check.Decision.Available)}"
                              + $" = 总 {Gb(memory.Total)} − 已用 {Gb(memory.Total - memory.Available)}"
                              + $" − 在途 {Gb(check.Decision.PendingTotal)} − 预留 {Gb(check.Headroom)}"
                              + RimRunnerConfig.SourceTag(RimRunnerConfig.Machine().MemHeadroomMb.FromFile));
            Console.WriteLine($"  已用里未归到 RimWorld 进程的 {Gb(check.External)}" + UnattributedNote(memory.ResidentsFault)
                              + $"；在途含在途实例的显存溢出 {Gb(spill.Pending)}");
        }

        PrintInstances(tickets, instances, memory, enumerateFault ?? memory?.ResidentsFault
            ?? (snapshot == null && memory == null ? "显存与物理内存都无法读取" : null));

        if (queue.Count > 0)
        {
            Console.WriteLine("准入排队：");
            foreach (AdmissionLedger.QueueEntry entry in queue)
                Console.WriteLine($"  {entry.Label} · 实例 {ClaimStore.Named(entry.Number)}"
                                  + $" · {entry.EnqueuedAt.ToLocalTime():HH:mm:ss} 起"
                                  + (entry.Waiting == null ? "" : $" · {entry.Waiting}"));
        }

        PrintEstimates(history, allEstimates, memory?.Total, snapshot?.Total);
        Console.WriteLine($"账目：{ledger.Root}");
        return 0;
    }

    private static void PrintConfig()
    {
        RimRunnerConfig.MachineConfig config = RimRunnerConfig.Machine();
        Console.WriteLine($"机器配置：{RimRunnerConfig.MachinePath}"
                          + (config.FileExists ? "" : "（无此文件，全取缺省）"));
        foreach (string line in config.Effective())
            Console.WriteLine("  " + line);
    }

    private static void PrintMachineLock(string claimRoot)
    {
        List<string> lines = MachineLock.ForClaimRoot(claimRoot).HolderLines(DateTimeOffset.Now);
        Console.WriteLine($"{MachineLock.MachineTitle}（{CapLine()}）：" + (lines.Count == 0 ? "无持有者" : ""));
        foreach (string line in lines)
            Console.WriteLine("  " + line);
    }

    public static void PrintCoordinator()
    {
        if (IsolatedRoot.Active) return;
        MachineSnapshot? snapshot;
        try
        {
            snapshot = MachineSnapshot.ReadOrThrow();
        }
        catch (StoreException exception)
        {
            Console.WriteLine($"无法读取全机协调快照：{exception.Message}");
            return;
        }

        Console.WriteLine("全机协调：" + (snapshot == null ? "无（机器上没有编排进程）" : CoordinatorLine(snapshot.Coordinator)));
    }

    internal static string CoordinatorLine(System.Text.Json.JsonElement coordinator)
    {
        string build = coordinator.GetProperty("build").GetString()!;
        string line = $"RimRunner 安装目录 {coordinator.GetProperty("packageRoot").GetString()} · 构建 {build[..Math.Min(8, build.Length)]}";
        if (coordinator.TryGetProperty("builtAt", out System.Text.Json.JsonElement at)
            && DateTimeOffset.TryParse(at.GetString(), out DateTimeOffset builtAt))
            line += $"（{builtAt.ToLocalTime():MM-dd HH:mm} 编译）";
        if (coordinator.TryGetProperty("devTree", out System.Text.Json.JsonElement tree))
            line += $" · 开发构建（{tree.GetString()}）"
                    + (coordinator.TryGetProperty("taken", out System.Text.Json.JsonElement taken) && taken.GetBoolean()
                        ? "，显式接管"
                        : "");
        return line;
    }

    public static void PrintMachineLockSummary(string claimRoot)
    {
        List<MachineLock.Ticket> tickets = MachineLock.ForClaimRoot(claimRoot).LiveHolderTickets();
        if (tickets.Count == 0) return;
        int waiting = tickets.Count(t => t.Mode is MachineLock.AwaitShared or MachineLock.AwaitExclusive);
        Console.WriteLine($"{MachineLock.MachineTitle}（{CapLine()}）：持有 {tickets.Count - waiting}"
                          + (waiting > 0 ? $"，排队 {waiting}" : "") + "；明细见 rimrunner machine");
    }

    private static string CapLine()
    {
        try
        {
            int value = BusyCap.Resolve();
            double reserve = BusyReserve.Resolve();
            return (value == 0 ? "同时执行的实例不限" : $"同时执行的实例上限 {value}")
                   + (reserve == 0 ? " · 不按 P 核余量限制"
                       : CpuHeadroom.ProbeFailure() is string failure
                           ? value > 0
                               ? $" · P 核余量 {reserve:0.#}（本机无法读取：{failure}，按名额上限准入）"
                               : $" · P 核余量 {reserve:0.#}（本机无法读取：{failure}；未配置 busyCap 时拒绝执行）"
                           : $" · P 核余量 {reserve:0.#}");
        }
        catch (StoreException exception)
        {
            return exception.Message;
        }
    }

    private static string UnattributedNote(string? enumerateFault) =>
        enumerateFault == null ? "" : "（RimWorld 进程无法枚举，含它们的占用）";

    private static void PrintInstances(List<AdmissionLedger.Ticket> tickets,
        List<AdmissionGate.RimWorldInstance> instances, MemoryAccount.Snapshot? memory, string? enumerateFault)
    {
        List<int> pids = [.. instances.Select(i => i.Pid).Union(memory?.Residents.Select(r => r.Pid) ?? [])];
        Console.WriteLine(pids.Count > 0 ? "RimWorld 进程："
            : enumerateFault != null ? $"RimWorld 进程：无法枚举（{enumerateFault}）"
            : "RimWorld 进程：无");
        foreach (int pid in pids)
        {
            AdmissionGate.RimWorldInstance? vram = instances.FirstOrDefault(i => i.Pid == pid);
            MemoryAccount.Resident? resident = memory?.Residents.FirstOrDefault(r => r.Pid == pid);
            AdmissionLedger.Ticket? ticket = tickets.FirstOrDefault(t => t.GamePid == pid);
            string line = $"{ticket?.Label ?? AdmissionGate.InstanceLabel(vram?.CommandLine ?? resident?.CommandLine)} · PID {pid}";
            if (vram != null)
                line += $" · 显存 {Gb(vram.Bytes)}" + AdmissionGate.SharedNote(vram) + Expected(ticket?.Expected ?? 0, vram.Bytes);
            if (resident != null)
                line += $" · 私有 {Gb(resident.PrivateBytes)}" + Expected(ticket?.MemoryExpected ?? 0, resident.PrivateBytes);

            Console.WriteLine("  " + line);
        }
    }

    private static string Expected(long expected, long current) =>
        expected - current >= 50_000_000 ? $"，预期峰值 {Gb(expected)}" : "";

    private static void PrintEstimates(AdmissionLedger.History history, bool all, long? memoryTotal, long? vramTotal)
    {
        var rows = history.Peaks.Keys.Union(history.MemoryPeaks.Keys)
            .Select(key => (Key: key,
                Memory: AdmissionLedger.FromMemoryHistory(history, key)?.Bytes,
                Vram: AdmissionLedger.FromHistory(history, key)?.Bytes))
            .Where(row => row.Memory != null || row.Vram != null)
            .OrderByDescending(row => Math.Max(Share(row.Memory, memoryTotal) ?? 0, Share(row.Vram, vramTotal) ?? 0))
            .ThenBy(row => row.Key, StringComparer.Ordinal)
            .ToList();

        const string title = "新实例预期峰值";
        if (rows.Count == 0)
        {
            Console.WriteLine($"{title}：尚无实测");
            return;
        }

        string basis = $"（各组合最近 {AdmissionLedger.PeaksKept} 次记录的最大值）";
        int shown = all ? rows.Count : Math.Min(EstimatesShown, rows.Count);
        Console.WriteLine($"{title}{basis}：");
        foreach ((string key, long? memory, long? vram) in rows.Take(shown))
        {
            var parts = new List<string>();
            if (memory != null) parts.Add($"内存 {Gb(memory.Value)}{Percent(memory, memoryTotal)}");
            if (vram != null) parts.Add($"显存 {Gb(vram.Value)}{Percent(vram, vramTotal)}");
            Console.WriteLine($"  {key.Replace("|", " · ")}：{string.Join(" · ", parts)}");
        }
        if (shown < rows.Count)
            Console.WriteLine("  " + FindingPresentation.Omitted(rows.Count - shown, "条", FindingPresentation.WithFlag("--estimates")));
    }

    private static double? Share(long? bytes, long? total) =>
        bytes is long b && total is long t && t > 0 ? (double)b / t : null;

    private static string Percent(long? bytes, long? total) =>
        Share(bytes, total) is double share ? $"（{share:0%}）" : "";

    private static string Gb(long bytes) => AdmissionGate.Gb(bytes);
}
