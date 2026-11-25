using System.Diagnostics;
using System.Management;

namespace RimRunnerCli;

internal static class TreeHosts
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    internal static string? OwnDevTree()
    {
        try
        {
            if (EnginePackage.Locate(_ => null).ReadManifest().DevTree == null) return null;
        }
        catch (StoreException)
        {
            return null;
        }

        return MainWorktree.GitTreeRoot(AppContext.BaseDirectory);
    }

    internal static bool Unclaimed(string gitTree)
    {
        try
        {
            return !new ClaimStore().All().Any(claim =>
                MainWorktree.GitTreeRoot(claim.ProductRoot) is string tree && PathIdentity.Same(tree, gitTree));
        }
        catch (StoreException)
        {
            return false;
        }
    }

    internal static void AwaitExit(IEnumerable<string> gitTrees, TextWriter output)
    {
        List<string> trees = [.. gitTrees.Select(PathIdentity.Normalize).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (trees.Count == 0) return;
        List<int> hosts;
        try
        {
            hosts = HostsUnder(trees);
        }
        catch (StoreException exception)
        {
            output.WriteLine($"[WARN] 未等待这几棵树的编排进程退出：{exception.Message}");
            return;
        }

        DateTime deadline = DateTime.UtcNow + ExitWait;
        foreach (int pid in hosts)
        {
            try
            {
                using Process host = Process.GetProcessById(pid);
                TimeSpan left = deadline - DateTime.UtcNow;
                if (host.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero)) continue;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                continue;
            }

            output.WriteLine($"[WARN] 编排进程（PID {pid}）{ExitWait.TotalSeconds:0} 秒内未退出，所在的树仍被占用");
        }
    }

    private static List<int> HostsUnder(List<string> trees)
    {
        string image = Path.GetFileName(Environment.ProcessPath ?? "rimrunner.exe").Replace("'", "''", StringComparison.Ordinal);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '{image}'");
            return
            [
                .. searcher.Get()
                    .Cast<ManagementObject>()
                    .Where(mo => (int)(uint)mo["ProcessId"] != Environment.ProcessId
                                 && IsHost((string?)mo["CommandLine"])
                                 && (string?)mo["ExecutablePath"] is string exe
                                 && trees.Any(tree => Under(exe, tree)))
                    .Select(mo => (int)(uint)mo["ProcessId"])
            ];
        }
        catch (ManagementException exception)
        {
            throw new StoreException($"无法枚举编排进程：{exception.Message}");
        }
    }

    private static bool IsHost(string? commandLine) =>
        commandLine != null && commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(OrchestratorPipe.HostVerb);

    private static bool Under(string path, string tree) =>
        PathIdentity.Normalize(path).StartsWith(tree + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
