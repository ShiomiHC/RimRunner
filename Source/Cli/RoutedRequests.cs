using System.Text.Json;

namespace RimRunnerCli;

internal static class RoutedRequests
{
    internal const string InstanceRequest = "request";
    private const string StopInstance = "stop";
    private const string HoldInstance = "hold";
    internal const string RevokeCommand = "revoke";
    internal const string RevokeAll = "revoke-all";

    internal const string PeekRoute = "peek-route";

    internal static readonly IReadOnlySet<string> PeekRouteOps = new HashSet<string>(StringComparer.Ordinal)
    {
        RimRunner.RimRunnerPipeProtocol.Ops.Progress,
    };

    internal static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    internal static async Task<RpcChannel.Reply> Handle(JsonElement args)
    {
        string op = args.GetProperty("op").GetString()!;
        switch (op)
        {
            case InstanceRequest:
            {
                int number = args.GetProperty("number").GetInt32();
                Action<Utf8JsonWriter>? parameters = args.TryGetProperty("parameters", out JsonElement given)
                    ? writer =>
                    {
                        foreach (JsonProperty property in given.EnumerateObject()) property.WriteTo(writer);
                    }
                    : null;
                TimeSpan? timeout = args.TryGetProperty("instanceTimeoutMs", out JsonElement ms)
                    ? TimeSpan.FromMilliseconds(ms.GetInt64())
                    : null;
                InstanceLink.Reply reply = InstanceLinks.Get(number) is { } link
                    ? await link.RequestAsync(args.GetProperty("instanceOp").GetString()!, parameters, timeout)
                        .ConfigureAwait(false)
                    : InstanceLink.Reply.NoLink;
                return RpcChannel.Reply.Success(writer =>
                {
                    writer.WriteNumber("status", reply.Status);
                    if (reply.Body != null) writer.WriteString("body", reply.Body);
                });
            }
            case StopInstance:
            {
                int number = args.GetProperty("number").GetInt32();
                int pid = args.GetProperty("pid").GetInt32();
                InstanceLink? link = InstanceLinks.Get(number);
                bool overrideHold = args.TryGetProperty("overrideHold", out JsonElement force) && force.ValueKind == JsonValueKind.True;
                LiveHold? held = link != null && link.Pid == pid && !overrideHold ? link.Hold : null;
                bool stopped = link != null && link.Pid == pid && held == null;
                if (stopped) link!.Stop(args.GetProperty("reason").GetString()!);
                return RpcChannel.Reply.Success(writer =>
                {
                    writer.WriteBoolean("stopped", stopped);
                    held?.Write(writer);
                });
            }
            case HoldInstance:
            {
                int number = args.GetProperty("number").GetInt32();
                InstanceLink? link = InstanceLinks.Get(number);
                bool owned = link != null;
                LiveHold? hold = link?.ExtendHold(TimeSpan.FromMinutes(args.GetProperty("holdMinutes").GetDouble()));
                return RpcChannel.Reply.Success(writer =>
                {
                    writer.WriteBoolean("owned", owned);
                    hold?.Write(writer);
                });
            }
            case RevokeCommand:
            {
                CommandContext? context = CommandContext.Find(args.GetProperty("command").GetInt32());
                context?.Revoke(args.GetProperty("reason").GetString()!);
                return RpcChannel.Reply.Success(writer => writer.WriteBoolean("revoked", context != null));
            }
            case RevokeAll:
            {
                List<CommandContext> revoked = CommandContext.RevokeAll(args.GetProperty("reason").GetString()!, null);
                return RpcChannel.Reply.Success(writer =>
                {
                    writer.WriteStartArray("revoked");
                    foreach (CommandContext context in revoked) writer.WriteStringValue(context.Label ?? $"命令 {context.Id}");
                    writer.WriteEndArray();
                });
            }
            default:
                return RpcChannel.Reply.Failure(400, "编排进程不接受转发的 " + op);
        }
    }

    internal static Task<InstanceLink.Reply> RequestAsync(int number, string op, Action<Utf8JsonWriter>? parameters,
        TimeSpan? timeout)
    {
        if (Leases.Link is not { } coordinator)
        {
            return Leases.Observer is { } observer && PeekRouteOps.Contains(op)
                ? Task.Run(() => PeekRequest(observer, number, op, parameters == null ? null : Encode(parameters),
                    timeout ?? InstanceLink.DefaultTimeout))
                : Task.FromResult(InstanceLink.Reply.NoLink);
        }

        string? encoded = parameters == null ? null : Encode(parameters);
        TimeSpan limit = timeout ?? InstanceLink.DefaultTimeout;
        return Task.Run(() =>
        {
            try
            {
                RpcChannel.Reply reply = coordinator.Route(number, InstanceRequest, writer =>
                {
                    writer.WriteString("instanceOp", op);
                    if (timeout is { } given) writer.WriteNumber("instanceTimeoutMs", (long)given.TotalMilliseconds);
                    if (encoded != null)
                    {
                        writer.WritePropertyName("parameters");
                        writer.WriteRawValue(encoded);
                    }
                }, limit + Margin);
                return new InstanceLink.Reply(reply.Body.GetProperty("status").GetInt32(),
                    PipeJson.TextOrNull(reply.Body, "body"));
            }
            catch (StoreException exception)
            {
                CliLog.Info($"[转发] 实例 {ClaimStore.Named(number)} 的 {op} 未送达：{exception.Message}");
                return InstanceLink.Reply.NoLink;
            }
        });
    }

    private static InstanceLink.Reply PeekRequest(LeaseObserver observer, int number, string op, string? parameters,
        TimeSpan timeout)
    {
        RpcChannel.Reply? reply;
        try
        {
            reply = observer.Route(number, op, parameters, timeout);
        }
        catch (StoreException exception)
        {
            return Refused(504, exception.Message);
        }

        if (reply == null) return InstanceLink.Reply.NoLink;
        if (!reply.Ok) return Refused(reply.Status, $"全机协调未受理只读转发：{reply.Error}");
        return new InstanceLink.Reply(reply.Body.GetProperty("status").GetInt32(), PipeJson.TextOrNull(reply.Body, "body"));
    }

    private static InstanceLink.Reply Refused(int status, string error) =>
        new(status, JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = error }));

    internal static (bool Stopped, LiveHold? Held) Stop(int number, int pid, string reason, bool overrideHold = false)
    {
        LiveHold? Unanswered() => overrideHold ? null : InstanceProbe.Hold(number);
        if (Leases.Link is not { } coordinator) return (false, Unanswered());
        try
        {
            RpcChannel.Reply reply = coordinator.Route(number, StopInstance, writer =>
            {
                writer.WriteNumber("pid", pid);
                writer.WriteString("reason", reason);
                if (overrideHold) writer.WriteBoolean("overrideHold", true);
            }, Margin);
            return (reply.Body.GetProperty("stopped").GetBoolean(), LiveHold.Read(reply.Body));
        }
        catch (StoreException)
        {
            return (false, Unanswered());
        }
    }

    internal static (bool Owned, LiveHold? Hold) Hold(int number, double minutes)
    {
        if (Leases.Link is not { } coordinator) return (false, null);
        try
        {
            RpcChannel.Reply reply = coordinator.Route(number, HoldInstance,
                writer => writer.WriteNumber("holdMinutes", minutes), Margin);
            return (reply.Body.GetProperty("owned").GetBoolean(), LiveHold.Read(reply.Body));
        }
        catch (StoreException)
        {
            return (false, null);
        }
    }

    private static string Encode(Action<Utf8JsonWriter> parameters)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            parameters(writer);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
