using System.IO.Pipes;
using System.Text.Json;

namespace RimRunnerCli;

internal sealed class LeaseObserver : Leases.IBackend
{
    private static readonly TimeSpan Wait = CoordinatorHost.Grace + TimeSpan.FromSeconds(10);
    private const int ConnectAttemptMs = 500;

    private readonly Func<bool> pipeListed;
    private readonly Func<List<string>> executors;
    private readonly string pipeName;

    internal LeaseObserver(string? pipeName = null, Func<List<string>>? executors = null)
    {
        this.pipeName = pipeName ?? CoordinatorHost.PipeName;
        this.executors = executors ?? (() => CoordinatorHost.EnumerateExecutors());
        pipeListed = () => File.Exists(@"\\.\pipe\" + this.pipeName);
    }

    public long Executor => -1;

    public bool Lock(int command, string section, TimeSpan timeout) =>
        throw new InvalidOperationException("只读命令不持机器级临界区：" + section);

    public void Unlock(int command, string section)
    {
    }

    public IReadOnlyList<Coordinator.Row> List(string table) =>
        Peek("peek", writer => writer.WriteString("table", table),
            body => (IReadOnlyList<Coordinator.Row>)[.. body.GetProperty("rows").EnumerateArray().Select(CoordinatorHost.ReadRow)],
            []);

    internal JsonElement? Machine() => Peek<JsonElement?>("machine", null, body => body.Clone(), null);

    internal RpcChannel.Reply? Route(int number, string instanceOp, string? parameters, TimeSpan timeout) =>
        Exchange<RpcChannel.Reply?>(RoutedRequests.PeekRoute, writer =>
        {
            writer.WriteNumber("number", number);
            writer.WriteString("instanceOp", instanceOp);
            if (parameters != null)
            {
                writer.WritePropertyName("parameters");
                writer.WriteRawValue(parameters);
            }
            writer.WriteNumber("instanceTimeoutMs", (long)timeout.TotalMilliseconds);
            writer.WriteNumber("timeoutMs", (long)(timeout + RoutedRequests.Margin).TotalMilliseconds);
        }, timeout + RoutedRequests.Margin + RoutedRequests.Margin, reply => reply, null, retryCall: false);

    private T Peek<T>(string op, Action<Utf8JsonWriter>? args, Func<JsonElement, T> read, T none) =>
        Exchange(op, args, Wait, reply => reply.Ok
            ? read(reply.Body)
            : throw new StoreException($"全机协调无法答复只读请求：{reply.Error}"), none, retryCall: true);

    private T Exchange<T>(string op, Action<Utf8JsonWriter>? args, TimeSpan callTimeout, Func<RpcChannel.Reply, T> read, T none,
        bool retryCall)
    {
        DateTime deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            if (!pipeListed() && executors().Count == 0) return none;
            NamedPipeClientStream stream = OrchestratorPipe.CreateClient(pipeName);
            bool sent = false;
            try
            {
                stream.Connect(ConnectAttemptMs);
                using RpcChannel channel = new RpcChannel(stream,
                    (_, _) => Task.FromResult(RpcChannel.Reply.Failure(400, "只读连接不收请求"))).Start();
                sent = true;
                return read(channel.Call(op, args, callTimeout));
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or ChannelClosedException)
            {
                if (sent && !retryCall)
                    throw new StoreException($"全机协调未答复 {op}：{exception.Message}");
                if (DateTime.UtcNow >= deadline)
                    throw new StoreException($"全机协调 {Wait.TotalSeconds:0} 秒内未答复：无法读取机器级租约（{exception.Message}）");
                Thread.Sleep(100);
            }
            finally
            {
                stream.Dispose();
            }
        }
    }

    public bool Put(int command, string table, string id, string payload) =>
        throw new InvalidOperationException("只读命令不写机器级租约：" + table);

    public Coordinator.Row? Claim(int command, string table, string id, string payload) =>
        throw new InvalidOperationException("只读命令不写机器级租约：" + table);

    public bool Delete(int command, string table, string id) => false;

    public void ReleaseCommand(int command)
    {
    }
}
