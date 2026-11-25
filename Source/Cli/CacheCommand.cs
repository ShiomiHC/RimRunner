namespace RimRunnerCli;

public static class CacheCommand
{
    public static int Execute(string[] args, Func<string, int> usage)
    {
        var store = new ClaimStore();
        var cache = new GameCache();
        Func<ClaimRecord, GameCache.Holder?> holderOf = GameCache.LiveProbe(store.Root);
        return args switch
        {
            [] => Show(cache, store.All(), holderOf),
            ["clear"] => Clear(cache, store.All(), holderOf),
            _ => usage($"cache 未知参数：{string.Join(" ", args)}"),
        };
    }

    private static int Show(GameCache cache, List<ClaimRecord> claims, Func<ClaimRecord, GameCache.Holder?> holderOf)
    {
        List<GameCache.Holder> holders = cache.Holders(claims, holderOf);
        Console.WriteLine($"游戏缓存（{cache.Root}）：{Mb(cache.Size())}");
        Console.WriteLine(holders.Count == 0 ? "  使用中的实例：无" : $"  使用中的实例：{Named(holders)}");
        return 0;
    }

    private static int Clear(GameCache cache, List<ClaimRecord> claims, Func<ClaimRecord, GameCache.Holder?> holderOf)
    {
        (long? freed, List<GameCache.Holder> held) = cache.Clear(claims, holderOf);
        if (freed is long bytes)
        {
            Console.WriteLine($"已清空游戏缓存（{Mb(bytes)}，{cache.Root}）");
            return 0;
        }

        Console.WriteLine($"[拒绝] 游戏缓存正由实例 {Named(held)} 使用，未清空");
        return ExitCodes.Rejected;
    }

    private static string Named(List<GameCache.Holder> holders) =>
        string.Join("、", holders.Select(holder =>
            ClaimStore.Named(holder.Number) + (holder.GamePid is int pid ? $"（PID {pid}）" : "")));

    private static string Mb(long bytes) => $"{bytes / 1_000_000.0:0} MB";
}
