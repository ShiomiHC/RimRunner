using System.Text.Json;
using System.Threading;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    internal const int BurstHeartbeatTicks = 300;

    private static volatile RimRunnerSamplePulse publishedSamples = RimRunnerSamplePulse.Empty;
    private static long publishedBurstTick = -1;
    private static long publishedBurstUtcTicks;

    internal static void PublishSamples(string caseId, List<RimRunnerSampleRecord> samples)
    {
        RimRunnerSamplePulseSegment[] segments = new RimRunnerSamplePulseSegment[samples.Count];
        RimRunnerSamplePulse previous = publishedSamples;
        for (int i = 0; i < samples.Count; i++)
        {
            if (string.Equals(previous.CaseId, caseId, StringComparison.Ordinal)
                && i < previous.Segments.Length
                && ReferenceEquals(previous.Segments[i].Record, samples[i]))
            {
                segments[i] = previous.Segments[i];
                continue;
            }

            segments[i] = new RimRunnerSamplePulseSegment(
                samples[i], Find.TickManager?.TicksGame ?? -1, DateTime.UtcNow.Ticks);
        }

        publishedSamples = new RimRunnerSamplePulse(caseId, segments);
    }

    internal static void PublishBurstHeartbeat(int ticksGame)
    {
        Volatile.Write(ref publishedBurstUtcTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref publishedBurstTick, ticksGame);
    }

    internal static void ClearBurstHeartbeat()
    {
        Volatile.Write(ref publishedBurstTick, -1);
    }

    internal static void BeginCasePulse(string caseId, List<RimRunnerSampleRecord> samples)
    {
        PublishSamples(caseId, samples);
        Volatile.Write(ref publishedBurstTick, -1);
    }

    internal static void ClearRunPulse()
    {
        publishedSamples = RimRunnerSamplePulse.Empty;
        Volatile.Write(ref publishedBurstTick, -1);
    }

    private static void AppendRunPulse(Utf8JsonWriter writer, bool latestOnly)
    {
        long nowUtc = DateTime.UtcNow.Ticks;
        long burstTick = Volatile.Read(ref publishedBurstTick);
        if (burstTick < 0)
        {
            writer.WriteNull(RimRunnerLiveContract.Progress.Burst);
        }
        else
        {
            long burstUtc = Volatile.Read(ref publishedBurstUtcTicks);
            writer.WriteStartObject(RimRunnerLiveContract.Progress.Burst);
            writer.WriteNumber(RimRunnerLiveContract.Progress.GameTick, burstTick);
            writer.WriteNumber(RimRunnerLiveContract.Progress.AgeMs, AgeMs(nowUtc, burstUtc));
            writer.WriteEndObject();
        }

        RimRunnerSamplePulse pulse = publishedSamples;
        if (pulse.CaseId == null)
        {
            writer.WriteNull(RimRunnerLiveContract.Progress.Samples);
            return;
        }

        writer.WriteStartObject(RimRunnerLiveContract.Progress.Samples);
        writer.WriteString(RimRunnerLiveContract.Progress.CurrentCase, pulse.CaseId);
        writer.WriteNumber(RimRunnerLiveContract.Progress.SegmentCount, pulse.Segments.Length);
        writer.WriteStartArray(RimRunnerLiveContract.Progress.Segments);
        foreach (RimRunnerSamplePulseSegment segment in latestOnly ? pulse.Segments.Skip(pulse.Segments.Length - 1) : pulse.Segments)
        {
            RimRunnerSampleRecord record = segment.Record;
            writer.WriteStartObject();
            writer.WriteString("series", record.Series);
            writer.WriteString("segment", record.Segment);
            writer.WriteNumber("windowTicks", record.WindowTicks);
            writer.WriteNumber(RimRunnerLiveContract.Progress.GameTick, segment.GameTick);
            writer.WriteNumber(RimRunnerLiveContract.Progress.AgeMs, AgeMs(nowUtc, segment.UtcTicks));
            writer.WriteStartArray("rows");
            foreach (RimRunnerSampleRow row in record.Rows)
            {
                writer.WriteStartObject();
                writer.WriteString("key", row.Key);
                writer.WriteStartObject("values");
                foreach (KeyValuePair<string, double> pair in row.Values)
                {
                    writer.WriteNumber(pair.Key, Math.Round(pair.Value, 10));
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static long AgeMs(long nowUtcTicks, long thenUtcTicks)
    {
        return Math.Max(0L, (nowUtcTicks - thenUtcTicks) / TimeSpan.TicksPerMillisecond);
    }

    private sealed class RimRunnerSamplePulse
    {
        internal static readonly RimRunnerSamplePulse Empty =
            new(null, []);

        internal readonly string? CaseId;
        internal readonly RimRunnerSamplePulseSegment[] Segments;

        internal RimRunnerSamplePulse(string? caseId, RimRunnerSamplePulseSegment[] segments)
        {
            CaseId = caseId;
            Segments = segments;
        }
    }

    private sealed class RimRunnerSamplePulseSegment
    {
        internal readonly RimRunnerSampleRecord Record;
        internal readonly int GameTick;
        internal readonly long UtcTicks;

        internal RimRunnerSamplePulseSegment(RimRunnerSampleRecord record, int gameTick, long utcTicks)
        {
            Record = record;
            GameTick = gameTick;
            UtcTicks = utcTicks;
        }
    }
}
