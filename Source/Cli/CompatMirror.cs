using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RimRunnerCli;

internal static class CompatMirror
{
    internal const string SwitchPrefix = "WaylithClr.";
    internal const string DirMarker = ".rimrunner-nocompat-";
    private const string RuntimeConfigSuffix = ".runtimeconfig.json";
    private const string StampFile = "rimrunner-nocompat.txt";
    private const string UsersDir = "rimrunner-users";
    private static readonly string[] SkippedTopLevel = ["Mods", "RimRunnerStaging"];

    internal sealed record Selection(IReadOnlyList<string> Closed, IReadOnlyList<string> Kept)
    {
        public string Arg => string.Join(",", Closed);

        public string Summary => Describe(Arg, Kept.Select(Short));
    }

    internal static string Short(string name) =>
        name.StartsWith(SwitchPrefix, StringComparison.Ordinal) ? name[SwitchPrefix.Length..] : name;

    internal static string Describe(string closedArg, IEnumerable<string>? keptShort = null)
    {
        int count = closedArg.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
        List<string> kept = [.. keptShort ?? []];
        return kept.Count == 0 ? $"{count} 项" : $"{count} 项，保留 {string.Join("、", kept)}";
    }

    internal static string? Select(string runtimeConfigText, IReadOnlyList<string> keep, out Selection? selection)
    {
        selection = null;
        List<string> all = [.. Switches(runtimeConfigText).Select(pair => pair.Key)];
        if (all.Count == 0)
            return $"现装 runtimeconfig 里没有 {SwitchPrefix}* 开关，本轮不关闭任何兼容开关";
        var keptFull = new HashSet<string>(StringComparer.Ordinal);
        List<string> unknown = [];
        foreach (string name in keep)
        {
            string full = SwitchPrefix + name;
            if (all.Contains(full)) keptFull.Add(full);
            else unknown.Add(name);
        }
        if (unknown.Count > 0)
            return $"--keep-compat 指定的开关不在现装 runtimeconfig 里：{string.Join("、", unknown)}；现装的开关：{string.Join("、", all.Select(Short))}";
        List<string> closed = [.. all.Where(name => !keptFull.Contains(name))];
        if (closed.Count == 0)
            return "--keep-compat 保留全部开关，本批与常规批次相同";
        selection = new Selection(closed, [.. all.Where(keptFull.Contains)]);
        return null;
    }

