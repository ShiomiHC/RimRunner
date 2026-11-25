using System.IO;
using System.Text;
using System.Text.Json;

namespace RimRunner;

public static class RimRunnerSampleWriter
{
    internal const string FileExtension = ".jsonl";

    internal const string SealKey = "seal";

    internal static void AppendRecord(string? runId, string caseId, RimRunnerSampleRecord record)
    {
        if (string.IsNullOrEmpty(runId))
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 用例 " + caseId + " 在没有 runId 的窗口里产生读数，无从记到用例名下");
        }

        string line = RimRunnerJson.Write(
            writer => AppendRecordLine(writer, runId, caseId, record), false);
        AppendToFile(caseId, line);
    }

    public static int Seal(RimRunnerReport report, Action<RimRunnerResultEntry, Exception> onFailure)
    {
        int sealedEntries = 0;
        foreach (RimRunnerResultEntry entry in AllEntries(report))
        {
            if (entry.samples.Count == 0) continue;
            try
            {
                SealEntry(report, entry);
                sealedEntries++;
            }
            catch (Exception exception)
            {
                onFailure(entry, exception);
            }
        }

        return sealedEntries;
    }

    private static void SealEntry(RimRunnerReport report, RimRunnerResultEntry entry)
    {
        RimRunnerRunSource.Identity? source = RimRunnerRunSource.Find(report.runId, out string? unavailable);
        string line = RimRunnerJson.Write(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("runId", report.runId);
                writer.WriteString("timestamp", report.timestamp);
                writer.WriteString("caseId", entry.id);
                writer.WriteString(SealKey, entry.status);
                writer.WriteNumber("segments", entry.samples.Count);
                writer.WriteNumber("durationMs", entry.durationMs);
                writer.WriteString("mapSite", MapSiteOf(entry.id));
                writer.WriteStartObject("source");
                if (source == null)
                {
                    writer.WriteString("error", unavailable);
                }
                else
                {
                    writer.WriteString("commit", source.Commit);
                    writer.WriteString("worktree", source.Worktree);
                    writer.WriteStartArray("changes");
                    foreach (string change in source.Changes) writer.WriteStringValue(change);
                    writer.WriteEndArray();
                    writer.WriteNumber("changeCount", source.ChangeCount);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            false);
        AppendToFile(entry.id, line);
    }

    private static string MapSiteOf(string caseId)
    {
        RimRunnerTestCase? testCase = RimRunnerRegistry.FindCase(caseId);
        return testCase is { OnIsolatedMap: true }
            ? "isolated " + RimRunnerIsolatedMap.Describe(testCase.IsolatedMapSize) + " " + testCase.IsolatedMapGround
            : "main";
    }

    private static void AppendToFile(string caseId, string line)
    {
        if (RimRunnerCompatOff.On)
            return;
        _ = FileNameFor(caseId);
        RimRunnerOutput.Sample(caseId, Encoding.UTF8.GetBytes(line));
    }

    private static string FileNameFor(string caseId)
    {
        if (string.IsNullOrWhiteSpace(caseId))
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 样本记录缺少用例 Id，无法写入读数库");
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            if (caseId.IndexOf(invalid) >= 0)
            {
                throw new InvalidOperationException(
                    RimRunnerLog.Prefix + " 用例 Id \"" + caseId
                    + "\" 含文件名非法字符，无法作为读数库文件名");
            }
        }

        return caseId + FileExtension;
    }

    private static void AppendRecordLine(
        Utf8JsonWriter writer, string runId, string caseId, RimRunnerSampleRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("runId", runId);
        writer.WriteString("timestamp", RimRunnerReportWriter.TimestampNow());
        writer.WriteString("caseId", caseId);
        writer.WriteString("series", record.Series);
        writer.WriteString("segment", record.Segment);
        writer.WriteNumber("windowTicks", record.WindowTicks);

        writer.WriteString("modList", RimRunnerPassRate.ResolveModListName());
        WriteStringMap(writer, "assemblyMvids", RimRunnerDefScope.OurAssemblyMvids());
        WriteStringMap(writer, "scope", record.Scope);

        writer.WriteStartArray("rows");
        foreach (RimRunnerSampleRow row in record.Rows)
        {
            writer.WriteStartObject();
            writer.WriteString("key", row.Key);
            WriteNumberMap(writer, "values", row.Values);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteStringMap(
        Utf8JsonWriter writer, string name, IEnumerable<KeyValuePair<string, string>> map)
    {
        writer.WriteStartObject(name);
        foreach (KeyValuePair<string, string> pair in map)
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteNumberMap(
        Utf8JsonWriter writer, string name, IEnumerable<KeyValuePair<string, double>> map)
    {
        writer.WriteStartObject(name);
        foreach (KeyValuePair<string, double> pair in map)
        {
            writer.WriteNumber(pair.Key, Math.Round(pair.Value, 10));
        }

        writer.WriteEndObject();
    }

    private static IEnumerable<RimRunnerResultEntry> AllEntries(RimRunnerReport report)
    {
        foreach (RimRunnerResultEntry entry in report.regressions) yield return entry;
        foreach (RimRunnerResultEntry entry in report.readouts) yield return entry;
    }
}
