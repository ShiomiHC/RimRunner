namespace RimRunnerCli;

internal static class CommandRevocation
{
    internal static bool Revoke(Coordinator.Owner owner, string reason)
    {
        if (owner.Executor == Leases.Me.Executor)
        {
            CommandContext.Find(owner.Command)?.Revoke(reason);
            return true;
        }

        return Leases.Link?.RevokeRemote(owner.Executor, owner.Command, reason) == true;
    }

    internal static bool RevokeAll(string reason, Action<string>? log)
    {
        foreach (CommandContext context in CommandContext.RevokeAll(reason, CommandContext.Current))
            log?.Invoke($"[撤销] {context.Label ?? $"命令 {context.Id}"}");
        if (Leases.Link is not { } link) return true;

        RpcChannel.Reply reply;
        try
        {
            reply = link.Broadcast(RoutedRequests.RevokeAll, writer => writer.WriteString("reason", reason), BroadcastTimeout);
        }
        catch (StoreException exception)
        {
            log?.Invoke($"[FAIL] 别的编排进程里的命令无法撤销：{exception.Message}");
            return false;
        }

        bool all = true;
        foreach (System.Text.Json.JsonElement answer in reply.Body.GetProperty("replies").EnumerateArray())
        {
            if (answer.GetProperty("status").GetInt32() != 200)
            {
                log?.Invoke($"[FAIL] 撤销未送达：{answer.GetProperty("error").GetString()}");
                all = false;
                continue;
            }

            foreach (System.Text.Json.JsonElement label in answer.GetProperty("body").GetProperty("revoked").EnumerateArray())
                log?.Invoke($"[撤销] {label.GetString()}");
        }

        return all;
    }

    private static readonly TimeSpan BroadcastTimeout = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10);
}
