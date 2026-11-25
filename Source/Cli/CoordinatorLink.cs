using System.IO.Pipes;
using System.Text.Json;

namespace RimRunnerCli;

internal sealed class CoordinatorLink : Leases.IBackend, IDisposable
{
    private static readonly TimeSpan ConnectAttempt = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HostDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstancesPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RevokeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan YieldWait = TimeSpan.FromSeconds(3);
    private const string StartupReason = "编排进程启动时协调管道无应答";
    private const string DisconnectedReason = "与上一任的连接断开";

    private readonly ReaderWriterLockSlim connecting = new(LockRecursionPolicy.NoRecursion);
    private readonly Lock mirrorGate = new();
    private readonly Dictionary<(string Table, string Id), (string Payload, int Command)> rows = [];
    private readonly Dictionary<string, int> sections = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim disconnected = new(true);
    private readonly CancellationTokenSource stopping = new();
    private readonly Thread loop;
    private RpcChannel? channel;
    private CoordinatorHost? host;
    private long executor;
    private JsonElement coordinator;
    private bool take;
    private int disposed;

    private readonly string pipeName;

    internal CoordinatorLink(CoordinatorElection.Candidate? self = null, string? pipeName = null)
    {
        Self = self ?? CoordinatorElection.Candidate.Self();
        this.pipeName = pipeName ?? CoordinatorHost.PipeName;
        loop = new Thread(ConnectLoop) { IsBackground = true, Name = "rimrunner-coordinator-link" };
    }

    internal Action<Utf8JsonWriter>? WriteInstances { get; set; }

    internal Func<JsonElement, Task<RpcChannel.Reply>>? Routed { get; set; }

    internal Action<int, string>? Revoke { get; set; }

    public long Executor => Interlocked.Read(ref executor);

    internal JsonElement Coordinator
    {
        get
        {
            lock (mirrorGate) return coordinator;
        }
    }

    internal CoordinatorElection.Candidate Self { get; }

    internal bool IsCoordinator => Volatile.Read(ref host) != null;

    internal CoordinatorHost? Host => Volatile.Read(ref host);

    internal void Start()
    {
        loop.Start();
        _ = Task.Run(ReportInstancesLoop);
    }

    internal bool RequestTakeover(TimeSpan timeout)
    {
        if (!Self.Dev) return false;
        if (IsCoordinator) return true;
        lock (mirrorGate) take = true;
        DateTime deadline = DateTime.UtcNow + timeout;
        if (connecting.TryEnterReadLock(timeout))
        {
            try
            {
                Volatile.Read(ref channel)?.Dispose();
            }
            finally
            {
                connecting.ExitReadLock();
            }
        }

        while (DateTime.UtcNow < deadline)
        {
            if (IsCoordinator && !disconnected.IsSet) return true;
            Thread.Sleep(50);
        }

        return IsCoordinator;
    }

    private void ConnectLoop()
    {
        bool everConnected = false;
        while (!stopping.IsCancellationRequested)
        {
            RpcChannel? current = Volatile.Read(ref channel);
            if (current != null && !current.IsClosed)
            {
                current.Completion.Wait(TimeSpan.FromSeconds(1));
                continue;
            }

            current?.Dispose();
            disconnected.Set();
            connecting.EnterWriteLock();
            try
            {
                Establish(everConnected ? DisconnectedReason : StartupReason);
                everConnected = true;
            }
            finally
            {
                connecting.ExitWriteLock();
            }
        }
    }

