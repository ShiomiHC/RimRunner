using System.Text;
using System.Text.Json;

namespace RimRunnerCli;

internal static class ObservationSignatures
{
    public static void Report(string text, string? logPath, string productRoot, string command,
        string? runId, TextWriter terminal, string? tablePath = null, string? hitLogPath = null)
    {
        IReadOnlyList<(string Section, string Key)> table;
        try
        {
            tablePath ??= TablePath();
            if (tablePath == null) return;
            table = ObservationCore.Load(tablePath);
        }
        catch (ObservationCore.SignatureException exception)
        {
            terminal.WriteLine($"{ObservationCore.FaultMark} {exception.Message}");
            return;
        }

        List<ObservationCore.Hit> hits = ObservationCore.Match(text, table);
        foreach (ObservationCore.Hit hit in hits)
        {
            PrintHit(terminal, hit);
        }

        if (hits.Count == 0)
        {
            return;
        }

        string? pinned = logPath == null ? null : Pin(logPath, terminal.WriteLine);
        terminal.WriteLine(logPath == null
            ? "  RimRunner 日志文件写入失败（见上方 [日志故障]）"
            : $"  RimRunner 日志：{pinned ?? logPath}");

        try
        {
            AppendLog(hitLogPath ?? HitLogPath(), hits, pinned ?? logPath, productRoot, command, runId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            terminal.WriteLine($"{ObservationCore.FaultMark} 命中未记入 {hitLogPath ?? HitLogPath()}：{exception.Message}");
        }
    }

    internal static bool MarksEnabled()
    {
        try
        {
            return TablePath() != null;
        }
        catch (ObservationCore.SignatureException)
        {
            return false;
        }
    }

    internal static void Mark(string key, string text)
    {
        Mark(key, text, CliLog.Info);
    }

    internal static void Mark(string key, string text, Action<string> write)
    {
        if (MarksEnabled()) write(RimRunner.RimRunnerObservationKeys.Line(key, text));
    }

    private static void PrintHit(TextWriter terminal, ObservationCore.Hit hit) =>
        terminal.WriteLine($"{ObservationCore.HitMark} {ObservationCore.LedgerFileName}「{hit.Section}」—— 日志命中 {hit.Lines} 行，"
            + $"首行：{hit.FirstLine}（不计入退出码）");

    private static string? Pin(string logPath, Action<string> fault)
    {
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(logPath)!, ObservationCore.PinnedSubDirName);
            Directory.CreateDirectory(dir);
            string pinned = Path.Combine(dir, Path.GetFileName(logPath));
            using (var source = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var target = new FileStream(pinned, FileMode.Create, FileAccess.Write))
                source.CopyTo(target);
            return pinned;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            fault($"[日志故障] 命中的 RimRunner 日志未钉住，会随日志修剪删除：{exception.Message}");
            return null;
        }
    }

    internal static TailWatch? WatchTail(string productRoot, int number, string? tablePath = null,
        string? hitLogPath = null)
    {
        try
        {
            tablePath ??= TablePath();
            if (tablePath == null) return null;
            return new TailWatch(ObservationCore.Load(tablePath), hitLogPath ?? HitLogPath(), productRoot, number);
        }
        catch (ObservationCore.SignatureException)
        {
            return null;
        }
    }

    internal static TailWatch? WatchCoordinator(string? tablePath = null, string? hitLogPath = null)
    {
        try
        {
            tablePath ??= TablePath();
            if (tablePath == null) return null;
            return new TailWatch(ObservationCore.Load(tablePath), hitLogPath ?? HitLogPath(), null, null);
        }
        catch (ObservationCore.SignatureException)
        {
            return null;
        }
    }

    internal sealed class TailWatch(IReadOnlyList<(string Section, string Key)> table, string hitLogPath,
        string? productRoot, int? number)
    {
        private readonly HashSet<string> recorded = new(StringComparer.Ordinal);

        internal IReadOnlyList<string> Lines(string logPath, string entries)
        {
            List<ObservationCore.Hit> fresh =
                [.. ObservationCore.Match(entries, table).Where(hit => recorded.Add(hit.Section))];
            if (fresh.Count == 0) return [];
            var faults = new List<string>();
            string? pinned = Pin(logPath, faults.Add);
            try
            {
                if (number is int tailNumber)
                    AppendLog(hitLogPath, fresh, pinned ?? logPath, productRoot!, command: null, runId: null, tailNumber);
                else
                    AppendLog(hitLogPath, fresh, pinned ?? logPath, productRoot: null, command: null, runId: null, coordinator: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                foreach (ObservationCore.Hit hit in fresh) recorded.Remove(hit.Section);
                faults.Add($"{ObservationCore.FaultMark} 命中未记入 {hitLogPath}：{exception.Message}");
            }

            return faults;
        }

        internal void Seal(string logPath)
        {
            if (recorded.Count > 0 && File.Exists(logPath)) Pin(logPath, _ => { });
        }
    }

    internal static string? TablePath() =>
        TablePath(EnginePackage.Locate(), CommandContext.GetEnvironmentVariable(IsolatedRoot.SignaturesVariable));

    internal static string? TablePath(EnginePackage package, string? overridden)
    {
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        PackageManifest manifest;
        try
        {
            manifest = package.ReadManifest();
        }
        catch (StoreException exception)
        {
            throw new ObservationCore.SignatureException(exception.Message);
        }

        return manifest.Observations ? Path.Combine(package.ObservationsDir, ObservationCore.TableFileName) : null;
    }

    internal static string HitLogPath() => ObservationCore.HitLogPath(MachineRoot.Resolve());

    private static void AppendLog(string path, List<ObservationCore.Hit> hits, string? logPath, string? productRoot,
        string? command, string? runId, int? number = null, bool coordinator = false)
    {
        var sb = new StringBuilder();
        string at = DateTime.Now.ToString(ObservationCore.CursorFormat);
        foreach (ObservationCore.Hit hit in hits)
        {
            var entry = new Dictionary<string, object?> { ["at"] = at, ["section"] = hit.Section };
            if (productRoot != null) entry["productRoot"] = productRoot;
            if (coordinator)
            {
                entry["coordinator"] = true;
            }
            else if (number is int tailNumber)
            {
                entry["instance"] = ClaimStore.Named(tailNumber);
            }
            else
            {
                entry["command"] = command;
                entry["runId"] = runId;
            }

            entry["lines"] = hit.Lines;
            entry["firstLine"] = hit.FirstLine;
            entry["log"] = logPath;
            sb.Append(JsonSerializer.Serialize(entry,
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            sb.Append('\n');
        }

        ActivityLog.AppendShared(path, sb.ToString());
    }
}
