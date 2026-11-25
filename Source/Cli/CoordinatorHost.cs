using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimRunnerCli;

internal sealed partial class CoordinatorHost : IDisposable
{
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OrphanSweep = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan YieldFlush = TimeSpan.FromMilliseconds(300);

    internal const string InstanceTable = "instance";

    private readonly CoordinatorElection.Candidate me;
    private readonly bool taken;
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly List<RpcChannel> channels = [];
    private readonly CoordinatorLog log;
    private string? LogFile => log.FilePath is { } path ? Path.GetFileName(path) : null;
    private NamedPipeServerStream? pending;
    private int disposed;
    private int yielding;

    private readonly string pipeName;

    private CoordinatorHost(CoordinatorElection.Candidate me, bool taken, string pipeName, NamedPipeServerStream first)
    {
        this.me = me;
        this.pipeName = pipeName;
        this.taken = taken;
        pending = first;
        log = new CoordinatorLog(Path.Combine(MachineRoot.Resolve(), "orchestrator-log"), me.Build,
            ObservationMarks.WatchCoordinator());
    }

    internal static string PipeName => $"rimrunner-{OrchestratorPipe.User}-coordinator";

    internal static Func<List<string>> EnumerateExecutors { get; set; } = ExecutorPipes;

    internal static Action<Action<string>> Settle { get; set; } = line => StoppedClaims.Settle(new ClaimStore(), line);

    private static readonly TimeSpan SettleInterval = TimeSpan.FromMinutes(10);
    private int settling;
    private int settleAgain;

    private readonly SemaphoreSlim orphanWake = new(0);
    private readonly HashSet<int> watchedOrphans = [];

    internal Coordinator Core { get; } = new(ready: false);

    internal Task Stopped => stoppedSignal.Task;

    private readonly TaskCompletionSource stoppedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static CoordinatorHost? TryStart(CoordinatorElection.Candidate me, bool taken, string reason, string pipeName)
    {
        NamedPipeServerStream first;
        try
        {
            first = OrchestratorPipe.CreateServer(pipeName, first: true);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return null;
        }

        var host = new CoordinatorHost(me, taken, pipeName, first);
        host.Begin(reason);
        return host;
    }

    private void Begin(string reason)
    {
        List<string> executors = EnumerateExecutors();
        log.Write($"接任全机协调：{CoordinatorElection.Describe(me)}；{reason}；等 {executors.Count} 个编排进程重报"
                  + (executors.Count > 0 ? "（" + string.Join("、", executors) + "）" : ""));
        Core.Await(executors);
        _ = Task.Run(AcceptLoop);
        _ = Task.Run(async () =>
        {
            DateTime start = DateTime.UtcNow;
            try
            {
                await Core.ReadyTask.WaitAsync(Grace, stopping.Token).ConfigureAwait(false);
                log.Write($"各编排进程重报完成，用时 {(DateTime.UtcNow - start).TotalSeconds:0.0} 秒，开始批租约");
            }
            catch (TimeoutException)
            {
                IReadOnlyList<string> late = Core.Unreported;
                Core.MarkReady();
                log.Write($"等满 {Grace.TotalSeconds:0} 秒仍未重报：{string.Join("、", late)}；开始批租约");
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RequestSettle();
            await WatchOrphans().ConfigureAwait(false);
        });
    }

    private void RequestSettle()
    {
        if (stopping.IsCancellationRequested) return;
        Volatile.Write(ref settleAgain, 1);
        if (Interlocked.CompareExchange(ref settling, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                while (Interlocked.Exchange(ref settleAgain, 0) == 1 && !stopping.IsCancellationRequested)
                    Settle(line => log.Write("已停登记收尾：" + line));
            }
            catch (Exception exception)
            {
                log.Write($"已停登记收尾失败：{exception.Message}");
            }
            finally
            {
                Volatile.Write(ref settling, 0);
            }

            if (Volatile.Read(ref settleAgain) == 1) RequestSettle();
        });
    }

