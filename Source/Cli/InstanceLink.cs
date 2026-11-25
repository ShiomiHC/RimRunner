using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RimRunner;

namespace RimRunnerCli;

internal sealed class InstanceLink
{
    internal const int MaxIncomingFrameBytes = 64 * 1024 * 1024;
    internal const long OutboxBytes = 16 * 1024 * 1024;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);
    private const string SelfExitMark = " 实例自行退出：";
    private const int PlayerLogTailBytes = 64 * 1024;
    private const int TailBacklogChars = 1024 * 1024;

    private readonly NamedPipeServerStream server;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<Reply>> pending = new();
    private readonly RimRunnerPipeOutbox outbox = new(OutboxBytes);
    private readonly RimRunnerPipeWatchdog watchdog = new(TimeSpan.FromSeconds(RimRunnerPipeProtocol.WatchdogSeconds),
        () => Environment.TickCount64);
    private readonly Lock sinkGate = new();
    private readonly List<IEngineLogSink> sinks = [];
    private readonly string playerLogPath;
    private readonly Action<string>? recordStop;
    private TaskCompletionSource changed = NewSignal();
    private readonly TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource recorded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? game;
    private int? expectedPid;
    private Timer? heartbeat;
    private long nextId;
    private long logEvents;
    private int closing;
    private int closed;
    private volatile bool connected;
    private volatile Handshake? hello;
    private volatile RunStatus? state;
    private volatile Pulse? pulse;
    private volatile GpuReading? gpu;
    private volatile string? fault;
    private volatile string? closedReason;
    private volatile string? exitReason;
    private LogTail? tail;
    private string? lastLog;
    private string tailBacklog = "";
    private DateTime? idleSince;
    private int usingRequests;
    private ShardServing? shardClaims;

    private sealed record ShardServing(ShardGroupQueue Queue, int Shard);

    private readonly Lock lifecycleGate = new();
    private readonly Lock runtimeGate = new();
    private InstanceRuntime runtime = new();

    private readonly Lock holdGate = new();
    private DateTimeOffset? holdUntil;

    internal sealed record LogTail(string Path, ObservationSignatures.TailWatch? Watch, string? Tag = null);

    private InstanceLink(int number, TimeSpan idleLimit, string playerLogPath, string outputRoot,
        Action<string>? recordStop, bool survivesDrain)
    {
        Number = number;
        SurvivesDrain = survivesDrain;
        Output = new InstanceOutput(outputRoot);
        Film = new FilmReceiver(SendFilmFed);
        IdleLimit = idleLimit;
        this.playerLogPath = playerLogPath;
        this.recordStop = recordStop;
        PipeName = $"rimrunner-{OrchestratorPipe.User}-inst-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";
        server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
    }

    internal int Number { get; }
    internal string PipeName { get; }
    internal TimeSpan IdleLimit { get; }
    internal bool SurvivesDrain { get; }
    internal int? Pid => game?.Id;
    internal bool Connected => connected && Volatile.Read(ref closed) == 0;
    internal bool EverConnected => connected;
    internal bool Closed => Volatile.Read(ref closed) != 0;
    private volatile bool exitedBeforeConnect;
    internal bool ExitedBeforeConnect => exitedBeforeConnect;
    internal Handshake? Hello => hello;
    internal RunStatus? State => state;
    internal Pulse? LatestPulse => pulse;
    internal long? GpuDemand => gpu?.Demand;
    internal int? UncachedTextures => gpu?.Uncached;
    internal long LogEvents => Interlocked.Read(ref logEvents);
    internal string? Fault => fault;
    internal string? ClosedReason => closedReason;
    internal int PendingCount => pending.Count;
    internal InstanceOutput Output { get; }
    internal FilmReceiver Film { get; }

    internal InstanceRuntime Runtime => Volatile.Read(ref runtime);

    internal void UpdateRuntime(Func<InstanceRuntime, InstanceRuntime> change)
    {
        lock (runtimeGate) Volatile.Write(ref runtime, change(runtime));
    }

    internal Task Settled => settled.Task;

    internal Task StopRecorded => recorded.Task;

    internal async Task<string?> ExitReasonAsync()
    {
        await StopRecorded.ConfigureAwait(false);
        return exitReason ?? closedReason;
    }

    internal Task Changed => Volatile.Read(ref changed).Task;

    internal TimeSpan? IdleFor
    {
        get
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan local;
            lock (sinkGate)
            {
                if (idleSince is not DateTime since) return null;
                local = now - since;
            }

            if (pulse is not { IdleSeconds: double seconds } latest) return local;
            TimeSpan reported = TimeSpan.FromSeconds(seconds) + (now - latest.ReceivedAt);
            return reported < local ? reported : local;
        }
    }

    internal static InstanceLink Create(int number, TimeSpan idleLimit, string playerLogPath, string outputRoot,
        Action<string>? recordStop, bool survivesDrain) =>
        new(number, idleLimit, playerLogPath, outputRoot, recordStop, survivesDrain);

    internal void Attach(Process process, ProcessIdentity.Stamp identity)
    {
        try
        {
            game = Process.GetProcessById(process.Id);
        }
        catch (ArgumentException)
        {
            Shutdown("游戏进程刚启动即退出", byUs: false, exitedBeforeConnect: true);
            return;
        }

        expectedPid = game.Id;
        UpdateRuntime(current => current with { LaunchedAt = DateTimeOffset.Now, GameStartedAt = identity.StartedAt });
        _ = DetachedTask.Run(AcceptAsync);
        Process watched = game;
        _ = DetachedTask.Run(() => WatchExitBeforeConnectAsync(watched));
    }

    private async Task WatchExitBeforeConnectAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            return;
        }

        if (!connected) Shutdown("游戏进程在引擎连上实例管道之前退出", byUs: false, exitedBeforeConnect: true);
    }

    internal void AcceptPeer(int pid)
    {
        expectedPid = pid;
        _ = DetachedTask.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            await server.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException
                                              or IOException)
        {
            Shutdown("管道在引擎连上之前关闭：" + exception.Message, byUs: true);
            return;
        }

        int? expected = expectedPid;
        if (Volatile.Read(ref closing) != 0) return;
        uint client = 0;
        bool gotPid;
        try
        {
            gotPid = GetNamedPipeClientProcessId(server.SafePipeHandle, out client);
        }
        catch (ObjectDisposedException)
        {
            if (Volatile.Read(ref closing) != 0) return;
            throw;
        }

        if (Volatile.Read(ref closing) != 0) return;
        if (!gotPid || (int)client != expected)
        {
            fault = $"实例管道被 PID {client} 连上，非本次启动的游戏（PID {expected}）";
            Shutdown(fault, byUs: true);
            return;
        }

        lock (lifecycleGate)
        {
            if (Volatile.Read(ref closing) != 0) return;
            connected = true;
            heartbeat = new Timer(_ => Beat(), null, RimRunnerPipeProtocol.HeartbeatIntervalMilliseconds,
                RimRunnerPipeProtocol.HeartbeatIntervalMilliseconds);
        }

        watchdog.NoteReceived();
        new Thread(WriteLoop) { IsBackground = true, Name = $"rimrunner-instance-{Number}-write" }.Start();
        Signal();
        await ReadLoopAsync().ConfigureAwait(false);
    }

    internal const string PeerClosedReason = "实例关闭管道";

    private void WriteLoop()
    {
        while (!Closed)
        {
            byte[]? frame = outbox.Take(1000);
            if (frame == null) continue;
            watchdog.NoteWriteStarted();
            try
            {
                server.Write(frame);
                server.Flush();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                                  or InvalidOperationException or OperationCanceledException)
            {
                Shutdown(RimRunnerPipeFrame.IsPeerClosed(exception) ? PeerClosedReason : "写管道失败：" + exception.Message, byUs: false);
                return;
            }
            finally
            {
                watchdog.NoteWriteFinished();
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        string reason;
        try
        {
            while (true)
            {
                RimRunnerPipeFrame? frame = await RimRunnerPipeFrame.ReadAsync(server, MaxIncomingFrameBytes,
                    lifetime.Token).ConfigureAwait(false);
                if (frame == null)
                {
                    reason = PeerClosedReason;
                    break;
                }

                watchdog.NoteReceived();
                using (frame) Dispatch(frame.Root, frame.Binary);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                              or OperationCanceledException or RimRunnerPipeProtocolException
                                              or EndOfStreamException)
        {
            reason = RimRunnerPipeFrame.IsPeerClosed(exception) ? PeerClosedReason : "读管道失败：" + exception.Message;
        }

        Shutdown(reason, byUs: false);
    }

    private void Dispatch(JsonElement root, ReadOnlyMemory<byte> binary)
    {
        if (root.TryGetProperty(RimRunnerPipeProtocol.Id, out JsonElement id) && id.TryGetInt64(out long requestId)
            && root.TryGetProperty(RimRunnerPipeProtocol.Status, out JsonElement status))
        {
            if (pending.TryRemove(requestId, out TaskCompletionSource<Reply>? waiter))
            {
                string body = root.TryGetProperty(RimRunnerPipeProtocol.Body, out JsonElement value)
                    ? value.GetRawText()
                    : "null";
                waiter.TrySetResult(new Reply(status.GetInt32(), body));
            }

            UpdateIdle();
            return;
        }

        if (!root.TryGetProperty(RimRunnerPipeProtocol.Event, out JsonElement name)) return;
        switch (name.GetString())
        {
            case RimRunnerPipeProtocol.Events.Hello:
                if (RimRunnerPipeFrame.ProtocolMismatch(root) is string mismatch)
                {
                    fault = mismatch;
                    Stop("握手的" + mismatch);
                    return;
                }

                hello = Handshake.Parse(root);
                break;
            case RimRunnerPipeProtocol.Events.State:
                state = RunStatus.Parse(root);
                UpdateIdle();
                break;
            case RimRunnerPipeProtocol.Events.Pulse:
                pulse = Pulse.Next(pulse, root, DateTime.UtcNow);
                break;
            case RimRunnerPipeProtocol.Events.GpuMemory:
                gpu = GpuReading.Parse(root);
                return;
            case RimRunnerPipeProtocol.Events.Log:
                Deliver(root);
                Interlocked.Increment(ref logEvents);
                break;
            case RimRunnerPipeProtocol.Events.Output:
                Output.Accept(root, binary);
                return;
            case RimRunnerPipeProtocol.Events.Sample:
                Output.AcceptSample(root, binary);
                return;
            case RimRunnerPipeProtocol.Events.Ledger:
                Output.AcceptLedger(root);
                return;
            case RimRunnerPipeProtocol.Events.Report:
                Output.AcceptReport(root, binary);
                return;
            case RimRunnerPipeProtocol.Events.Screenshot:
                Output.AcceptScreenshot(root, binary);
                return;
            case RimRunnerPipeProtocol.Events.FilmFrame:
                Film.AcceptFrame(binary, root);
                return;
            case RimRunnerPipeProtocol.Events.FilmResult:
                Film.AcceptResult(root);
                return;
            case RimRunnerPipeProtocol.Events.ShardClaim:
                AnswerShardClaim(root);
                return;
            case RimRunnerPipeProtocol.Events.ShardDone:
                if (Volatile.Read(ref shardClaims) is { } serving
                    && root.TryGetProperty(RimRunnerPipeProtocol.Keys.Ordinal, out JsonElement done) && done.TryGetInt32(out int ordinal))
                    serving.Queue.Complete(serving.Shard, ordinal, DateTime.UtcNow);
                return;
            default:
                return;
        }

        Signal();
    }

    internal async Task<Reply> RequestAsync(string op, Action<Utf8JsonWriter>? parameters = null,
        TimeSpan? timeout = null, CancellationToken token = default, string? oversizeNote = null)
    {
        if (!Connected) return Reply.NoLink;
        long id = Interlocked.Increment(ref nextId);
        var waiter = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiter;
        bool uses = op != RimRunnerPipeProtocol.Ops.Progress;
        if (uses) Interlocked.Increment(ref usingRequests);
        UpdateIdle();
        try
        {
            byte[] frame = RimRunnerPipeFrame.EncodeObject(writer =>
            {
                writer.WriteNumber(RimRunnerPipeProtocol.Id, id);
                writer.WriteString(RimRunnerPipeProtocol.Op, op);
                if (parameters == null) return;
                writer.WriteStartObject(RimRunnerPipeProtocol.Params);
                parameters(writer);
                writer.WriteEndObject();
            });
            int payload = frame.Length - RimRunnerPipeFrame.PrefixBytes;
            if (payload > RimRunnerPipeFrame.MaxFrameBytes)
            {
                throw new ReportFlowException(
                    $"{op} 请求编码后 {payload} 字节，超过实例的单帧上限 {RimRunnerPipeFrame.MaxFrameBytes} 字节"
                    + (oversizeNote == null ? "" : "；主要占用者：" + oversizeNote));
            }

            if (!Send(frame)) return Reply.NoLink;

            TimeSpan limit = timeout ?? DefaultTimeout;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (limit != Timeout.InfiniteTimeSpan) bounded.CancelAfter(limit);
            try
            {
                return await waiter.Task.WaitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return Reply.TimedOut;
            }
        }
        finally
        {
            pending.TryRemove(id, out _);
            if (uses)
            {
                Interlocked.Decrement(ref usingRequests);
                Touch();
            }
        }
    }

    internal LiveHold? Hold
    {
        get
        {
            lock (holdGate) return LiveHold.Live(holdUntil, DateTimeOffset.Now);
        }
    }

    internal LiveHold ExtendHold(TimeSpan grace)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        lock (holdGate)
        {
            DateTimeOffset until = now + grace;
            if (holdUntil is not { } previous || previous < until) holdUntil = until;
            return LiveHold.Live(holdUntil, now)!;
        }
    }

    internal void Touch()
    {
        lock (sinkGate)
        {
            if (idleSince != null) idleSince = DateTime.UtcNow;
        }

        UpdateIdle();
    }

    internal IDisposable InUse()
    {
        Interlocked.Increment(ref usingRequests);
        UpdateIdle();
        return new UseScope(this);
    }

    private sealed class UseScope(InstanceLink link) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            Interlocked.Decrement(ref link.usingRequests);
            link.Touch();
        }
    }

    private void UpdateIdle()
    {
        bool idle = state is { InGame: true, RunState: "idle" } && Volatile.Read(ref usingRequests) == 0;
        lock (sinkGate)
        {
            if (!idle) idleSince = null;
            else idleSince ??= DateTime.UtcNow;
        }
    }

    private void Beat()
    {
        if (watchdog.Check() is string stalled)
        {
            Stop($"实例管道{stalled}，已持续 {RimRunnerPipeProtocol.WatchdogSeconds} 秒，由编排进程停止");
            return;
        }

        Send(RimRunnerPipeFrame.EncodeObject(writer =>
                writer.WriteString(RimRunnerPipeProtocol.Event, RimRunnerPipeProtocol.Events.Heartbeat)),
            RimRunnerPipeProtocol.Events.Heartbeat, counted: false);
        if (Hold != null) Touch();
        else UpdateIdle();
        if (IdleLimit > TimeSpan.Zero && IdleFor is TimeSpan idle && idle >= IdleLimit)
            Stop($"空闲满 {IdleExit.Minutes(IdleLimit.TotalMinutes)}，由编排进程停止");
    }

    internal void ServeShardClaims(ShardGroupQueue queue, int shard) =>
        Volatile.Write(ref shardClaims, new ShardServing(queue, shard));

    private void AnswerShardClaim(JsonElement root)
    {
        if (!root.TryGetProperty(RimRunnerPipeProtocol.Keys.Ask, out JsonElement ask) || !ask.TryGetInt64(out long askId))
            return;
        ShardServing? serving = Volatile.Read(ref shardClaims);
        Send(RimRunnerPipeFrame.EncodeObject(writer =>
        {
            writer.WriteString(RimRunnerPipeProtocol.Event, RimRunnerPipeProtocol.Events.Answer);
            writer.WriteNumber(RimRunnerPipeProtocol.Keys.Ask, askId);
            if (serving != null)
                writer.WriteNumber(RimRunnerPipeProtocol.Keys.Ordinal, serving.Queue.Take(serving.Shard));
            else
                writer.WriteString(RimRunnerPipeProtocol.Keys.Error, "本实例未在执行分片组");
        }));
    }

    private void SendFilmFed(string? error) => Send(RimRunnerPipeFrame.EncodeObject(writer =>
    {
        writer.WriteString(RimRunnerPipeProtocol.Event, RimRunnerPipeProtocol.Events.FilmFed);
        if (error != null) writer.WriteString(RimRunnerPipeProtocol.Keys.Error, error);
    }));

    private bool Send(byte[] frame, string? latestKey = null, bool counted = true)
    {
        if (Closed) return false;
        if (outbox.Enqueue(frame, latestKey, counted)) return true;
        if (outbox.Overflowed)
            Stop($"发往实例的写队列已满 {OutboxBytes / (1024 * 1024)} MiB，实例不再读管道，由编排进程停止");
        return false;
    }

    internal void Stop(string reason) => Shutdown(reason, byUs: true);

    private void Shutdown(string reason, bool byUs, bool exitedBeforeConnect = false)
    {
        Timer? timer;
        lock (lifecycleGate)
        {
            if (Interlocked.Exchange(ref closing, 1) != 0) return;
            this.exitedBeforeConnect = exitedBeforeConnect && !connected;
            closedReason = reason;
            timer = heartbeat;
            heartbeat = null;
        }

        InstanceLinks.Remove(this);
        Volatile.Write(ref closed, 1);
        lifetime.Cancel();
        timer?.Dispose();
        outbox.Complete();
        Output.Complete();
        Film.Close();
        try
        {
            server.Dispose();
        }
        catch (IOException)
        {
        }

        foreach (TaskCompletionSource<Reply> waiter in pending.Values) waiter.TrySetResult(Reply.NoLink);
        Signal();
        CliLog.Destination stopperLog = CliLog.CurrentDestination;
        _ = DetachedTask.Run(() =>
        {
            try
            {
                CliLog.Within(stopperLog, () => Settle(reason, byUs));
                recorded.TrySetResult();
                Output.Drained.Wait();
            }
            finally
            {
                recorded.TrySetResult();
                settled.TrySetResult();
            }
        });
    }

    private void Settle(string reason, bool byUs)
    {
        Process? process = game;
        int? pid = process?.Id;
        bool killed = false;
        int? exitCode = null;
        if (process != null)
        {
            try
            {
                if (!process.WaitForExit(ExitGrace))
                {
                    killed = true;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(ExitGrace);
                }

                if (process.HasExited) exitCode = process.ExitCode;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                                  or System.ComponentModel.Win32Exception)
            {
            }
        }

        lock (sinkGate)
        {
            sinks.Clear();
            EndTail();
        }

        if (pid == null) return;
        string recorded = byUs
            ? reason
            : SelfExitReason(playerLogPath) is string own
                ? "实例自行退出：" + own
                : killed
                    ? reason + $"；游戏进程 {ExitGrace.TotalSeconds:0} 秒内未退出，由编排进程停止"
                    : "游戏进程已退出，引擎未写下退出原因" + (exitCode is int code ? $"，退出码 {code}" : "");
        exitReason = recorded;
        if (recordStop == null) return;
        try
        {
            recordStop(recorded);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or StoreException)
        {
        }
    }

    internal static string? SelfExitReason(string playerLogPath)
    {
        string tail;
        try
        {
            using FileStream stream = new(playerLogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - PlayerLogTailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            tail = reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        int at = tail.LastIndexOf(SelfExitMark, StringComparison.Ordinal);
        if (at < 0) return null;
        string rest = tail[(at + SelfExitMark.Length)..];
        int end = rest.IndexOfAny(['\r', '\n']);
        return (end < 0 ? rest : rest[..end]).Trim();
    }

    internal void Follow(IEngineLogSink sink)
    {
        lock (sinkGate)
        {
            if (!sinks.Contains(sink)) sinks.Add(sink);
            EndTail();
        }
    }

    private void EndTail()
    {
        if (tail != null && tailBacklog.Length > 0) AppendTail("");
        tail?.Watch?.Seal(tail.Path);
        tail = null;
        tailBacklog = "";
    }

    private void AppendTail(string entries)
    {
        LogTail current = tail!;
        string text = tailBacklog + entries;
        try
        {
            File.AppendAllText(current.Path, text, new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            tailBacklog = text;
            if (text.Length > TailBacklogChars)
            {
                tail = null;
                tailBacklog = "";
            }

            return;
        }

        tailBacklog = "";
        if (current.Watch != null)
            foreach (string fault in current.Watch.Lines(current.Path, text))
                tailBacklog += CliLog.TailFaultEntry(fault, current.Tag);
    }

    internal void Unfollow(IEngineLogSink sink, Func<LogTail?>? handOver = null)
    {
        lock (sinkGate)
        {
            sinks.Remove(sink);
            if (sinks.Count == 0 && handOver != null && !Closed && handOver() is LogTail handed)
            {
                tail = handed;
                lastLog = handed.Path;
            }
        }
    }

    internal string? TailPath
    {
        get
        {
            lock (sinkGate) return tail?.Path;
        }
    }

    internal string? PreviousLog
    {
        get
        {
            lock (sinkGate) return lastLog;
        }
    }

    private void Deliver(JsonElement root)
    {
        DateTime at = root.TryGetProperty(RimRunnerPipeProtocol.Keys.At, out JsonElement stamp)
                      && stamp.ValueKind == JsonValueKind.String && stamp.TryGetDateTimeOffset(out DateTimeOffset when)
            ? when.LocalDateTime
            : DateTime.Now;
        string? level = root.TryGetProperty(RimRunnerPipeProtocol.Keys.Level, out JsonElement levelValue)
            ? levelValue.GetString()
            : null;
        string text = root.TryGetProperty(RimRunnerPipeProtocol.Keys.Text, out JsonElement textValue)
            ? textValue.GetString() ?? ""
            : "";
        IReadOnlyList<string> lines = FormatLog(level, text);
        lock (sinkGate)
        {
            foreach (IEngineLogSink sink in sinks) sink.EngineLines(at, lines);
            if (sinks.Count > 0 || tail == null) return;
            if (!File.Exists(tail.Path))
            {
                tail = null;
                tailBacklog = "";
                return;
            }

            AppendTail(CliLog.EngineEntries(at, lines, tail.Tag));
        }
    }

    internal static IReadOnlyList<string> FormatLog(string? level, string text)
    {
        string[] parts = text.TrimEnd('\r', '\n').Split('\n');
        var lines = new List<string>(parts.Length);
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index].TrimEnd('\r');
            lines.Add(index == 0
                ? (level == RimRunnerPipeProtocol.Keys.LevelWarn ? CliLog.WarnTag : "") + part
                : "  " + part);
        }

        return lines;
    }

    private void Signal() => Interlocked.Exchange(ref changed, NewSignal()).TrySetResult();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    internal sealed record Reply(int Status, string? Body)
    {
        internal const int NoLinkStatus = 0;
        internal const int TimedOutStatus = -1;
        internal static readonly Reply NoLink = new(NoLinkStatus, null);
        internal static readonly Reply TimedOut = new(TimedOutStatus, null);
        internal bool Ok => Status is >= 200 and < 300;

        internal string ErrorText()
        {
            if (Status == NoLinkStatus) return "实例未连接";
            if (Status == TimedOutStatus) return "实例未在时限内回包";
            try
            {
                using JsonDocument document = JsonDocument.Parse(Body ?? "null");
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out JsonElement error)
                    && error.ValueKind == JsonValueKind.String)
                    return error.GetString()!;
            }
            catch (JsonException)
            {
            }

            return $"状态 {Status}";
        }
    }

    internal sealed record Handshake(int Pid, string? Mode, Dictionary<string, string>? AssemblyMvids,
        IReadOnlyList<string>? ProductCapabilities, IReadOnlyList<string>? LoadedPackIds,
        Dictionary<string, string>? ModAssemblyShortfalls = null)
    {
        internal static Handshake Parse(JsonElement root)
        {
            Dictionary<string, string>? TextMap(string key)
            {
                if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
                    return null;
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String)
                        map[property.Name] = property.Value.GetString()!;
                return map;
            }

            Dictionary<string, string>? mvids = TextMap(RimRunnerPipeProtocol.Keys.AssemblyMvids);

            return new Handshake(
                root.TryGetProperty(RimRunnerPipeProtocol.Keys.Pid, out JsonElement pid) && pid.TryGetInt32(out int value)
                    ? value
                    : 0,
                PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.Mode),
                mvids,
                PipeJson.TextArrayOrNull(root, RimRunnerPipeProtocol.Keys.ProductCapabilities),
                PipeJson.TextArrayOrNull(root, RimRunnerPipeProtocol.Keys.LoadedPackIds),
                TextMap(RimRunnerPipeProtocol.Keys.ModAssemblyShortfalls));
        }
    }

    internal sealed record RunStatus(bool InGame, string? RunState, string? ActiveCommand, string? ActiveRunId,
        string? StartupFault, bool ProfilerArmed = false, int Drives = 0)
    {
        internal static RunStatus Parse(JsonElement root) => new(
            root.TryGetProperty(RimRunnerPipeProtocol.Keys.InGame, out JsonElement inGame)
            && inGame.ValueKind == JsonValueKind.True,
            PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.RunState),
            PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.ActiveCommand),
            PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.ActiveRunId),
            PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.StartupFault),
            root.TryGetProperty(RimRunnerPipeProtocol.Keys.ProfilerArmed, out JsonElement armed)
            && armed.ValueKind == JsonValueKind.True,
            root.TryGetProperty(RimRunnerLiveContract.Drive.Record, out JsonElement drive)
            && drive.ValueKind == JsonValueKind.Object
            && drive.TryGetProperty(RimRunnerLiveContract.Drive.RecordCount, out JsonElement count)
            && count.TryGetInt32(out int drives) ? drives : 0);
    }

    internal sealed record GpuReading(long? DeviceLocal, long? Offloaded, int? Uncached = null)
    {
        internal long? Demand => DeviceLocal is long local && Offloaded is long offloaded ? local + offloaded : null;

        internal static GpuReading Parse(JsonElement root) =>
            new(Bytes(root, RimRunnerPipeProtocol.Keys.DeviceLocalBytes), Bytes(root, RimRunnerPipeProtocol.Keys.OffloadedBytes),
                Bytes(root, RimRunnerPipeProtocol.Keys.UncachedTextures) is long uncached ? (int)uncached : null);

        private static long? Bytes(JsonElement root, string key) =>
            root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long bytes) && bytes >= 0 ? bytes : null;
    }

    internal sealed record Pulse(string Body, DateTime ReceivedAt, long? Frame, DateTime FrameChangedAt,
        double? IdleSeconds = null)
    {
        internal static Pulse Next(Pulse? previous, JsonElement root, DateTime now)
        {
            long? frame = root.TryGetProperty(RimRunnerPipeProtocol.Keys.Frame, out JsonElement value)
                          && value.TryGetInt64(out long number)
                ? number
                : null;
            double? idle = root.TryGetProperty(RimRunnerPipeProtocol.Keys.IdleSeconds, out JsonElement seconds)
                           && seconds.TryGetDouble(out double parsed)
                ? parsed
                : null;
            DateTime changedAt = previous != null && previous.Frame == frame ? previous.FrameChangedAt : now;
            return new Pulse(root.GetRawText(), now, frame, changedAt, idle);
        }
    }
}

