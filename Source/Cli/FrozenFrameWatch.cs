using System.Text.Json;

namespace RimRunnerCli;

public sealed class FrozenFrameWatch
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(5);

    private DateTime? stuckSince;
    private long? stuckFrame;
    private long? stuckTick;
    private long? stuckMarks;

    public TimeSpan? Observe(DateTime now, string? body)
    {
        if (body == null) return null;
        long? frame;
        long? tick;
        long? marks;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            frame = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty(RimRunner.RimRunnerPipeProtocol.Keys.Frame, out JsonElement f)
                    && f.TryGetInt64(out long frameNumber)
                ? frameNumber
                : null;
            tick = frame != null
                   && root.TryGetProperty(RimRunner.RimRunnerLiveContract.Progress.Burst, out JsonElement burst)
                   && burst.ValueKind == JsonValueKind.Object
                   && burst.TryGetProperty(RimRunner.RimRunnerLiveContract.Progress.GameTick, out JsonElement t)
                   && t.TryGetInt64(out long value)
                ? value
                : null;
            marks = frame != null
                    && root.TryGetProperty(RimRunner.RimRunnerPipeProtocol.Keys.LoadMarks, out JsonElement m)
                    && m.TryGetInt64(out long count)
                ? count
                : null;
        }
        catch (JsonException)
        {
            frame = null;
            tick = null;
            marks = null;
        }

        if (frame == null)
        {
            stuckSince = null;
            return null;
        }

        if (stuckSince == null || stuckFrame != frame || stuckTick != tick || stuckMarks != marks)
        {
            stuckSince = now;
            stuckFrame = frame;
            stuckTick = tick;
            stuckMarks = marks;
            return null;
        }

        TimeSpan stuck = now - stuckSince.Value;
        return stuck >= Threshold ? stuck : null;
    }
}