    internal static string Rewrite(string runtimeConfigText, Selection selection)
    {
        JsonNode root = JsonNode.Parse(runtimeConfigText) ?? throw new StoreException("runtimeconfig 为空");
        JsonObject properties = ConfigProperties(root);
        foreach (string name in selection.Closed)
        {
            properties[name] = properties[name]?.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? JsonValue.Create("")
                : JsonValue.Create(false);
        }
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Switches(string runtimeConfigText)
    {
        JsonNode root = JsonNode.Parse(runtimeConfigText) ?? throw new StoreException("runtimeconfig 为空");
        return ConfigProperties(root).Where(pair => pair.Key.StartsWith(SwitchPrefix, StringComparison.Ordinal));
    }

    private static JsonObject ConfigProperties(JsonNode root) =>
        root["runtimeOptions"]?["configProperties"] as JsonObject
        ?? throw new StoreException("runtimeconfig 里没有 runtimeOptions.configProperties");

    internal static string RuntimeConfigPath(string gameExe) =>
        Path.Combine(Path.GetDirectoryName(CommandContext.FullPath(gameExe))!,
            Path.GetFileNameWithoutExtension(gameExe) + RuntimeConfigSuffix);

    internal static string MirrorName(string gameDirName, Selection selection, string treeStamp)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(selection.Arg + "\n" + treeStamp));
        return gameDirName + DirMarker + Convert.ToHexStringLower(hash)[..12];
    }

    private static string TreeStamp(string gameDir)
    {
        var lines = new List<string>();
        foreach ((string relative, string full, bool isLink) in Walk(gameDir))
        {
            if (isLink)
                lines.Add($"{relative}|->|{Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName}");
            else
            {
                var info = new FileInfo(full);
                lines.Add($"{relative}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    private static IEnumerable<(string Relative, string Full, bool IsLink)> Walk(string gameDir)
    {
        var pending = new Stack<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(gameDir))
        {
            if (SkippedTopLevel.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                continue;
            pending.Push(entry);
        }
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string relative = Path.GetRelativePath(gameDir, current);
            FileAttributes attributes = File.GetAttributes(current);
            if (!attributes.HasFlag(FileAttributes.Directory))
                yield return (relative, current, false);
            else if (attributes.HasFlag(FileAttributes.ReparsePoint))
                yield return (relative, current, true);
            else
                foreach (string child in Directory.EnumerateFileSystemEntries(current))
                    pending.Push(child);
        }
    }

    internal sealed class Use(string exe, string lease, ClaimStore store, string gameDir, Action<string>? log) : IDisposable
    {
        public string Exe { get; } = exe;
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                File.Delete(lease);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                CliLog.Info($"[兼容关闭] 镜像使用租约删除失败：{lease}（{failure.Message}）；本进程退出后该租约失效");
            }

            try
            {
                Collect(store, gameDir, log ?? CliLog.Info);
            }
            catch (Exception failure)
            {
                string line = $"[兼容关闭] 镜像回收未完成：{gameDir}（{failure.Message}）；下一次备镜像或卸载时会再次回收";
                CliLog.Info(line);
                log?.Invoke("[WARN] " + line);
            }
        }
    }

    internal static Use Ensure(string gameExe, Selection selection, ClaimStore store, Action<string>? log)
    {
        string gameDir = Path.GetDirectoryName(CommandContext.FullPath(gameExe))!;
        string parent = Path.GetDirectoryName(gameDir)
            ?? throw new StoreException($"游戏目录没有上级目录，镜像无法创建：{gameDir}");
        string mirror = Path.Combine(parent, MirrorName(Path.GetFileName(gameDir), selection, TreeStamp(gameDir)));
        using (store.MirrorLock())
        {
            if (!File.Exists(Path.Combine(mirror, StampFile)))
            {
                Build(gameDir, gameExe, mirror, selection, log);
                CollectHeld(store, gameDir, log, except: mirror);
            }
            using Process self = Process.GetCurrentProcess();
            string lease = Path.Combine(mirror, UsersDir, $"{self.Id}-{self.StartTime.ToUniversalTime().Ticks}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.GetDirectoryName(lease)!);
            File.WriteAllText(lease, "");
            return new Use(Path.Combine(mirror, Path.GetFileName(gameExe)), lease, store, gameDir, log);
        }
    }

    private static bool Leased(string mirror)
    {
        string dir = Path.Combine(mirror, UsersDir);
        if (!Directory.Exists(dir))
            return false;
        foreach (string lease in Directory.EnumerateFiles(dir))
        {
            string[] parts = Path.GetFileName(lease).Split('-');
            if (parts.Length != 3 || !int.TryParse(parts[0], out int pid) || !long.TryParse(parts[1], out long ticks))
                continue;
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (process.StartTime.ToUniversalTime().Ticks == ticks)
                    return true;
            }
            catch (Exception gone) when (gone is ArgumentException or InvalidOperationException
                                             or System.ComponentModel.Win32Exception)
            {
            }
        }
        return false;
    }

    private static void Build(string gameDir, string gameExe, string mirror, Selection selection, Action<string>? log)
    {
        Stopwatch watch = Stopwatch.StartNew();
        string partial = mirror + ".partial";
        if (Directory.Exists(mirror))
            FileSystemSafety.DeleteTreeJunctionSafe(mirror);
        if (Directory.Exists(partial))
            FileSystemSafety.DeleteTreeJunctionSafe(partial);
        Directory.CreateDirectory(partial);

        string configName = Path.GetFileName(RuntimeConfigPath(gameExe));
        int files = 0;
        try
        {
            foreach ((string relative, string full, bool isLink) in Walk(gameDir))
            {
                string target = Path.Combine(partial, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (isLink)
                {
                    Junction.Create(target, Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full);
                    continue;
                }
                if (string.Equals(relative, configName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!CreateHardLinkW(target, full, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new StoreException(error == ErrorNotSameDevice
                        ? $"兼容关闭所用的镜像目录须与游戏目录同卷，{parent(mirror)} 下无法创建硬链接：{full}"
                        : $"兼容关闭所用的镜像目录无法创建，硬链接 {full} 失败（Win32 错误 {error}）");
                }
                files++;
            }
            File.WriteAllText(Path.Combine(partial, configName),
                Rewrite(File.ReadAllText(RuntimeConfigPath(gameExe)), selection));
            Directory.CreateDirectory(Path.Combine(partial, "Mods"));
            File.WriteAllText(Path.Combine(partial, StampFile),
                $"source={gameDir}\nclosed={selection.Arg}\n");
            Directory.Move(partial, mirror);
        }
        catch
        {
            try
            {
                FileSystemSafety.DeleteTreeJunctionSafe(partial);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"[兼容关闭] 镜像目录未建成，残留未删除：{partial}（{cleanup.Message}）");
            }
            throw;
        }
        log?.Invoke($"[兼容关闭] 镜像目录已建：{mirror}（{watch.Elapsed.TotalSeconds:0.0} 秒）");

        static string parent(string path) => Path.GetDirectoryName(path) ?? path;
    }

    internal static void LinkForeignMods(string originalModsFolder, string mirrorModsFolder,
        IReadOnlyCollection<string> modIds, string productRoot, Action<string>? log)
    {
        var wanted = new HashSet<string>(modIds, StringComparer.OrdinalIgnoreCase);
        string productPrefix = CommandContext.FullPath(productRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (ModsFolderScan.ModsEntry entry in ModsFolderScan.Scan(originalModsFolder))
        {
            if (entry.PackageId == null || !wanted.Contains(entry.PackageId)
                || CommandContext.FullPath(entry.RealPath).StartsWith(productPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            string link = Path.Combine(mirrorModsFolder, Path.GetFileName(entry.Path));
            if (Directory.Exists(link))
                continue;
            Junction.Create(link, entry.RealPath);
            CliLog.Info($"[兼容关闭] 镜像 Mods/ 接入 {entry.PackageId}：{link} → {entry.RealPath}");
        }
    }

    internal static string? MirrorOf(string modsFolder)
    {
        string? dir = Path.GetDirectoryName(CommandContext.FullPath(modsFolder).TrimEnd(Path.DirectorySeparatorChar));
        return dir != null && Path.GetFileName(dir).Contains(DirMarker, StringComparison.Ordinal) ? dir : null;
    }

    internal static string GameDirOf(string mirror)
    {
        string name = Path.GetFileName(mirror);
        return Path.Combine(Path.GetDirectoryName(mirror)!, name[..name.IndexOf(DirMarker, StringComparison.Ordinal)]);
    }

    internal static void Collect(ClaimStore store, string gameDir, Action<string>? log)
    {
        using (store.MirrorLock())
            CollectHeld(store, gameDir, log, except: null);
    }

    internal static bool MayRunFromInside(string? commandLine, string inside) =>
        commandLine?.Contains(inside.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ?? true;

    private static void CollectHeld(ClaimStore store, string gameDir, Action<string>? log, string? except)
    {
        string? parent = Path.GetDirectoryName(gameDir);
        if (parent == null || !Directory.Exists(parent))
            return;
        string prefix = Path.GetFileName(gameDir) + DirMarker;
        List<ClaimRecord> claims = store.All();
        List<GameProcessProbe.GameProcess> processes = GameProcessProbe.Enumerate();
        foreach (string mirror in Directory.EnumerateDirectories(parent, prefix + "*"))
        {
            string inside = CommandContext.FullPath(mirror).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if ((except != null && PathIdentity.Same(mirror, except)) || Leased(mirror)
                || claims.Any(claim => CommandContext.FullPath(claim.ModsFolder).StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                || processes.Any(process => MayRunFromInside(process.CommandLine, inside)))
                continue;
            try
            {
                FileSystemSafety.DeleteTreeJunctionSafe(mirror);
                CliLog.Info($"[兼容关闭] 已删除无人使用的镜像目录：{mirror}");
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"[兼容关闭] 无人使用的镜像目录未删除，下一次再删除：{mirror}（{failure.Message}）");
            }
        }
    }

    private const int ErrorNotSameDevice = 17;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);
}