internal sealed record InstanceRuntime(
    DateTimeOffset? LaunchedAt = null,
    DateTimeOffset? GameStartedAt = null,
    DateTimeOffset? LoadedAt = null,
    string? LaunchRunId = null,
    string? CurrentRunId = null,
    string? ContentFingerprint = null,
    string? TextureTier = null,
    string? Experiments = null,
    string? EnvChecksGreenExcludes = null,
    string? StartupRejection = null);

internal interface IEngineLogSink
{
    void EngineLines(DateTime at, IReadOnlyList<string> lines);
}

internal static class InstanceLinks
{
    private static readonly ConcurrentDictionary<int, InstanceLink> ByNumber = new();

    internal static InstanceLink? Get(int number) => ByNumber.TryGetValue(number, out InstanceLink? link) ? link : null;

    internal static IReadOnlyCollection<InstanceLink> All => [.. ByNumber.Values];

    internal static int Count => ByNumber.Count;

    internal static InstanceLink Open(int number, TimeSpan idleLimit, string playerLogPath, string outputRoot,
        Action<string>? recordStop, bool survivesDrain = false)
    {
        InstanceLink link = InstanceLink.Create(number, idleLimit, playerLogPath, outputRoot, recordStop, survivesDrain);
        while (true)
        {
            if (ByNumber.TryGetValue(number, out InstanceLink? previous))
            {
                previous.Stop("同一登记下另启动实例");
                ByNumber.TryRemove(KeyValuePair.Create(number, previous));
                continue;
            }

            if (ByNumber.TryAdd(number, link)) return link;
        }
    }

