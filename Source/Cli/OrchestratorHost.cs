using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

internal sealed class OrchestratorHost
{
    internal const string CommandOp = "command";
    internal const string OutEvent = "out";
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BuildPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettleWait = TimeSpan.FromSeconds(30);
    private const string DrainReason = "排空：RimRunner 安装目录里已是别的构建，由编排进程停止";
    private static readonly TimeSpan TakeoverWait = CoordinatorHost.Grace + TimeSpan.FromSeconds(10);

    private readonly string build = OrchestratorPipe.Build;
    private readonly string pipeName;
    private readonly string dllPath = Path.Combine(AppContext.BaseDirectory, "rimrunner.dll");
    private readonly Lock gate = new();
    private readonly CancellationTokenSource stopping = new();
    private int active;
    private DateTime lastActivity = DateTime.UtcNow;
    private bool draining;
    private bool unclaimed;
    private readonly string? ownTree = TreeHosts.OwnDevTree();
    private readonly CoordinatorLink link = new();

    private OrchestratorHost() => pipeName = OrchestratorPipe.Name(build);

    internal static int Launch()
    {
        GameLauncher.StartDetached(SelfExecutable.Resolve(), [OrchestratorPipe.HostVerb], AppContext.BaseDirectory,
            new Dictionary<string, string>());
        return 0;
    }

    internal static int Run()
    {
        Console.SetOut(new ConsoleRouter(TextWriter.Null));
        Console.SetError(new ConsoleRouter(TextWriter.Null));
        LoadDependencies();
        return new OrchestratorHost().Serve();
    }

