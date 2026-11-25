namespace RimRunnerCli;

public sealed class DllFreshnessResult
{
    public string? FlowError { get; init; }

    public IReadOnlyList<string> Stale { get; init; } = [];

    public IReadOnlyList<string> Skipped { get; init; } = [];

    public int ComparedCount { get; init; }

    public bool Fresh => FlowError == null && Stale.Count == 0;
}

public static class DllFreshnessGate
{
    public static DllFreshnessResult Check(
        IReadOnlyDictionary<string, string>? loadedMvids,
        IReadOnlyDictionary<string, string> declared,
        string productRoot)
    {
        if (loadedMvids == null)
            return Failure("[engine] 回包缺少 assemblyMvids");
        if (loadedMvids.Count == 0)
            return Failure("实例未加载本产品与引擎的程序集");
        if (declared.Count == 0)
            return Failure("描述符没有产物程序集（builds 与 testMod），无法检测陈旧。实例当前加载的本产品与引擎程序集："
                             + $"{string.Join("、", loadedMvids.Keys)}");

        var stale = new List<string>();
        int compared = 0;
        foreach (KeyValuePair<string, string> loaded in loadedMvids)
        {
            if (!declared.TryGetValue(loaded.Key, out string? relative))
                return Failure($"rimrunner.toml 的 builds 与 testMod 里没有本产品的程序集 {loaded.Key}");

            string path = Path.Combine(productRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                return Failure($"{loaded.Key} 的盘上文件不存在：{path}");

            string? diskMvid = TryReadMvid(path, out string? readError);
            if (diskMvid == null)
                return Failure($"无法读取 {loaded.Key} 的磁盘 MVID：{path}；{readError}");

            compared++;
            if (!string.Equals(diskMvid, loaded.Value, StringComparison.OrdinalIgnoreCase))
                stale.Add(Path.GetFileName(path));
        }

        return new DllFreshnessResult
        {
            Stale = stale,
            Skipped =
            [
                .. declared.Keys.Where(name => !loadedMvids.ContainsKey(name))
                    .OrderBy(name => name, StringComparer.Ordinal)
            ],
            ComparedCount = compared,
        };
    }

    public static string? TryReadMvid(string dllPath, out string? error)
    {
        if (RimTooling.IO.PeImage.TryReadIdentity(CommandContext.FullPath(dllPath), out RimTooling.IO.PeIdentity identity))
        {
            error = null;
            return identity.Mvid.ToString("D");
        }

        error = "无法读取 PE 元数据";
        return null;
    }

    private static DllFreshnessResult Failure(string message) => new() { FlowError = message };
}