    internal static void Remove(InstanceLink link) => ByNumber.TryRemove(KeyValuePair.Create(link.Number, link));

    internal static Task<InstanceLink.Reply> RequestAsync(int number, string op,
        Action<Utf8JsonWriter>? parameters = null, TimeSpan? timeout = null, CancellationToken token = default,
        string? oversizeNote = null) =>
        Get(number) is { } link
            ? link.RequestAsync(op, parameters, timeout, token, oversizeNote)
            : RoutedRequests.RequestAsync(number, op, parameters, timeout);

    internal static InstanceLink.Reply Request(int number, string op,
        Action<Utf8JsonWriter>? parameters = null, TimeSpan? timeout = null) =>
        RequestAsync(number, op, parameters, timeout).GetAwaiter().GetResult();

    internal static InstanceLink.Reply RequestLatestProgress(int number, TimeSpan? timeout = null)
    {
        InstanceLink.Reply reply = Request(number, RimRunner.RimRunnerPipeProtocol.Ops.Progress,
            writer => writer.WriteBoolean(RimRunner.RimRunnerLiveContract.Progress.LatestSegment, true), timeout);
        return reply.Status == 400 ? Request(number, RimRunner.RimRunnerPipeProtocol.Ops.Progress, timeout: timeout) : reply;
    }

    internal static IReadOnlySet<string> TailPaths() =>
        ByNumber.Values.Select(link => link.TailPath).Where(path => path != null)
            .Select(path => CommandContext.FullPath(path!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

internal static class PipeJson
{
    internal static string? TextOrNull(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static IReadOnlyList<string>? TextArrayOrNull(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToList()
            : null;
}