    internal static List<string> ExecutorPipes()
    {
        Regex pattern = ExecutorPipePattern(OrchestratorPipe.User);
        try
        {
            return
            [
                .. Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>()
                    .Where(name => pattern.IsMatch(name)).Distinct(StringComparer.OrdinalIgnoreCase),
            ];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static Regex ExecutorPipePattern(string user) =>
        new($"^rimrunner-{Regex.Escape(user)}-[0-9a-f]{{32}}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task AcceptLoop()
    {
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream? current;
            lock (gate) current = pending;
            if (current == null) return;
            try
            {
                await current.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException
                                                  or ObjectDisposedException)
            {
                return;
            }

            NamedPipeServerStream next;
            try
            {
                next = OrchestratorPipe.CreateServer(pipeName, first: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                current.Dispose();
                return;
            }

            lock (gate)
            {
                if (stopping.IsCancellationRequested)
                {
                    next.Dispose();
                    current.Dispose();
                    return;
                }

                pending = next;
            }

            Serve(current);
        }
    }

    private void Serve(NamedPipeServerStream stream)
    {
        Coordinator.ExecutorInfo? info = null;
        RpcChannel? channel = null;
        channel = new RpcChannel(stream, (op, args) => Handle(op, args, channel!, ref info));
        lock (gate)
        {
            if (stopping.IsCancellationRequested)
            {
                channel.Dispose();
                return;
            }

            channels.Add(channel);
        }

        channel.Start();
        _ = channel.Completion.ContinueWith(_ =>
        {
            channel.Dispose();
            lock (gate) channels.Remove(channel);
            if (info is { } joined)
            {
                Core.Leave(joined.Id);
                log.Write($"编排进程断开：{joined.Pipe}（PID {joined.Pid}），名下租约全部释放");
                RequestSettle();
                WakeOrphanWatch();
            }
        }, TaskScheduler.Default);
    }

    private delegate Task<RpcChannel.Reply> Op(JsonElement args);

    private Task<RpcChannel.Reply> Handle(string op, JsonElement args, RpcChannel channel,
        ref Coordinator.ExecutorInfo? info)
    {
        if (op == "hello") return Task.FromResult(Hello(args, channel, ref info));
        if (op is "peek" or "machine") return PeekAsync(op, args);
        if (op == RoutedRequests.PeekRoute) return PeekRouteAsync(args);
        if (info is not { } executor) return Task.FromResult(RpcChannel.Reply.Failure(409, "未握手"));
        long id = executor.Id;
        Coordinator.Owner Owner() => new(id, args.GetProperty("command").GetInt32());
        switch (op)
        {
            case "report":
                return Task.FromResult(Report(executor, args));
            case "lock":
                return LockAsync(Owner(), args);
            case "unlock":
                Core.Unlock(Owner(), args.GetProperty("section").GetString()!);
                return Ok();
            case "list":
                return Task.FromResult(List(args.GetProperty("table").GetString()!));
            case "put":
                return Task.FromResult(Flag("written", Core.Put(Owner(), args.GetProperty("table").GetString()!,
                    args.GetProperty("id").GetString()!, args.GetProperty("payload").GetString()!)));
            case "claim":
                return Task.FromResult(ClaimReply(Core.Claim(Owner(), args.GetProperty("table").GetString()!,
                    args.GetProperty("id").GetString()!, args.GetProperty("payload").GetString()!)));
            case "delete":
                return Task.FromResult(Flag("deleted", Core.Delete(Owner(), args.GetProperty("table").GetString()!,
                    args.GetProperty("id").GetString()!)));
            case "release":
                Core.ReleaseCommand(id, args.GetProperty("command").GetInt32());
                return Ok();
            case "instances":
                Core.UpdateInstances(id, args.GetProperty("instances"));
                return Ok();
            case "route":
                return RouteAsync(executor, args);
            case "broadcast":
                return BroadcastAsync(executor, args);
            case "revoke":
                return RevokeAsync(executor, args);
            case "note":
                Note($"{executor.Pipe}：{args.GetProperty("text").GetString()}");
                return Ok();
            default:
                return Task.FromResult(RpcChannel.Reply.Failure(400, "未知请求：" + op));
        }
    }

    private async Task<RpcChannel.Reply> PeekAsync(string op, JsonElement args)
    {
        try
        {
            await Core.ReadyTask.WaitAsync(Grace + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return RpcChannel.Reply.Failure(503, "接任后的重报尚未收齐");
        }

        return op == "machine" ? Machine() : List(args.GetProperty("table").GetString()!);
    }

    private RpcChannel.Reply Hello(JsonElement args, RpcChannel channel, ref Coordinator.ExecutorInfo? info)
    {
        CoordinatorElection.Candidate newcomer = CoordinatorElection.Candidate.Read(args);
        bool take = args.TryGetProperty("take", out JsonElement value) && value.GetBoolean();
        if (Volatile.Read(ref yielding) != 0)
            return RpcChannel.Reply.Failure(503, "正在移交全机协调");
        if (CoordinatorElection.ShouldYield(me, taken, newcomer, take))
        {
            Interlocked.Exchange(ref yielding, 1);
            Core.Freeze();
            log.Write($"移交全机协调给 {CoordinatorElection.Describe(newcomer)}：{CoordinatorElection.Why(me, newcomer, take)}");
            string successor = CoordinatorElection.Describe(newcomer);
            _ = Task.Delay(YieldFlush).ContinueWith(_ => Resign("移交给 " + successor), TaskScheduler.Default);
            return RpcChannel.Reply.Success(writer => writer.WriteBoolean("yield", true));
        }

        info = Core.Join(newcomer.Pipe, newcomer.Build, newcomer.PackageRoot, newcomer.DevTree, newcomer.BuiltAt,
            args.GetProperty("pid").GetInt32(), channel);
        log.Write($"编排进程连上：{CoordinatorElection.Describe(newcomer)}（PID {info.Pid}）");
        long id = info.Id;
        return RpcChannel.Reply.Success(writer =>
        {
            writer.WriteNumber("executor", id);
            writer.WritePropertyName("coordinator");
            me.Write(writer, taken, LogFile);
        });
    }

    private RpcChannel.Reply Report(Coordinator.ExecutorInfo executor, JsonElement args)
    {
        bool late = Core.Ready;
        var held = new List<Coordinator.Held>();
        foreach (JsonElement row in args.GetProperty("rows").EnumerateArray())
        {
            held.Add(new Coordinator.Held(row.GetProperty("table").GetString()!, row.GetProperty("id").GetString()!,
                row.GetProperty("payload").GetString()!, row.GetProperty("command").GetInt32()));
        }

        var sectionsHeld = new List<(string, int)>();
        foreach (JsonElement section in args.GetProperty("sections").EnumerateArray())
            sectionsHeld.Add((section.GetProperty("section").GetString()!, section.GetProperty("command").GetInt32()));

        IReadOnlyList<int> revoked = Core.Report(executor.Id, held, sectionsHeld, args.GetProperty("instances"),
            LeaseConflicts.Conflicts);
        if (held.Count > 0 || sectionsHeld.Count > 0 || revoked.Count > 0)
        {
            log.Write($"{executor.Pipe} 重报 {held.Count} 项租约、{sectionsHeld.Count} 个临界区"
                      + (late ? "（迟报）" : "")
                      + (revoked.Count > 0 ? $"；与已批的冲突，作废命令 {string.Join("、", revoked)} 名下冲突的租约与临界区，交命令所属的编排进程撤销" : ""));
        }

        return RpcChannel.Reply.Success(writer =>
        {
            writer.WriteStartArray("revoked");
            foreach (int command in revoked) writer.WriteNumberValue(command);
            writer.WriteEndArray();
        });
    }

    private async Task<RpcChannel.Reply> LockAsync(Coordinator.Owner owner, JsonElement args)
    {
        bool granted;
        try
        {
            granted = await Core.LockAsync(owner, args.GetProperty("section").GetString()!,
                TimeSpan.FromMilliseconds(args.GetProperty("timeoutMs").GetInt64()), stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return RpcChannel.Reply.Failure(Resigning, "全机协调正在卸任");
        }

        return RpcChannel.Reply.Success(writer => writer.WriteBoolean("granted", granted));
    }

    internal const int Resigning = 421;

    private RpcChannel.Reply List(string table)
    {
        IReadOnlyList<Coordinator.Row> rows = Core.List(table);
        return RpcChannel.Reply.Success(writer =>
        {
            writer.WriteStartArray("rows");
            foreach (Coordinator.Row row in rows) WriteRow(writer, row);
            writer.WriteEndArray();
        });
    }

    internal static void WriteRow(Utf8JsonWriter writer, Coordinator.Row row)
    {
        writer.WriteStartObject();
        writer.WriteString("id", row.Id);
        writer.WriteString("payload", row.Payload);
        writer.WriteNumber("executor", row.Owner.Executor);
        writer.WriteNumber("command", row.Owner.Command);
        writer.WriteEndObject();
    }

    internal static Coordinator.Row ReadRow(JsonElement row) =>
        new(row.GetProperty("id").GetString()!, row.GetProperty("payload").GetString()!,
            new Coordinator.Owner(row.GetProperty("executor").GetInt64(), row.GetProperty("command").GetInt32()));

    private static RpcChannel.Reply ClaimReply(Coordinator.Row? holder) =>
        RpcChannel.Reply.Success(writer =>
        {
            if (holder == null) return;
            writer.WritePropertyName("holder");
            WriteRow(writer, holder);
        });

    private static RpcChannel.Reply Flag(string name, bool value) =>
        RpcChannel.Reply.Success(writer => writer.WriteBoolean(name, value));

    private static Task<RpcChannel.Reply> Ok() => Task.FromResult(RpcChannel.Reply.Success());

    private RpcChannel.Reply Machine() =>
        RpcChannel.Reply.Success(writer =>
        {
            writer.WritePropertyName("coordinator");
            me.Write(writer, taken, LogFile);
            writer.WriteStartArray("executors");
            foreach (Coordinator.ExecutorInfo executor in Core.Executors)
            {
                writer.WriteStartObject();
                writer.WriteNumber("executor", executor.Id);
                writer.WriteString("pipe", executor.Pipe);
                writer.WriteString("build", executor.Build);
                writer.WriteString("packageRoot", executor.PackageRoot);
                if (executor.DevTree != null) writer.WriteString("devTree", executor.DevTree);
                writer.WriteNumber("pid", executor.Pid);
                writer.WritePropertyName("instances");
                if (executor.Instances.ValueKind == JsonValueKind.Array) executor.Instances.WriteTo(writer);
                else
                {
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("orphans");
            foreach (Coordinator.Row row in Core.List(InstanceTable).Where(row => row.Owner.Executor == 0))
                writer.WriteRawValue(row.Payload);
            writer.WriteEndArray();
        });

    private async Task<RpcChannel.Reply> RouteAsync(Coordinator.ExecutorInfo from, JsonElement args)
    {
        int number = args.GetProperty("number").GetInt32();
        Coordinator.ExecutorInfo? owner = OwnerOf(number);
        if (owner == null) return RpcChannel.Reply.Failure(404, $"实例 {ClaimStore.Named(number)} 不在任何编排进程名下");
        if (owner.Channel == null || owner.Id == from.Id)
            return RpcChannel.Reply.Failure(409, $"实例 {ClaimStore.Named(number)} 就在发请求的编排进程名下");
        string what = args.GetProperty("op").GetString()!;
        if (what is "stop" or "revoke")
            log.Write($"转发 {what}：{from.Pipe} → {owner.Pipe}，实例 {ClaimStore.Named(number)}"
                      + (args.TryGetProperty("reason", out JsonElement reason) ? "：" + reason.GetString() : ""));
        try
        {
            return await owner.Channel.CallAsync("routed", writer =>
            {
                foreach (JsonProperty property in args.EnumerateObject()) property.WriteTo(writer);
            }, TimeSpan.FromMilliseconds(args.TryGetProperty("timeoutMs", out JsonElement t) ? t.GetInt64() : 60_000))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ChannelClosedException or TimeoutException)
        {
            return RpcChannel.Reply.Failure(503, $"实例 {ClaimStore.Named(number)} 所属的编排进程无应答：{exception.Message}");
        }
    }

    private async Task<RpcChannel.Reply> PeekRouteAsync(JsonElement args)
    {
        string instanceOp = args.GetProperty("instanceOp").GetString()!;
        if (!RoutedRequests.PeekRouteOps.Contains(instanceOp))
            return RpcChannel.Reply.Failure(403, $"只读连接不转发 {instanceOp}");
        try
        {
            await Core.ReadyTask.WaitAsync(Grace + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return RpcChannel.Reply.Failure(503, "接任后的重报尚未收齐");
        }

        int number = args.GetProperty("number").GetInt32();
        if (OwnerOf(number) is not { Channel: { } channel })
            return RpcChannel.Reply.Failure(404, $"实例 {ClaimStore.Named(number)} 不在任何编排进程名下");
        try
        {
            return await channel.CallAsync("routed", writer =>
            {
                writer.WriteString("op", RoutedRequests.InstanceRequest);
                writer.WriteNumber("number", number);
                writer.WriteString("instanceOp", instanceOp);
                if (args.TryGetProperty("parameters", out JsonElement parameters))
                {
                    writer.WritePropertyName("parameters");
                    parameters.WriteTo(writer);
                }

                if (args.TryGetProperty("instanceTimeoutMs", out JsonElement ms)) writer.WriteNumber("instanceTimeoutMs", ms.GetInt64());
            }, TimeSpan.FromMilliseconds(args.GetProperty("timeoutMs").GetInt64())).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ChannelClosedException or TimeoutException)
        {
            return RpcChannel.Reply.Failure(503, $"实例 {ClaimStore.Named(number)} 所属的编排进程无应答：{exception.Message}");
        }
    }

    private async Task<RpcChannel.Reply> RevokeAsync(Coordinator.ExecutorInfo from, JsonElement args)
    {
        long target = args.GetProperty("executor").GetInt64();
        int command = args.GetProperty("command").GetInt32();
        string reason = args.GetProperty("reason").GetString()!;
        if (Core.ExecutorOf(target) is not { Channel: { } channel } owner)
            return RpcChannel.Reply.Failure(404, $"实例所属的编排进程 {target} 已退出");
        log.Write($"撤销：{from.Pipe} 撤销 {owner.Pipe} 的命令 {command}：{reason}");
        try
        {
            return await channel.CallAsync("routed", writer =>
            {
                writer.WriteString("op", RoutedRequests.RevokeCommand);
                writer.WriteNumber("command", command);
                writer.WriteString("reason", reason);
            }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ChannelClosedException or TimeoutException)
        {
            return RpcChannel.Reply.Failure(503, $"{owner.Pipe} 无应答：{exception.Message}");
        }
    }

    private async Task<RpcChannel.Reply> BroadcastAsync(Coordinator.ExecutorInfo from, JsonElement args)
    {
        List<Coordinator.ExecutorInfo> targets = [.. Core.Executors.Where(e => e.Id != from.Id && e.Channel != null)];
        log.Write($"广播 {args.GetProperty("op").GetString()}：来自 {from.Pipe}，交 {targets.Count} 个编排进程");
        RpcChannel.Reply[] replies = await Task.WhenAll(targets.Select(async target =>
        {
            try
            {
                return await target.Channel!.CallAsync("routed", writer =>
                {
                    foreach (JsonProperty property in args.EnumerateObject()) property.WriteTo(writer);
                }, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ChannelClosedException or TimeoutException)
            {
                return RpcChannel.Reply.Failure(503, $"{target.Pipe} 无应答：{exception.Message}");
            }
        })).ConfigureAwait(false);
        return RpcChannel.Reply.Success(writer =>
        {
            writer.WriteStartArray("replies");
            foreach (RpcChannel.Reply reply in replies)
            {
                writer.WriteStartObject();
                writer.WriteNumber("status", reply.Status);
                if (reply.Error != null) writer.WriteString("error", reply.Error);
                if (reply.Body.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("body");
                    reply.Body.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private Coordinator.ExecutorInfo? OwnerOf(int number)
    {
        foreach (Coordinator.ExecutorInfo executor in Core.Executors)
        {
            if (executor.Instances.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement instance in executor.Instances.EnumerateArray())
            {
                if (instance.TryGetProperty("number", out JsonElement reported) && reported.TryGetInt32(out int reportedNumber)
                    && reportedNumber == number)
                    return executor;
            }
        }

        return Core.List(InstanceTable).FirstOrDefault(row => row.Id == number.ToString()) is { } row
               && row.Owner.Executor != 0
            ? Core.ExecutorOf(row.Owner.Executor)
            : null;
    }

    private void WakeOrphanWatch() => orphanWake.Release();

    private void WatchOrphanExit(int pid)
    {
        if (!watchedOrphans.Add(pid)) return;
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            WakeOrphanWatch();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using (process) await process.WaitForExitAsync(stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }

            WakeOrphanWatch();
        });
    }

    private async Task WatchOrphans()
    {
        bool first = true;
        var owner = new Coordinator.Owner(0, 0);
        DateTime lastSettle = DateTime.UtcNow;
        while (!stopping.IsCancellationRequested)
        {
            if (DateTime.UtcNow - lastSettle >= SettleInterval)
            {
                lastSettle = DateTime.UtcNow;
                RequestSettle();
            }

            HashSet<string> claimed = [.. Core.List(InstanceTable).Where(row => row.Owner.Executor != 0).Select(row => row.Id)];
            var alive = new HashSet<string>(StringComparer.Ordinal);
            var alivePids = new HashSet<int>();
            foreach (GameProcessProbe.GameProcess process in GameProcessProbe.Enumerate())
            {
                if (GameProcessProbe.ClaimedNumber(process.CommandLine, RimRunner.RimRunnerEngineArgs.Instance) is not int number)
                    continue;
                string id = number.ToString();
                if (claimed.Contains(id)) continue;
                alive.Add(id);
                alivePids.Add(process.Pid);
                WatchOrphanExit(process.Pid);
                string payload = JsonSerializer.Serialize(new { number, pid = process.Pid });
                if (Core.Claim(owner, InstanceTable, id, payload) == null && first)
                    log.Write($"无人认领的游戏进程 PID {process.Pid}（实例 {ClaimStore.Named(number)}）记为占用，等该进程退出");
            }

            foreach (Coordinator.Row row in Core.List(InstanceTable).Where(row => row.Owner.Executor == 0))
            {
                if (alive.Contains(row.Id)) continue;
                Core.Delete(owner, InstanceTable, row.Id);
                log.Write($"实例 {(int.TryParse(row.Id, out int gone) ? ClaimStore.Named(gone) : row.Id)} 的无主游戏进程已退出，释放占用");
                RequestSettle();
            }

            watchedOrphans.IntersectWith(alivePids);
            first = false;
            try
            {
                await orphanWake.WaitAsync(OrphanSweep, stopping.Token).ConfigureAwait(false);
                while (orphanWake.Wait(0)) { }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal void Note(string line) => log.Write(line);

    public void Dispose() => Resign("编排进程退出");

    internal void Resign(string reason)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        log.Close(reason);
        stopping.Cancel();
        List<RpcChannel> open;
        NamedPipeServerStream? waiting;
        lock (gate)
        {
            open = [.. channels];
            waiting = pending;
            pending = null;
        }

        waiting?.Dispose();
        foreach (RpcChannel channel in open) channel.Dispose();
        stoppedSignal.TrySetResult();
    }
}
