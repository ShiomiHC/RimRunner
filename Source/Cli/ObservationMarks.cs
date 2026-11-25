using System.Text;
using System.Text.Json;

namespace RimRunnerCli;

internal static class ObservationMarks
{
    internal const string FaultMark = "[观察故障]";

    public static void Report(string text, string? logPath, string productRoot, string command,
        string? runId, TextWriter terminal, string? hitLogPath = null)
    {
        try
        {
            hitLogPath = Destination(hitLogPath);
        }
        catch (StoreException exception)
        {
            terminal.WriteLine($"{FaultMark} {exception.Message}");
            return;
        }

        if (hitLogPath == null) return;
        List<ObservationHits.Hit> hits = ObservationHits.Match(text);
        if (hits.Count == 0) return;
        foreach (ObservationHits.Hit hit in hits)
            terminal.WriteLine($"{ObservationHits.HitMark} {hit.Key} —— 日志命中 {hit.Lines} 行，首行：{hit.FirstLine}（不计入退出码）");

        string? pinned = logPath == null ? null : Pin(logPath, terminal.WriteLine);
        terminal.WriteLine(logPath == null
            ? "  RimRunner 日志文件写入失败（见上方 [日志故障]）"
            : $"  RimRunner 日志：{pinned ?? logPath}");

        try
        {
            AppendLog(hitLogPath, hits, pinned ?? logPath, productRoot, command, runId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            terminal.WriteLine($"{FaultMark} 命中未记入 {hitLogPath}：{exception.Message}");
        }
    }

    internal static bool MarksEnabled()
    {
        try
        {
            return Declared();
        }
        catch (StoreException)
        {
            return false;
        }
    }

    private static bool Declared() => EnginePackage.Locate().ReadManifest().Observations;

    private static string? Destination(string? overridden) =>
        overridden ?? (Declared() ? ObservationHits.LogPath(MachineRoot.Resolve()) : null);

    internal static void Mark(string key, string text)
    {
        Mark(key, text, CliLog.Info);
    }

    internal static void Mark(string key, string text, Action<string> write)
    {
        if (MarksEnabled()) write(RimRunner.RimRunnerObservationKeys.Line(key, text));
    }

    private static string? Pin(string logPath, Action<string> fault)
    {
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(logPath)!, ObservationHits.PinnedSubDirName);
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

    internal static TailWatch? WatchTail(string productRoot, int number, string? hitLogPath = null) =>
        Watch(productRoot, number, hitLogPath);

    internal static TailWatch? WatchCoordinator(string? hitLogPath = null) => Watch(null, null, hitLogPath);

    private static TailWatch? Watch(string? productRoot, int? number, string? hitLogPath)
    {
        try
        {
            return Destination(hitLogPath) is { } destination ? new TailWatch(destination, productRoot, number) : null;
        }
        catch (StoreException)
        {
            return null;
        }
    }

    internal sealed class TailWatch(string hitLogPath, string? productRoot, int? number)
    {
        private readonly HashSet<string> recorded = new(StringComparer.Ordinal);

        internal IReadOnlyList<string> Lines(string logPath, string entries)
        {
            List<ObservationHits.Hit> fresh = [.. ObservationHits.Match(entries).Where(hit => recorded.Add(hit.Key))];
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
                foreach (ObservationHits.Hit hit in fresh) recorded.Remove(hit.Key);
                faults.Add($"{FaultMark} 命中未记入 {hitLogPath}：{exception.Message}");
            }

            return faults;
        }

        internal void Seal(string logPath)
        {
            if (recorded.Count > 0 && File.Exists(logPath)) Pin(logPath, _ => { });
        }
    }

    private static void AppendLog(string path, List<ObservationHits.Hit> hits, string? logPath, string? productRoot,
        string? command, string? runId, int? number = null, bool coordinator = false)
    {
        var sb = new StringBuilder();
        string at = DateTime.Now.ToString(ObservationHits.TimeFormat);
        foreach (ObservationHits.Hit hit in hits)
        {
            var entry = new Dictionary<string, object?> { ["at"] = at, [ObservationHits.KeyField] = hit.Key };
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
