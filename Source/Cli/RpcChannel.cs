using System.Collections.Concurrent;
using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

internal sealed class RpcChannel : IDisposable
{
    internal const int MaxFrameBytes = InstanceLink.MaxIncomingFrameBytes;

    internal const int TooLarge = 413;

    internal sealed record Reply(int Status, string? Error, JsonElement Body)
    {
        internal bool Ok => Status == 200;

        internal static Reply Success(Action<Utf8JsonWriter>? body = null) => new(200, null, Encode(body));

        internal static Reply Failure(int status, string error) => new(status, error, default);

        private static JsonElement Encode(Action<Utf8JsonWriter>? body)
        {
            if (body == null) return default;
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                body(writer);
                writer.WriteEndObject();
            }

            using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }
    }

    internal delegate Task<Reply> Handler(string op, JsonElement args);

    private readonly Stream stream;
    private readonly Handler handler;
    private readonly Lock writeGate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Reply>> pending = new();
    private readonly CancellationTokenSource closed = new();
    private int lastId;
    private Task? reading;

    internal RpcChannel(Stream stream, Handler handler)
    {
        this.stream = stream;
        this.handler = handler;
    }

    internal RpcChannel Start()
    {
        reading ??= Task.Run(ReadLoop);
        return this;
    }

    internal Task Completion => reading ?? throw new InvalidOperationException("尚未开始读取");

    internal CancellationToken Closed => closed.Token;

    internal bool IsClosed => closed.IsCancellationRequested;

    internal async Task<Reply> CallAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken token = default)
    {
        int id = Interlocked.Increment(ref lastId);
        var answer = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = answer;
        try
        {
            if (!Send(RimRunnerPipeFrame.EncodeObject(writer =>
                {
                    writer.WriteNumber(RimRunnerPipeFrame.ProtocolKey, RimRunnerPipeFrame.Protocol);
                    writer.WriteNumber("id", id);
                    writer.WriteString("op", op);
                    writer.WriteStartObject("args");
                    args?.Invoke(writer);
                    writer.WriteEndObject();
                })))
                throw new ChannelClosedException();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closed.Token);
            if (timeout is { } limit) linked.CancelAfter(limit);
            try
            {
                return await answer.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (closed.IsCancellationRequested && !token.IsCancellationRequested)
            {
                throw new ChannelClosedException();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"对端 {timeout?.TotalSeconds:0} 秒内未应答 {op}");
            }
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    internal Reply Call(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken token = default) =>
        CallAsync(op, args, timeout, token).GetAwaiter().GetResult();

    private bool Send(byte[] frame)
    {
        lock (writeGate)
        {
            if (closed.IsCancellationRequested) return false;
            try
            {
                stream.Write(frame);
                stream.Flush();
                return true;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                                  or OperationCanceledException)
            {
                closed.Cancel();
                return false;
            }
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (await RimRunnerPipeFrame.ReadAsync(stream, MaxFrameBytes, closed.Token).ConfigureAwait(false)
                   is { } frame)
            {
                using (frame)
                {
                    JsonElement root = frame.Root;
                    if (root.TryGetProperty("re", out JsonElement re))
                    {
                        if (pending.TryGetValue(re.GetInt32(), out TaskCompletionSource<Reply>? answer))
                        {
                            answer.TrySetResult(new Reply(root.GetProperty("status").GetInt32(),
                                root.TryGetProperty("error", out JsonElement error) ? error.GetString() : null,
                                root.TryGetProperty("body", out JsonElement body) ? body.Clone() : default));
                        }

                        continue;
                    }

                    int id = root.GetProperty("id").GetInt32();
                    if (RimRunnerPipeFrame.ProtocolMismatch(root) is string mismatch)
                    {
                        Answer(id, Reply.Failure(400, mismatch));
                        continue;
                    }

                    string op = root.GetProperty("op").GetString() ?? "";
                    JsonElement args = root.TryGetProperty("args", out JsonElement value) ? value.Clone() : default;
                    _ = Task.Run(async () =>
                    {
                        Reply reply;
                        try
                        {
                            reply = await handler(op, args).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            reply = Reply.Failure(500, $"{op} 处理时抛出：{exception.GetType().Name}: {exception.Message}");
                        }

                        Answer(id, reply);
                    });
                }
            }
        }
        catch (Exception exception) when (exception is IOException or RimRunnerPipeProtocolException
                                              or ObjectDisposedException or OperationCanceledException
                                              or JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
        finally
        {
            closed.Cancel();
            foreach (TaskCompletionSource<Reply> answer in pending.Values) answer.TrySetException(new ChannelClosedException());
        }
    }

    private void Answer(int id, Reply reply)
    {
        byte[] frame = EncodeAnswer(id, reply);
        if (frame.Length > MaxFrameBytes)
            frame = EncodeAnswer(id, Reply.Failure(TooLarge, $"回包 {frame.Length} 字节，超过连接上限 {MaxFrameBytes} 字节"));
        Send(frame);
    }

    private static byte[] EncodeAnswer(int id, Reply reply) =>
        RimRunnerPipeFrame.EncodeObject(writer =>
        {
            writer.WriteNumber("re", id);
            writer.WriteNumber("status", reply.Status);
            if (reply.Error != null) writer.WriteString("error", reply.Error);
            if (reply.Body.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("body");
                reply.Body.WriteTo(writer);
            }
        });

    public void Dispose()
    {
        closed.Cancel();
        stream.Dispose();
    }
}

internal sealed class ChannelClosedException() : Exception("连接在答复之前断开");
