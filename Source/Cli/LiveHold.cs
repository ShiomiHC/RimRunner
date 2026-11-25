using System.Text.Json;

namespace RimRunnerCli;

public sealed record LiveHold(DateTimeOffset Until)
{
    internal static LiveHold? Live(DateTimeOffset? until, DateTimeOffset now) =>
        until is { } at && at > now ? new LiveHold(at) : null;

    internal string Describe() => $"live 保留到 {Until.ToLocalTime():HH:mm:ss}";

    internal void Write(Utf8JsonWriter writer) => writer.WriteString("holdUntil", Until);

    internal static LiveHold? Read(JsonElement instance) =>
        instance.TryGetProperty("holdUntil", out JsonElement until) && until.TryGetDateTimeOffset(out DateTimeOffset at)
            ? Live(at, DateTimeOffset.Now)
            : null;
}

internal static class LiveHolds
{
    public const int GraceDefault = 3;

    internal static LiveHold? Extend(int number, double minutes, out bool owned)
    {
        if (InstanceLinks.Get(number) is { } link)
        {
            owned = true;
            return link.ExtendHold(TimeSpan.FromMinutes(minutes));
        }

        (owned, LiveHold? hold) = RoutedRequests.Hold(number, minutes);
        return hold;
    }
}