    private static void LoadDependencies()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<System.Reflection.Assembly>([typeof(OrchestratorHost).Assembly]);
        while (pending.TryPop(out System.Reflection.Assembly? assembly))
        {
            foreach (System.Reflection.AssemblyName name in assembly.GetReferencedAssemblies())
            {
                if (!seen.Add(name.FullName)) continue;
                try
                {
                    pending.Push(System.Reflection.Assembly.Load(name));
                }
                catch (Exception exception) when (exception is FileNotFoundException or FileLoadException
                                                      or BadImageFormatException)
                {
                }
            }
        }
    }

    private int Serve()
    {
        NamedPipeServerStream first;
        try
        {
            first = OrchestratorPipe.CreateServer(pipeName, first: true);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return 0;
        }

        link.Revoke = (command, reason) => CommandContext.Find(command)?.Revoke(reason);
        link.WriteInstances = MachineSnapshot.WriteLocal;
        link.Routed = RoutedRequests.Handle;
        Leases.Install(link);
        link.Start();
        Task accept = AcceptLoop(first);
        using var watch = new Timer(_ => Tick(), null, BuildPoll, BuildPoll);
        try
        {
            accept.Wait();
        }
        catch (AggregateException) when (stopping.IsCancellationRequested)
        {
        }

        while (Volatile.Read(ref active) > 0) Thread.Sleep(200);
        List<InstanceLink> links = [.. InstanceLinks.All];
        foreach (InstanceLink link in links) link.Stop(DrainReasonFor(link));
        Task.WaitAll([.. links.Select(link => link.Settled)], SettleWait);
        link.Note(draining ? "编排进程退出：RimRunner 安装目录里已是别的构建，排空完毕"
                : unclaimed ? "编排进程退出：本开发构建所在的树已没有登记"
                : $"编排进程退出：空闲满 {IdleGrace.TotalSeconds:0} 秒")
            .Wait(TimeSpan.FromSeconds(2));
        link.Dispose();
        return 0;
    }

    internal static IReadOnlyList<InstanceLink> DrainStops(IEnumerable<InstanceLink> links, Func<int, bool> owned) =>
        [.. links.Where(link => !link.SurvivesDrain && link.Hold == null && link.IdleFor != null && !owned(link.Number))];

    private static bool OwnedByCommand(int number)
    {
        try
        {
            return ColdStartLock.OwnersOf(new ClaimStore().Root, number).Any;
        }
        catch (StoreException)
        {
            return true;
        }
    }

    internal static bool HoldsDrain(IEnumerable<InstanceLink> links) =>
        links.Any(link => link.SurvivesDrain || link.Hold != null);

    internal static string DrainReasonFor(InstanceLink link) => link.State?.ProfilerArmed == true
        ? DrainReason + "；实例上 perf arm 装的探针与累积序列随之清空"
        : DrainReason;

    private async Task AcceptLoop(NamedPipeServerStream pending)
    {
        while (true)
        {
            try
            {
                await pending.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pending.DisposeAsync().ConfigureAwait(false);
                return;
            }

            NamedPipeServerStream connected = pending;
            pending = OrchestratorPipe.CreateServer(pipeName, first: false);
            lock (gate)
            {
                active++;
                lastActivity = DateTime.UtcNow;
            }

            _ = Task.Run(() => Serve(connected));
        }
    }

    private void Tick()
    {
        bool quiet;
        bool drainingNow;
        lock (gate)
        {
            if (!draining && OrchestratorPipe.MvidOnDisk(dllPath) is string onDisk && onDisk != build)
                draining = true;
            drainingNow = draining;
        }

        if (drainingNow)
        {
            foreach (InstanceLink link in DrainStops(InstanceLinks.All, OwnedByCommand))
                link.Stop(DrainReasonFor(link));
        }

        lock (gate)
        {
            quiet = active == 0 && InstanceLinks.Count == 0;
        }

        bool released = quiet && ownTree != null && TreeHosts.Unclaimed(ownTree);
        lock (gate)
        {
            quiet = active == 0 && InstanceLinks.Count == 0;
            bool idle = quiet && (released || DateTime.UtcNow - lastActivity >= IdleGrace);
            bool drained = draining && !HoldsDrain(InstanceLinks.All);
            if ((drained || idle) && !stopping.IsCancellationRequested && active == 0)
            {
                unclaimed = !draining && released;
                stopping.Cancel();
            }
        }
    }

    private void Serve(NamedPipeServerStream stream)
    {
        try
        {
            using (stream)
            {
                ServeCommand(stream);
            }
        }
        catch (Exception exception) when (exception is IOException or RimRunnerPipeProtocolException
                                              or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            bool last;
            lock (gate)
            {
                active--;
                lastActivity = DateTime.UtcNow;
                last = active == 0;
            }

            if (last && ownTree != null) Tick();
        }
    }

    private void ServeCommand(NamedPipeServerStream stream)
    {
        using RimRunnerPipeFrame? request = RimRunnerPipeFrame.Read(stream, OrchestratorPipe.MaxFrameBytes);
        if (request == null) return;
        var channel = new ClientChannel(stream);
        JsonElement root = request.Root;
        int requestId = root.TryGetProperty("id", out JsonElement idValue) && idValue.ValueKind == JsonValueKind.Number
            ? idValue.GetInt32()
            : 0;
        if (RimRunnerPipeFrame.ProtocolMismatch(root) is string mismatch)
        {
            channel.Respond(requestId, 400, mismatch, null);
            return;
        }

        if (!root.TryGetProperty("op", out JsonElement op) || op.GetString() != CommandOp)
        {
            channel.Respond(requestId, 400, "未知请求：只收 " + CommandOp, null);
            return;
        }

        string[] args = [.. root.GetProperty("args").EnumerateArray().Select(arg => arg.GetString() ?? "")];
        string cwd = root.GetProperty("cwd").GetString() ?? AppContext.BaseDirectory;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty variable in root.GetProperty("env").EnumerateObject())
            environment[variable.Name] = variable.Value.GetString() ?? "";

        var disconnected = new CancellationTokenSource();
        var context = new CommandContext(CommandContext.NextId(), cwd, environment,
            channel.Writer, disconnected.Token, $"rimrunner {string.Join(" ", args)}（{cwd}）");
        _ = Task.Run(() =>
        {
            try
            {
                while (RimRunnerPipeFrame.Read(stream, OrchestratorPipe.MaxFrameBytes) is { } extra) extra.Dispose();
            }
            catch (Exception exception) when (exception is IOException or RimRunnerPipeProtocolException
                                                  or ObjectDisposedException or OperationCanceledException)
            {
            }

            channel.Gone();
            disconnected.Cancel();
        });

        if (CoordinatorElection.WantsTake(name => environment.GetValueOrDefault(name), link.Self)
            && !link.RequestTakeover(TakeoverWait))
        {
            channel.Writer.WriteLine($"[WARN] {CoordinatorElection.TakeVariable}=1：{TakeoverWait.TotalSeconds:0} 秒内本编排进程未接任全机协调，"
                                     + "本命令继续执行，准入由现任全机协调判定");
        }

        int code = ExitCodes.InternalFault;
        var worker = new Thread(() =>
        {
            using (CommandContext.Enter(context))
            {
                code = Program.RunCommand(args);
            }
        })
        {
            Name = $"rimrunner-command-{context.Id}",
        };
        worker.Start();
        worker.Join();
        Leases.ReleaseCommand(context.Id);
        channel.Writer.Flush();
        channel.Respond(requestId, 200, null, code);
    }

    private sealed class ClientChannel
    {
        private readonly Stream stream;
        private readonly Lock writeGate = new();
        private bool gone;

        internal ClientChannel(Stream stream)
        {
            this.stream = stream;
            Writer = new FrameWriter(this);
        }

        internal TextWriter Writer { get; }

        internal void Gone()
        {
            lock (writeGate) gone = true;
        }

        internal void Send(byte[] frame)
        {
            lock (writeGate)
            {
                if (gone) return;
                try
                {
                    stream.Write(frame);
                    stream.Flush();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                                      or OperationCanceledException)
                {
                    gone = true;
                }
            }
        }

        internal void Respond(int id, int status, string? error, int? exitCode) =>
            Send(RimRunnerPipeFrame.EncodeObject(writer =>
            {
                writer.WriteNumber("id", id);
                writer.WriteNumber("status", status);
                if (error != null) writer.WriteString("error", error);
                if (exitCode != null)
                {
                    writer.WriteStartObject("body");
                    writer.WriteNumber("exitCode", exitCode.Value);
                    writer.WriteEndObject();
                }
            }));
    }

    private sealed class FrameWriter(ClientChannel channel) : TextWriter
    {
        private readonly StringBuilder pending = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (pending)
            {
                pending.Append(value);
                if (value == '\n') FlushLocked();
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (pending)
            {
                pending.Append(value);
                FlushLocked();
            }
        }

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        public override void WriteLine(string? value) => Write(value + "\n");

        public override void Flush()
        {
            lock (pending) FlushLocked();
        }

        private void FlushLocked()
        {
            if (pending.Length == 0) return;
            string text = pending.ToString();
            pending.Clear();
            channel.Send(RimRunnerPipeFrame.EncodeObject(writer =>
            {
                writer.WriteString("event", OutEvent);
                writer.WriteString("text", text);
            }));
        }
    }
}
