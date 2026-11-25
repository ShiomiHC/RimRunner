namespace RimRunnerCli;

internal static class EngineArtifact
{
    internal const string AssemblyName = "RimRunner";
    internal const string FileName = AssemblyName + ".dll";
    internal const string SelfTestAssemblyName = "RimRunner.SelfTest";
    internal const string SelfTestFileName = SelfTestAssemblyName + ".dll";

    internal static bool? LoadedIsPackageCopy(IReadOnlyDictionary<string, string>? loaded, EnginePackage package)
    {
        if (loaded == null || !loaded.TryGetValue(AssemblyName, out string? instanceMvid)) return null;
        string? diskMvid = DllFreshnessGate.TryReadMvid(package.DllPath, out _);
        return diskMvid == null ? null : string.Equals(diskMvid, instanceMvid, StringComparison.OrdinalIgnoreCase);
    }

    internal static string PackageCopyNote(bool same) =>
        same ? "实例进程里的引擎与 RimRunner 安装目录里的引擎是同一份" : "实例进程里的引擎与 RimRunner 安装目录里的引擎不是同一份";

    internal static void AssertReferencesEngine(string testDll)
    {
        if (AssemblyReferences(testDll).Contains(AssemblyName, StringComparer.OrdinalIgnoreCase))
            return;

        throw new StoreException(
            $"{Path.GetFileName(testDll)} 不引用 {AssemblyName}");
    }

    internal static string? RimRunnerRefMetadata(string dll)
    {
        string full = CommandContext.FullPath(dll);
        try
        {
            if (!RimTooling.IO.PeImage.TryReadIdentity(full, out _))
                throw new StoreException($"无法读取测试程序集的程序集元数据：{dll}");

            return RimTooling.IO.PeImage.TryReadAssemblyMetadata(full, "RimRunnerRef");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or BadImageFormatException or InvalidOperationException)
        {
            throw new StoreException($"无法读取测试程序集的程序集元数据：{dll}；{exception.Message}", exception);
        }
    }

    private static List<string> AssemblyReferences(string dll)
    {
        string full = CommandContext.FullPath(dll);
        try
        {
            if (!RimTooling.IO.PeImage.TryReadIdentity(full, out _))
                throw new StoreException($"无法读取测试程序集的程序集引用表：{dll}");

            return [.. RimTooling.IO.PeImage.ReadAssemblyReferences(full)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or BadImageFormatException or InvalidOperationException)
        {
            throw new StoreException($"无法读取测试程序集的程序集引用表：{dll}；{exception.Message}", exception);
        }
    }

    internal static (string? File, DateTime Utc) Newest(IEnumerable<string> files)
    {
        string? newestFile = null;
        DateTime newestUtc = DateTime.MinValue;
        foreach (string file in files)
        {
            DateTime utc = File.GetLastWriteTimeUtc(file);
            if (utc > newestUtc)
            {
                newestUtc = utc;
                newestFile = file;
            }
        }

        return (newestFile, newestUtc);
    }

    internal static IReadOnlyDictionary<string, string> FreshnessSurface(
        OrchestratorDescriptor descriptor, EnginePackage package)
    {
        var surface = new Dictionary<string, string>(descriptor.FreshnessAssemblies)
        {
            [AssemblyName] = package.DllPath,
        };
        if (descriptor.EngineSelfTests) surface[SelfTestAssemblyName] = package.SelfTestDllPath;
        return surface;
    }
}
