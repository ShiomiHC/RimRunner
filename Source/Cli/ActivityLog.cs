using System.Security.AccessControl;
using System.Text;
using System.Text.Json;

namespace RimRunnerCli;

internal static class ActivityLog
{
    public const string BatchStart = "batch-start";
    public const string BatchEnd = "batch-end";
    public const string Ready = "ready";
    public const string Live = "live";
    public const string Showcase = "showcase";
    public const string Film = "film";
    public const string Stop = "stop";
    public const string Queue = "queue";
    public const string MachineQueue = "machine-queue";
    public const string Reap = "reap";
    public const string LoadTraced = "load-trace";
    public const string MemoryHighEvent = "memory-high";

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal const long RotateBytes = 1024 * 1024;

    private static bool processFailureNoted;

    internal static string LogPath()
    {
        return Path.Combine(MachineRoot.Resolve(), "activity.jsonl");
    }

    internal sealed record QueuedInstance(int Pid, int? Number, long Bytes);

    public static void Touch(string ev, int number, int? gamePid = null, string? detail = null,
        EngineReport? report = null)
    {
        ClaimRecord? claim = null;
        try { claim = new ClaimStore().Read(number); }
        catch (StoreException) { }

        InstanceLink? link = InstanceLinks.Get(number);

        Append(new
        {
            at = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ev,
            number,
            pid = gamePid ?? link?.Pid,
            launchedAt = link?.Runtime.LaunchedAt?.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            productRoot = claim?.ProductRoot,
            purpose = claim?.EffectivePurpose,
            cli = Environment.ProcessId,
            detail,
            command = report?.Command,
            modList = report == null ? null : report.ModList ?? "",
            memoryStart = report?.MemoryStart is { Count: > 0 } start ? start : null,
            memoryEnd = report?.MemoryEnd is { Count: > 0 } end ? end : null,
        });
    }

    public static void MemoryHigh(int number, int? gamePid, string key, long bytes, long peak, int instances)
    {
        Append(new
        {
            at = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ev = MemoryHighEvent,
            number,
            pid = gamePid,
            cli = Environment.ProcessId,
            key,
            bytes,
            peak,
            instances,
        });
    }

    public static void Queued(long need, long available, IEnumerable<QueuedInstance> instances)
    {
        Append(new
        {
            at = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ev = Queue,
            cli = Environment.ProcessId,
            need,
            available,
            instances = instances.Select(i => new { pid = i.Pid, number = i.Number, bytes = i.Bytes }).ToList(),
        });
    }

    public static void MachineQueued(string reason, int cap, double? idle, double? need, int busy, int ahead)
    {
        List<object> instances = [];
        try
        {
            foreach (var game in GameProcessProbe.Enumerate())
            {
                double? cpuSeconds = null;
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(game.Pid);
                    cpuSeconds = Math.Round(process.TotalProcessorTime.TotalSeconds, 2);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                                      or System.ComponentModel.Win32Exception)
                {
                }

                instances.Add(new
                {
                    pid = game.Pid,
                    number = GameProcessProbe.ClaimedNumber(game.CommandLine, RimRunner.RimRunnerEngineArgs.Instance),
                    cpuSeconds,
                });
            }
        }
        catch (StoreException)
        {
        }

        Append(new
        {
            at = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ev = MachineQueue,
            cli = Environment.ProcessId,
            reason,
            cap,
            idle = idle is double free ? Math.Round(free, 2) : (double?)null,
            need,
            busy,
            ahead,
            instances,
        });
    }

    public static void LoadTrace(int number, int gamePid, object trace)
    {
        Append(new
        {
            at = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ev = LoadTraced,
            number,
            pid = gamePid,
            cli = Environment.ProcessId,
            trace,
        });
    }

    private static void Append(object entry)
    {
        string path = LogPath();
        try
        {
            RotateIfOver(path, RotateBytes);
            AppendShared(path, JsonSerializer.Serialize(entry, Options) + "\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (FirstFailure())
            {
                Console.Error.WriteLine($"[activity] 写入失败：{path}：{exception.Message}");
            }
        }
    }

    internal static void RotateIfOver(string path, long limit)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= limit) return;

        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(CommandContext.FullPath(path).ToUpperInvariant())))[..16];
        using var mutex = new Mutex(false, "RimRunner-activity-rotate-" + key);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned) return;
        try
        {
            info.Refresh();
            if (info.Exists && info.Length > limit)
                File.Move(path, path + ".1", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static bool FirstFailure()
    {
        if (CommandContext.Current is { } context)
        {
            lock (context.NotedOnce) return context.NotedOnce.Add("activity-write");
        }

        if (processFailureNoted) return false;
        processFailureNoted = true;
        return true;
    }

    internal static void AppendShared(string path, string text)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using FileStream stream = new FileInfo(path).Create(FileMode.Append, FileSystemRights.AppendData,
                    FileShare.ReadWrite, 1, FileOptions.None, null);
                stream.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (Exception exception) when (attempt < 3
                                              && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
