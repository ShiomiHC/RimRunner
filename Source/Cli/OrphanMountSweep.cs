namespace RimRunnerCli;

internal static class OrphanMountSweep
{
    public static void Run(string searchDir, ClaimStore store, string saveDataRoot, Action<string> log)
    {
        foreach (ClaimRecord claim in store.All()
                     .Where(record => record.OnWorktree && !record.Policy.ProcessScoped)
                     .OrderBy(record => record.Number))
        {
            try
            {
                SweepClaim(claim, store, saveDataRoot, log);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or StoreException or DescriptorException)
            {
                log($"[清扫] 实例 {claim.DisplayName} 未卸载：{exception.Message}");
            }
        }

        SweepProcessScoped(store, saveDataRoot, log);

        try
        {
            SweepCells(searchDir, store, log);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or DescriptorException)
        {
            log($"[清扫] 产出格未清理完成：{exception.Message}");
        }
    }

    private static void SweepProcessScoped(ClaimStore store, string saveDataRoot, Action<string> log)
    {
        List<GameProcessProbe.GameProcess> processes = GameProcessProbe.Enumerate();
        foreach (ClaimRecord claim in store.All()
                     .Where(record => record.Policy.ProcessScoped && ShardReap.ProcessGone(record, processes))
                     .OrderBy(record => record.Number))
        {
            var lines = new List<string>();
            try
            {
                if (ShardReap.TryReap(store, claim.Number, saveDataRoot, lines.Add))
                    lines.ForEach(CliLog.Info);
                else
                    lines.ForEach(log);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or StoreException or DescriptorException or InvalidOperationException)
            {
                lines.ForEach(log);
                log($"[清扫] 实例 {claim.DisplayName} 的{ShardReap.RecordNoun(claim)}未卸载：{exception.Message}");
            }
        }
    }

    private static void SweepClaim(ClaimRecord claim, ClaimStore store, string saveDataRoot, Action<string> log)
    {
        if (!UnmountFlow.ProductRootGone(claim.ProductRoot))
            return;

        using IDisposable? gate = TryGate(store, claim.Number);
        if (gate == null || store.Read(claim.Number) == null)
            return;

        (OrchestratorDescriptor Descriptor, string Root)? main = UnmountFlow.SourceOf(claim);
        if (main == null)
        {
            log($"[清扫] 实例 {claim.DisplayName} 的 worktree 已不在盘上，未找到同产品的主树，未自动卸载");
            return;
        }

        CliLog.Info($"[清扫] 实例 {claim.DisplayName} 的 worktree 已不在盘上，自动卸载：{claim.ProductRoot}");
        RunCoordinator.StopOutcome stopped = RunCoordinator.EnsureStopped(store.Root, [claim], forceBusy: false, log);
        bool done = stopped.Verdict == null
                    && UnmountFlow.For(main, store, saveDataRoot).Unmount(claim, forceBusy: false, log);
        if (!done)
            log($"[清扫] 实例 {claim.DisplayName} 未卸载，登记保留");
    }

    private static void SweepCells(string searchDir, ClaimStore store, Action<string> log)
    {
        string? productRoot = OrchestratorDescriptor.FindProductRoot(searchDir);
        if (productRoot == null)
            return;
        string mainProductRoot = TreeScope.Of(productRoot).MainOrSelfProductRoot;
        OrchestratorDescriptor descriptor = OrchestratorDescriptor.Load(mainProductRoot).Descriptor;
        string cells = Path.Combine(descriptor.ResolveOutputDir(mainProductRoot), WorktreeOutputLink.TreesDirName);
        if (!Directory.Exists(cells))
            return;

        List<ClaimRecord> claims = store.All();
        foreach (string cell in Directory.GetDirectories(cells))
        {
            if (WorktreeOutputLink.MarkedProductRoot(cell) is not string cellProductRoot)
                continue;
            if (!WorktreeOutputLink.TreeGone(cellProductRoot)
                || claims.Any(claim => PathIdentity.Same(claim.ProductRoot, cellProductRoot)))
                continue;
            string? drive = Path.GetPathRoot(CommandContext.FullPath(cellProductRoot));
            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive))
                continue;

            WorktreeOutputLink.RemoveShell(descriptor, cellProductRoot);
            Directory.Delete(cell, recursive: true);
            CliLog.Info($"[清扫] 已删除 worktree 的产出格：{cell}");
        }
    }

    private static IDisposable? TryGate(ClaimStore store, int number)
    {
        try
        {
            return Leases.Lock(store.SweepSection(number), TimeSpan.Zero, "删树补卸锁");
        }
        catch (StoreException)
        {
            return null;
        }
    }
}
