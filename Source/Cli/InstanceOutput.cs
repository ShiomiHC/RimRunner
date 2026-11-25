using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using RimRunner;

namespace RimRunnerCli;

internal sealed partial class InstanceOutput
{
    private const int SharingRetries = 40;
    private const int SharingRetryDelayMs = 50;
    private static readonly Lock[] PathGates = [.. Enumerable.Range(0, 64).Select(_ => new Lock())];

    private readonly Channel<Item> queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<Fault> faults = new();
    private readonly ConcurrentQueue<Fault> quietFaults = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Report>> reports = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Shot>> shots = new(StringComparer.Ordinal);

    private readonly TimeSpan ledgerLockTimeout;

    internal InstanceOutput(string root, TimeSpan? ledgerLockTimeout = null)
    {
        Root = CommandContext.FullPath(root);
        this.ledgerLockTimeout = ledgerLockTimeout ?? LedgerBook.LockTimeout;
        Drained = Task.Run(DrainAsync);
    }

    internal string Root { get; }

    internal Task Drained { get; }

    internal void Accept(JsonElement root, ReadOnlyMemory<byte> content)
    {
        string path = PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.Path) ?? "";
        var write = new Write(path, content,
            root.TryGetProperty(RimRunnerPipeProtocol.Keys.Append, out JsonElement append)
            && append.ValueKind == JsonValueKind.True,
            root.TryGetProperty(RimRunnerPipeProtocol.Keys.Quiet, out JsonElement quiet)
            && quiet.ValueKind == JsonValueKind.True);
        if (!queue.Writer.TryWrite(write))
            Record(new Fault(path, "实例管道已关闭，未写入", write.Quiet));
    }

    internal void AcceptReport(JsonElement root, ReadOnlyMemory<byte> json)
    {
        string runId = PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.RunId) ?? "";
        string archive = PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.Path) ?? "";
        (int, int)? keep = root.TryGetProperty(RimRunnerPipeProtocol.Keys.KeepRecent, out JsonElement recent)
                           && recent.TryGetInt32(out int keepRecent)
                           && root.TryGetProperty(RimRunnerPipeProtocol.Keys.KeepFailed, out JsonElement failed)
                           && failed.TryGetInt32(out int keepFailed)
            ? (keepRecent, keepFailed)
            : null;
        var write = new ReportWrite(runId, archive, PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.Latest),
            keep, json);
        if (!queue.Writer.TryWrite(write))
            ArrivalSource(runId).TrySetResult(new Report(runId, archive, null, "实例管道已关闭，未写入"));
    }

    internal Task<Report> Arrival(string runId) => ArrivalSource(runId).Task;

    private TaskCompletionSource<Report> ArrivalSource(string runId) =>
        reports.GetOrAdd(runId, _ => new TaskCompletionSource<Report>(TaskCreationOptions.RunContinuationsAsynchronously));

    internal void AcceptScreenshot(JsonElement root, ReadOnlyMemory<byte> png)
    {
        string snapshot = root.TryGetProperty(RimRunnerPipeProtocol.Keys.Snapshot, out JsonElement body)
                          && body.ValueKind == JsonValueKind.Object
            ? body.GetRawText()
            : "{}";
        string id = body.ValueKind == JsonValueKind.Object
            ? PipeJson.TextOrNull(body, RimRunnerLiveContract.Screenshot.ArtifactId) ?? ""
            : "";
        var write = new ShotWrite(id, PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.Path), png, snapshot);
        if (!queue.Writer.TryWrite(write))
            ShotSource(id).TrySetResult(new Shot(snapshot, write.Path == null ? null : "实例管道已关闭，未写入"));
    }

    internal Task<Shot> ScreenshotArrival(string artifactId) => ShotSource(artifactId).Task;

    internal void ForgetScreenshot(string artifactId) => shots.TryRemove(artifactId, out _);

    private TaskCompletionSource<Shot> ShotSource(string artifactId) =>
        shots.GetOrAdd(artifactId, _ => new TaskCompletionSource<Shot>(TaskCreationOptions.RunContinuationsAsynchronously));

    internal void Divert(ShardGroupSink sink, int index, string runId) =>
        queue.Writer.TryWrite(new DivertTo(new ShardTarget(sink, index, runId)));

    internal void Undivert() => queue.Writer.TryWrite(new DivertTo(null));

    internal Task Settled()
    {
        var barrier = new Barrier();
        return queue.Writer.TryWrite(barrier) ? barrier.Done.Task : Drained;
    }

    internal void Complete() => queue.Writer.TryComplete();

    internal IReadOnlyList<Fault> TakeFaults() => Take(faults);

    internal IReadOnlyList<Fault> TakeQuietFaults() => Take(quietFaults);

    private static List<Fault> Take(ConcurrentQueue<Fault> from)
    {
        var taken = new List<Fault>();
        while (from.TryDequeue(out Fault? fault)) taken.Add(fault);
        return taken;
    }

    private void Record(Fault fault) => (fault.Quiet ? quietFaults : faults).Enqueue(fault);

    private async Task DrainAsync()
    {
        await foreach (Item item in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item is Barrier barrier)
            {
                barrier.Done.TrySetResult();
                continue;
            }

            if (item is SamplesUse use)
            {
                UseSampleKind(use.Kind);
                continue;
            }

            if (item is SampleWrite sample)
            {
                ApplySample(sample);
                continue;
            }

            if (item is DivertTo divertTo)
            {
                divert = divertTo.Target;
                continue;
            }

            if (item is LedgerWrite ledger)
            {
                if (divert != null) DivertLedger(divert, ledger);
                else ApplyLedger(ledger);
                continue;
            }

            if (item is ReportWrite report)
            {
                ArrivalSource(report.RunId).TrySetResult(ApplyReport(report));
                continue;
            }

            if (item is ShotWrite shot)
            {
                ShotSource(shot.ArtifactId).TrySetResult(new Shot(shot.Snapshot, ApplyShot(shot)));
                continue;
            }

            var write = (Write)item;
            try
            {
                Apply(write);
            }
            catch (Exception exception) when (IsWriteFault(exception))
            {
                Record(new Fault(write.Path, exception.Message, write.Quiet));
            }
        }

        CloseSampleStore();
    }

    internal static bool IsWriteFault(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
            or NotSupportedException or JsonException;

    private void Apply(Write write)
    {
        string full = Resolve(write.Path);
        lock (GateOf(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (write.Append) AppendAll(full, write.Content.Span);
            else RimTooling.IO.AtomicFile.Write(full, write.Content.Span);
        }
    }

    private string? ApplyShot(ShotWrite shot)
    {
        if (shot.Path == null) return null;
        try
        {
            Apply(new Write(shot.Path, shot.Png, Append: false, Quiet: true));
            return null;
        }
        catch (Exception exception) when (IsWriteFault(exception))
        {
            return exception.Message;
        }
    }

    private static Lock GateOf(string full) =>
        PathGates[(StringComparer.OrdinalIgnoreCase.GetHashCode(full) & int.MaxValue) % PathGates.Length];

    internal static void UnderGate(string full, Action action)
    {
        lock (GateOf(full)) action();
    }

    private Report ApplyReport(ReportWrite write)
    {
        string archive, latest;
        try
        {
            if (divert != null && divert.RunId == write.RunId)
            {
                ShardGroupSink sink = divert.Sink;
                divert = null;
                archive = sink.ReportTarget(write.ArchivePath);
                Replace(archive, WithLedgerFaults(write.Json.ToArray(), SettleLedgers()));
                return new Report(write.RunId, archive, null, null);
            }

            archive = Resolve(write.ArchivePath);
            byte[] json = WithLedgerFaults(write.Json.ToArray(), SettleLedgers());
            Replace(archive, json);
            if (write.LatestPath == null) return Settle(write, archive, null);
            latest = Resolve(write.LatestPath);
            Replace(latest, json);
        }
        catch (Exception exception) when (IsWriteFault(exception))
        {
            return new Report(write.RunId, write.ArchivePath, null, exception.Message);
        }

        return Settle(write, archive, latest);
    }

    private Report Settle(ReportWrite write, string archive, string? latest)
    {
        if (write.Keep is (int keepRecent, int keepFailed))
        {
            try
            {
                var failures = new List<string>();
                OutputRetention.PruneArchive(Path.GetDirectoryName(archive)!, keepRecent, keepFailed,
                    RimRunnerArchiveName.AgeCutoff(DateTime.Now), keepNewest: true, failures);
                failures.AddRange(OutputRetention.Sweep(Root, DateTime.Now).Failures);
                foreach (string failure in failures)
                    Record(new Fault(write.ArchivePath, "清理旧产出失败：" + failure, Quiet: true));
            }
            catch (Exception exception) when (IsWriteFault(exception))
            {
                Record(new Fault(write.ArchivePath, "清理旧产出失败：" + exception.Message, Quiet: true));
            }
        }

        return new Report(write.RunId, archive, latest, null);
    }

    private static void Replace(string full, byte[] content)
    {
        lock (GateOf(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            RimTooling.IO.AtomicFile.Write(full, content);
        }
    }

    private string Resolve(string relative)
    {
        if (RimRunnerOutputPath.Fault(relative) is string fault) throw new InvalidDataException(fault);
        string full = CommandContext.FullPath(Path.Combine(Root,
            relative.Replace(RimRunnerOutputPath.Separator, Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("路径在产出根之外：" + relative);
        return full;
    }

    private static void AppendAll(string path, ReadOnlySpan<byte> content)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(content);
                return;
            }
            catch (IOException exception) when (attempt < SharingRetries && IsSharingViolation(exception))
            {
                Thread.Sleep(SharingRetryDelayMs);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception) => (exception.HResult & 0xFFFF) is 32 or 33;

    internal sealed record Fault(string Path, string Message, bool Quiet);

    internal sealed record Report(string RunId, string ArchivePath, string? LatestPath, string? Fault);

    internal sealed record Shot(string Snapshot, string? Fault);

    private abstract record Item;

    private ShardTarget? divert;

    private sealed record ShardTarget(ShardGroupSink Sink, int Index, string RunId);

    private sealed record DivertTo(ShardTarget? Target) : Item;

    private sealed record Write(string Path, ReadOnlyMemory<byte> Content, bool Append, bool Quiet) : Item;

    private sealed record ReportWrite(string RunId, string ArchivePath, string? LatestPath, (int Recent, int Failed)? Keep,
        ReadOnlyMemory<byte> Json) : Item;

    private sealed record ShotWrite(string ArtifactId, string? Path, ReadOnlyMemory<byte> Png, string Snapshot) : Item;

    private sealed record Barrier : Item
    {
        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