    private void Establish(string reason)
    {
        DateTime hostAfter = reason == StartupReason ? DateTime.UtcNow : DateTime.UtcNow + HostDelay;
        while (!stopping.IsCancellationRequested)
        {
            if (Volatile.Read(ref host) == null && DateTime.UtcNow >= hostAfter) TryHost(reason);

            NamedPipeClientStream stream = OrchestratorPipe.CreateClient(pipeName);
            try
            {
                stream.Connect(ConnectAttempt);
            }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
                stream.Dispose();
                Thread.Sleep(50);
                continue;
            }

            var opened = new RpcChannel(stream, Handle).Start();
            switch (Hello(opened))
            {
                case HelloResult.Joined:
                    Volatile.Write(ref channel, opened);
                    disconnected.Reset();
                    return;
                case HelloResult.Yield:
                    opened.Dispose();
                    reason = "上一任移交";
                    hostAfter = DateTime.UtcNow;
                    DateTime giveUp = DateTime.UtcNow + YieldWait;
                    while (Volatile.Read(ref host) == null && DateTime.UtcNow < giveUp && !stopping.IsCancellationRequested)
                    {
                        if (TryHost(reason)) break;
                        Thread.Sleep(50);
                    }

                    break;
                default:
                    opened.Dispose();
                    Thread.Sleep(100);
                    break;
            }
        }
    }

    private bool TryHost(string reason)
    {
        bool taking;
        lock (mirrorGate) taking = take;
        if (CoordinatorHost.TryStart(Self, taking, reason, pipeName) is not { } started) return false;
        Volatile.Write(ref host, started);
        _ = started.Stopped.ContinueWith(_ => Interlocked.CompareExchange(ref host, null, started),
            TaskScheduler.Default);
        return true;
    }

    private enum HelloResult
    {
        Joined,
        Yield,
        Failed,
    }

    private HelloResult Hello(RpcChannel opened)
    {
        bool taking;
        lock (mirrorGate) taking = take;
        try
        {
            RpcChannel.Reply hello = opened.Call("hello", writer =>
            {
                Self.WriteFields(writer);
                writer.WriteNumber("pid", Environment.ProcessId);
                if (taking) writer.WriteBoolean("take", true);
            }, HelloTimeout);
            if (!hello.Ok) return HelloResult.Failed;
            if (hello.Body.TryGetProperty("yield", out JsonElement yield) && yield.GetBoolean()) return HelloResult.Yield;
            Interlocked.Exchange(ref executor, hello.Body.GetProperty("executor").GetInt64());
            lock (mirrorGate) coordinator = hello.Body.GetProperty("coordinator").Clone();

            RpcChannel.Reply report = opened.Call("report", WriteReport, HelloTimeout);
            if (!report.Ok) return HelloResult.Failed;
            List<int> revoked = [.. report.Body.GetProperty("revoked").EnumerateArray().Select(value => value.GetInt32())];
            if (revoked.Count > 0) Abandon(revoked);
            return HelloResult.Joined;
        }
        catch (Exception exception) when (exception is ChannelClosedException or TimeoutException)
        {
            return HelloResult.Failed;
        }
    }

    private void WriteReport(Utf8JsonWriter writer)
    {
        lock (mirrorGate)
        {
            writer.WriteStartArray("rows");
            foreach (((string table, string id), (string payload, int command)) in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("table", table);
                writer.WriteString("id", id);
                writer.WriteString("payload", payload);
                writer.WriteNumber("command", command);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("sections");
            foreach ((string section, int command) in sections)
            {
                writer.WriteStartObject();
                writer.WriteString("section", section);
                writer.WriteNumber("command", command);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        WriteInstancesProperty(writer);
    }

    private void WriteInstancesProperty(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("instances");
        WriteInstances?.Invoke(writer);
        writer.WriteEndArray();
    }

    private void Abandon(IReadOnlyList<int> commands)
    {
        lock (mirrorGate)
        {
            foreach ((string, string) key in rows.Where(pair => commands.Contains(pair.Value.Command)).Select(pair => pair.Key).ToList())
                rows.Remove(key);
            foreach (string section in sections.Where(pair => commands.Contains(pair.Value)).Select(pair => pair.Key).ToList())
                sections.Remove(section);
        }

        foreach (int command in commands)
            Revoke?.Invoke(command, "全机协调换任时，本命令持有的租约与新一任已批出的租约冲突，被作废");
    }

    private async Task<RpcChannel.Reply> Handle(string op, JsonElement args)
    {
        if (op == "routed" && Routed is { } routed) return await routed(args).ConfigureAwait(false);
        return RpcChannel.Reply.Failure(400, "编排进程不接受 " + op);
    }

    private async Task ReportInstancesLoop()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(InstancesPoll, stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Volatile.Read(ref channel) is not { IsClosed: false } current) continue;
            try
            {
                await current.CallAsync("instances", WriteInstancesProperty, CallTimeout).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ChannelClosedException or TimeoutException)
            {
            }
        }
    }

    private T Call<T>(string op, Action<Utf8JsonWriter>? args, TimeSpan budget, Func<RpcChannel.Reply, T> apply)
    {
        DateTime deadline = DateTime.UtcNow + budget + ReconnectBudget;
        while (true)
        {
            TimeSpan left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !connecting.TryEnterReadLock(left))
                throw new StoreException($"全机协调无应答：{(budget + ReconnectBudget).TotalSeconds:0} 秒内未答复 {op}");
            try
            {
                if (Volatile.Read(ref channel) is { IsClosed: false } current
                    && current.Call(op, args, budget + CallTimeout) is { Status: not CoordinatorHost.Resigning } reply)
                {
                    if (!reply.Ok) throw new StoreException($"全机协调拒绝 {op}：{reply.Error}");
                    return apply(reply);
                }
            }
            catch (ChannelClosedException)
            {
            }
            catch (TimeoutException exception)
            {
                throw new StoreException($"全机协调未答复 {op}：{exception.Message}");
            }
            finally
            {
                connecting.ExitReadLock();
            }

            disconnected.Wait(TimeSpan.FromMilliseconds(100));
            Thread.Sleep(20);
        }
    }

    private void Forget(string op, Action<Utf8JsonWriter> args, Action mirror)
    {
        if (!connecting.TryEnterReadLock(ReconnectBudget))
        {
            mirror();
            return;
        }

        try
        {
            mirror();
            if (Volatile.Read(ref channel) is { IsClosed: false } current) current.Call(op, args, CallTimeout);
        }
        catch (Exception exception) when (exception is ChannelClosedException or TimeoutException)
        {
        }
        finally
        {
            connecting.ExitReadLock();
        }
    }

    public bool Lock(int command, string section, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        return Call("lock", writer =>
        {
            writer.WriteNumber("command", command);
            writer.WriteString("section", section);
            writer.WriteNumber("timeoutMs",
                (long)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds));
        }, timeout, reply =>
        {
            bool granted = reply.Body.GetProperty("granted").GetBoolean();
            if (granted)
                lock (mirrorGate) sections[section] = command;
            return granted;
        });
    }

    public void Unlock(int command, string section) =>
        Forget("unlock", writer =>
        {
            writer.WriteNumber("command", command);
            writer.WriteString("section", section);
        }, () =>
        {
            lock (mirrorGate)
            {
                if (sections.TryGetValue(section, out int holder) && holder == command) sections.Remove(section);
            }
        });

    public IReadOnlyList<Coordinator.Row> List(string table) =>
        Call("list", writer => writer.WriteString("table", table), TimeSpan.Zero,
            reply => (IReadOnlyList<Coordinator.Row>)[.. reply.Body.GetProperty("rows").EnumerateArray().Select(CoordinatorHost.ReadRow)]);

    public bool Put(int command, string table, string id, string payload) =>
        Call("put", writer => WriteItem(writer, command, table, id, payload), TimeSpan.Zero, reply =>
        {
            bool written = reply.Body.GetProperty("written").GetBoolean();
            if (written)
            {
                lock (mirrorGate)
                    rows[(table, id)] = (payload, rows.TryGetValue((table, id), out (string, int Command) held) ? held.Command : command);
            }

            return written;
        });

    public Coordinator.Row? Claim(int command, string table, string id, string payload) =>
        Call("claim", writer => WriteItem(writer, command, table, id, payload), TimeSpan.Zero, reply =>
        {
            if (reply.Body.ValueKind == JsonValueKind.Object && reply.Body.TryGetProperty("holder", out JsonElement holder))
                return CoordinatorHost.ReadRow(holder);
            lock (mirrorGate) rows[(table, id)] = (payload, command);
            return (Coordinator.Row?)null;
        });

    public bool Delete(int command, string table, string id)
    {
        bool mine = false;
        Forget("delete", writer =>
        {
            writer.WriteNumber("command", command);
            writer.WriteString("table", table);
            writer.WriteString("id", id);
        }, () =>
        {
            lock (mirrorGate)
            {
                mine = rows.Remove((table, id));
            }
        });
        return mine;
    }

    public void ReleaseCommand(int command) =>
        Forget("release", writer => writer.WriteNumber("command", command), () =>
        {
            lock (mirrorGate)
            {
                foreach ((string, string) key in rows.Where(pair => pair.Value.Command == command).Select(pair => pair.Key).ToList())
                    rows.Remove(key);
                foreach (string section in sections.Where(pair => pair.Value == command).Select(pair => pair.Key).ToList())
                    sections.Remove(section);
            }
        });

    private static void WriteItem(Utf8JsonWriter writer, int command, string table, string id, string payload)
    {
        writer.WriteNumber("command", command);
        writer.WriteString("table", table);
        writer.WriteString("id", id);
        writer.WriteString("payload", payload);
    }

    internal JsonElement Machine() =>
        Call("machine", null, TimeSpan.Zero, reply => reply.Body.Clone());

    internal RpcChannel.Reply Route(int number, string op, Action<Utf8JsonWriter>? args, TimeSpan timeout) =>
        Call("route", writer =>
        {
            writer.WriteNumber("number", number);
            writer.WriteString("op", op);
            writer.WriteNumber("timeoutMs", (long)timeout.TotalMilliseconds);
            args?.Invoke(writer);
        }, timeout, reply => reply);

    internal bool RevokeRemote(long target, int command, string reason)
    {
        try
        {
            return Call("revoke", writer =>
            {
                writer.WriteNumber("executor", target);
                writer.WriteNumber("command", command);
                writer.WriteString("reason", reason);
            }, RevokeTimeout, _ => true);
        }
        catch (StoreException)
        {
            return false;
        }
    }

    internal RpcChannel.Reply Broadcast(string op, Action<Utf8JsonWriter>? args, TimeSpan timeout) =>
        Call("broadcast", writer =>
        {
            writer.WriteString("op", op);
            args?.Invoke(writer);
        }, timeout, reply => reply);

    internal Task Note(string text)
    {
        if (Volatile.Read(ref channel) is not { IsClosed: false } current) return Task.CompletedTask;
        return current.CallAsync("note", writer => writer.WriteString("text", text), CallTimeout)
            .ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        Volatile.Read(ref host)?.Resign("编排进程退出");
        Volatile.Read(ref channel)?.Dispose();
    }
}
