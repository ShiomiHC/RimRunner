using System.Text;
using System.Text.Json;

namespace RimRunnerCli;

public sealed class ProgressStallWatch(TimeSpan limit, DateTime start)
{
    private readonly TimeSpan limit = limit;
    private DateTime lastAdvance = start;
    private string? fingerprint;
    private string? extra;

    public bool EverAnswered => fingerprint != null;

    public TimeSpan? Observe(DateTime now, string? body, string? extraProgress = null)
    {
        string? current = Fingerprint(body);
        if (current != null)
        {
            if (fingerprint != null && current != fingerprint)
                lastAdvance = now;
            fingerprint = current;
        }

        if (extraProgress != null)
        {
            if (extra != null && extraProgress != extra)
                lastAdvance = now;
            extra = extraProgress;
        }

        TimeSpan idle = now - lastAdvance;
        return idle >= limit ? idle : null;
    }

    internal static string? Fingerprint(string? body)
    {
        if (body == null) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var builder = new StringBuilder();
            foreach (string key in ScalarKeys)
            {
                builder.Append(key).Append('=');
                if (root.TryGetProperty(key, out JsonElement value))
                    builder.Append(value.GetRawText());
                builder.Append(';');
            }

            builder.Append("burst=");
            if (root.TryGetProperty(RimRunner.RimRunnerLiveContract.Progress.Burst, out JsonElement burst))
                builder.Append(TickOf(burst));
            builder.Append(";samples=");
            if (root.TryGetProperty(RimRunner.RimRunnerLiveContract.Progress.Samples, out JsonElement samples)
                && samples.ValueKind == JsonValueKind.Object
                && samples.TryGetProperty(RimRunner.RimRunnerLiveContract.Progress.Segments,
                    out JsonElement segments)
                && segments.ValueKind == JsonValueKind.Array)
            {
                if (samples.TryGetProperty(RimRunner.RimRunnerLiveContract.Progress.SegmentCount, out JsonElement count))
                    builder.Append(count.GetRawText()).Append(':');
                foreach (JsonElement segment in segments.EnumerateArray())
                    builder.Append(TickOf(segment)).Append(',');
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly string[] ScalarKeys =
    [
        RimRunner.RimRunnerLiveContract.Progress.ProgramState,
        RimRunner.RimRunnerPipeProtocol.Keys.RunState,
        RimRunner.RimRunnerPipeProtocol.Keys.LoadMarks,
        RimRunner.RimRunnerPipeProtocol.Keys.ActiveRunId,
        RimRunner.RimRunnerLiveContract.Progress.CasesDone,
        RimRunner.RimRunnerLiveContract.Progress.CasesTotal,
        RimRunner.RimRunnerLiveContract.Progress.CurrentCase,
        RimRunner.RimRunnerLiveContract.Progress.Phase,
        RimRunner.RimRunnerLiveContract.Progress.AssertCount,
        RimRunner.RimRunnerLiveContract.Progress.BodyTicks,
        RimRunner.RimRunnerLiveContract.Progress.BodyFrames
    ];

    private static string TickOf(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(RimRunner.RimRunnerLiveContract.Progress.GameTick, out JsonElement tick)
            ? tick.GetRawText()
            : "";
}
