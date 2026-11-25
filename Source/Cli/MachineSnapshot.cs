using System.Text.Json;

namespace RimRunnerCli;

internal sealed class MachineSnapshot
{
    internal sealed record Instance(long Executor, string Pipe, string Build, int Number, int? Pid,
        InstanceProbe.PingSnapshot? Ping, DateTimeOffset? StartedAt = null, DateTimeOffset? LaunchedAt = null,
        DateTimeOffset? LoadedAt = null, string? TextureTier = null, long? GpuDemand = null,
        string? CurrentRunId = null);

    private MachineSnapshot(JsonElement coordinator, IReadOnlyList<Instance> instances)
    {
        Coordinator = coordinator;
        Instances = instances;
    }

    internal JsonElement Coordinator { get; }

    internal IReadOnlyList<Instance> Instances { get; }

    internal static MachineSnapshot? Read()
    {
        try
        {
            return Leases.Machine() is { } body ? Parse(body) : null;
        }
        catch (StoreException)
        {
            return null;
        }
    }

    internal static MachineSnapshot? ReadOrThrow() => Leases.Machine() is { } body ? Parse(body) : null;

    internal static Instance? Remote(int number) => Read()?.RemoteOf(number);

    internal Instance? RemoteOf(int number)
    {
        long self = Leases.Link?.Executor ?? -1;
        return Instances.FirstOrDefault(instance => instance.Number == number && instance.Executor != self);
    }

    internal static MachineSnapshot Parse(JsonElement body)
    {
        var instances = new List<Instance>();
        foreach (JsonElement executor in body.GetProperty("executors").EnumerateArray())
        {
            long id = executor.GetProperty("executor").GetInt64();
            string pipe = executor.GetProperty("pipe").GetString()!;
            string build = executor.GetProperty("build").GetString()!;
            foreach (JsonElement instance in executor.GetProperty("instances").EnumerateArray())
            {
                if (!instance.TryGetProperty("number", out JsonElement numberValue) || !numberValue.TryGetInt32(out int number)) continue;
                instances.Add(new Instance(id, pipe, build, number,
                    instance.TryGetProperty("pid", out JsonElement pid) && pid.TryGetInt32(out int value) ? value : null,
                    ReadPing(instance), ReadTime(instance, "startedAt"), ReadTime(instance, "launchedAt"),
                    ReadTime(instance, "loadedAt"), PipeJson.TextOrNull(instance, "textureTier"),
                    instance.TryGetProperty("gpuDemand", out JsonElement demand) && demand.ValueKind == JsonValueKind.Number
                        && demand.TryGetInt64(out long bytes) ? bytes : null,
                    PipeJson.TextOrNull(instance, "currentRunId")));
            }
        }

        return new MachineSnapshot(body.GetProperty("coordinator").Clone(), instances);
    }

    internal static void WriteLocal(Utf8JsonWriter writer)
    {
        foreach (InstanceLink link in InstanceLinks.All)
        {
            writer.WriteStartObject();
            writer.WriteNumber("number", link.Number);
            if (link.Pid is int pid) writer.WriteNumber("pid", pid);
            InstanceRuntime runtime = link.Runtime;
            if (runtime.GameStartedAt is { } startedAt) writer.WriteString("startedAt", startedAt);
            if (runtime.LaunchedAt is { } launchedAt) writer.WriteString("launchedAt", launchedAt);
            if (runtime.LoadedAt is { } loadedAt) writer.WriteString("loadedAt", loadedAt);
            if (runtime.TextureTier != null) writer.WriteString("textureTier", runtime.TextureTier);
            if (runtime.CurrentRunId != null) writer.WriteString("currentRunId", runtime.CurrentRunId);
            if (link.GpuDemand is long demand) writer.WriteNumber("gpuDemand", demand);
            if (InstanceProbe.Snapshot(link) is { } ping)
            {
                writer.WriteBoolean("inGame", ping.InGame);
                if (ping.RunState != null) writer.WriteString("runState", ping.RunState);
                if (ping.Mode != null) writer.WriteString("mode", ping.Mode);
                if (ping.StartupFault != null) writer.WriteString("startupFault", ping.StartupFault);
                if (ping.IdleSeconds is double idle) writer.WriteNumber("idleSeconds", idle);
                if (ping.IdleLimitMinutes is double limit) writer.WriteNumber("idleLimitMinutes", limit);
                ping.Hold?.Write(writer);
                if (ping.Drives > 0) writer.WriteNumber("drives", ping.Drives);
            }

            writer.WriteEndObject();
        }
    }

    private static DateTimeOffset? ReadTime(JsonElement instance, string name) =>
        instance.TryGetProperty(name, out JsonElement value) && value.TryGetDateTimeOffset(out DateTimeOffset time)
            ? time
            : null;

    private static InstanceProbe.PingSnapshot? ReadPing(JsonElement instance)
    {
        if (!instance.TryGetProperty("inGame", out JsonElement inGame)) return null;
        return new InstanceProbe.PingSnapshot(inGame.ValueKind == JsonValueKind.True,
            PipeJson.TextOrNull(instance, "runState"), null, PipeJson.TextOrNull(instance, "startupFault"),
            PipeJson.TextOrNull(instance, "mode"), null,
            instance.TryGetProperty("idleSeconds", out JsonElement idle) ? idle.GetDouble() : null,
            instance.TryGetProperty("idleLimitMinutes", out JsonElement limit) ? limit.GetDouble() : null,
            LiveHold.Read(instance),
            instance.TryGetProperty("drives", out JsonElement drives) && drives.TryGetInt32(out int count) ? count : 0);
    }
}
