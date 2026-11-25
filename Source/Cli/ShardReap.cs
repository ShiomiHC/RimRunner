using System.Diagnostics;

namespace RimRunnerCli;

internal static class ShardReap
{
    internal static readonly TimeSpan FreshMountGrace = TimeSpan.FromMinutes(10);

    internal static readonly TimeSpan StopDrainGrace = TimeSpan.FromMinutes(1);

    public static void Watch(int number, int gamePid, Task stopRecorded, string? claimRoot = null,
        string? saveDataRoot = null) =>
        WatchForExit(number, gamePid, stopRecorded, claimRoot, saveDataRoot);

    public static void WatchSettled(int number, int gamePid, Task settled, string? claimRoot = null,
        string? saveDataRoot = null) =>
        WatchForExit(number, gamePid, settled, claimRoot, saveDataRoot);

    private static void WatchForExit(int number, int gamePid, Task finished, string? claimRoot, string? saveDataRoot)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(gamePid);
        }
        catch (ArgumentException)
        {
        }

        ExecutionContext.SuppressFlow();
        try
        {
            _ = Task.Run(async () =>
            {
                if (process != null)
                {
                    using (process) await process.WaitForExitAsync().ConfigureAwait(false);
                }

                await finished.ConfigureAwait(false);
                Reaped(number, gamePid, claimRoot, saveDataRoot);            });
        }
        finally
        {
            ExecutionContext.RestoreFlow();
        }
    }

    private static void Reaped(int number, int pid, string? claimRoot, string? saveDataRoot)
    {
        var store = new ClaimStore(claimRoot);
        var lines = new List<string>();
        bool reaped;
        try
        {
            reaped = TryReap(store, number, saveDataRoot ?? RecordBuilder.SaveDataRoot, lines.Add,
                processObservedGone: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or StoreException or DescriptorException or InvalidOperationException)
        {
            lines.Add(exception.Message);
            reaped = false;
        }

        string detail = reaped ? "已卸载"
            : lines.Count > 0 ? "卸载失败：" + string.Join(" / ", lines)
            : store.Has(number) ? "让路：号被持有、冷启动中或进程仍在"
            : "登记已不在";
        ActivityLog.Touch(ActivityLog.Reap, number, pid, detail);
    }

    internal static bool ProcessGone(ClaimRecord claim, IReadOnlyList<GameProcessProbe.GameProcess>? processes = null)
    {
        return InstanceProbe.Facts(claim.Number, processes) is not { } facts
               || !ProcessIdentity.GameAlive(facts.Pid, facts.StartedAt);
    }

    public static bool TryReap(ClaimStore store, int number, string saveDataRoot, Action<string> log,
        bool processObservedGone = false)
    {
        if (store.Read(number) is not { Policy.ProcessScoped: true } first) return false;
        PurposePolicy policy = first.Policy;
        string lockLabel = policy.ReapLockLabel
                           ?? throw new InvalidOperationException($"随进程用途 {policy.WireName} 缺少 ReapLockLabel");
        using ColdStartLock? hold = policy.ReapRespectsShardHold
            ? ShardHold.TryAcquire(store.Root, number, first.ProductRoot, lockLabel)
            : null;
        if (policy.ReapRespectsShardHold && hold == null) return false;
        using ColdStartLock? starting = ColdStartLock.TryAcquire(store.Root, number, first.ProductRoot,
            DateTimeOffset.Now, lockLabel).Lock;
        if (starting == null) return false;

        if (store.Read(number) is not { Policy.ProcessScoped: true } claim) return false;
        if (claim.PurposeKind != first.PurposeKind) return false;
        if (GameProcessProbe.Enumerate().Any(process => InstanceProbe.IsOwnedAutoTestProcess(process, number)))
            return false;
        if (!processObservedGone && claim.StoppedAt == null
            && DateTime.UtcNow - (store.WrittenAtUtc(number) ?? DateTime.UtcNow) < FreshMountGrace)
            return false;

        string tag = claim.Policy.ReapTag ?? $"[{claim.EffectivePurpose}]";
        string recordNoun = RecordNoun(claim);
        if (UnmountFlow.SourceOf(claim) is not { } source)
        {
            if (!UnmountFlow.DescriptorOptional(claim))
            {
                log($"{tag} 实例 {claim.DisplayName} 的游戏进程已退出，未找到同产品的编排描述符，{recordNoun}保留：{claim.ProductRoot}");
                return false;
            }

            log($"{tag} 实例 {claim.DisplayName} 的产品根已不在盘上，按已删的产品根卸载登记");
            return UnmountFlow.For(null, store, saveDataRoot).Unmount(claim, forceBusy: false, log);
        }

        log($"{tag} 实例 {claim.DisplayName} 的游戏进程已退出，卸载{recordNoun}");
        return UnmountFlow.For(source, store, saveDataRoot).Unmount(claim, forceBusy: false, log);
    }

    internal static string RecordNoun(ClaimRecord claim) =>
        claim.Policy.ReapRecordNoun ?? claim.Noun;
}

internal static class ShardHold
{
    private const string TablePrefix = "shard-hold:";

    internal static string TableFor(string claimRoot) => TablePrefix + PathIdentity.Normalize(claimRoot).ToUpperInvariant();

    public static ColdStartLock? TryAcquire(string claimRoot, int number, string productRoot, string label) =>
        ColdStartLock.TryAcquireIn(TableFor(claimRoot), number, productRoot, DateTimeOffset.Now, label).Lock;

    public static ColdStartLock.Holder? AliveHolder(string claimRoot, int number) =>
        ColdStartLock.HolderIn(TableFor(claimRoot), number);

    internal static List<int>? TakeOver(string claimRoot, IEnumerable<ClaimRecord> claims, Action<string>? log)
    {
        List<(ClaimRecord Claim, ColdStartLock.Holder Holder)> held =
            [.. claims.Select(claim => (claim, ColdStartLock.OwnersOf(claimRoot, claim.Number).Group))
                .Where(entry => entry.Group != null).Select(entry => (entry.claim, entry.Group!))];
        var revoked = new HashSet<Coordinator.Owner>();
        var taken = new List<int>();
        foreach ((ClaimRecord claim, ColdStartLock.Holder holder) in held)
        {
            bool revoke = revoked.Add(holder.Owner);
            if (revoke)
                log?.Invoke($"[分片持有] 撤销持有实例 {claim.DisplayName} 的分片组：{holder.Label ?? holder.ProductRoot}（--force）");
            if (!ColdStartLock.RevokeAndWait(TableFor(claimRoot), claim.DisplayName, claim.Number, holder, revoke,
                    "持有该实例的分片组", "持有", log))
                return null;
            taken.Add(claim.Number);
        }

        return taken;
    }
}
