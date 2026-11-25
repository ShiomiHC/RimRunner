namespace RimRunnerCli;

internal static class StoppedClaims
{
    public static void Settle(ClaimStore store, Action<string> log) =>
        Settle(store, claim => GameCache.LiveProbe(store.Root)(claim) != null, log);

    internal static void Settle(ClaimStore store, Func<ClaimRecord, bool> isLive, Action<string> log,
        string? saveDataRoot = null)
    {
        List<ClaimRecord> claims;
        try
        {
            claims = store.All();
        }
        catch (StoreException exception)
        {
            log($"已停登记收尾未执行：{exception.Message}");
            return;
        }

        bool canReap = Leases.Observer == null;
        DateTimeOffset now = DateTimeOffset.Now;
        foreach (ClaimRecord claim in claims)
        {
            try
            {
                if (isLive(claim)) continue;
                RunCoordinator.ReleaseSynthDirs(store, claim, isLive, log);
                if (!canReap || !claim.Policy.ProcessScoped || RecentlyStopped(claim, now)) continue;
                ShardReap.TryReap(store, claim.Number, saveDataRoot ?? RecordBuilder.SaveDataRoot, log);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or StoreException or DescriptorException or InvalidOperationException)
            {
                log($"实例 {claim.DisplayName} 收尾未完成：{exception.Message}");
            }
        }
    }

    private static bool RecentlyStopped(ClaimRecord claim, DateTimeOffset now) =>
        claim.StoppedAt is { } stopped && now - stopped < ShardReap.StopDrainGrace;
}
