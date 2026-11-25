using System.IO;

namespace RimRunner;

internal static class RimRunnerOutput
{
    internal static void Replace(string relativePath, byte[] content, bool quiet = false) =>
        Push(relativePath, content, append: false, quiet);

    internal static void Append(string relativePath, byte[] content) =>
        Push(relativePath, content, append: true, quiet: false);

    internal static void Sample(string caseId, byte[] line)
    {
        if (!RimRunnerPipeLink.Connected)
        {
            throw new InvalidOperationException("本实例未接入实例管道，用例 " + caseId + " 的样本无处提交");
        }

        RimRunnerPipeLink.Emit(RimRunnerPipeProtocol.Events.Sample,
            writer => writer.WriteString(RimRunnerPipeProtocol.Keys.CaseId, caseId), binary: line);
    }

    internal static void Report(string runId, string archivePath, string? latestPath, (int Recent, int Failed)? keep,
        byte[] json)
    {
        Check(archivePath);
        if (latestPath != null)
        {
            Check(latestPath);
        }

        RimRunnerPipeLink.Emit(RimRunnerPipeProtocol.Events.Report, writer =>
        {
            writer.WriteString(RimRunnerPipeProtocol.Keys.RunId, runId);
            writer.WriteString(RimRunnerPipeProtocol.Keys.Path, archivePath);
            if (latestPath != null)
            {
                writer.WriteString(RimRunnerPipeProtocol.Keys.Latest, latestPath);
            }

            if (keep is (int recent, int failed))
            {
                writer.WriteNumber(RimRunnerPipeProtocol.Keys.KeepRecent, recent);
                writer.WriteNumber(RimRunnerPipeProtocol.Keys.KeepFailed, failed);
            }
        }, binary: json);
    }

    internal static void Ledger(string relativePath, string label, int cap,
        IReadOnlyDictionary<string, List<string>> cells, IReadOnlyList<string> drop)
    {
        Check(relativePath);
        RimRunnerPipeLink.Emit(RimRunnerPipeProtocol.Events.Ledger, writer =>
        {
            writer.WriteString(RimRunnerPipeProtocol.Keys.Path, relativePath);
            writer.WriteString(RimRunnerPipeProtocol.Keys.Label, label);
            writer.WriteNumber(RimRunnerPipeProtocol.Keys.Cap, cap);
            writer.WriteStartObject(RimRunnerPipeProtocol.Keys.Cells);
            foreach (KeyValuePair<string, List<string>> pair in cells)
            {
                writer.WriteStartArray(pair.Key);
                foreach (string cell in pair.Value)
                {
                    writer.WriteStringValue(cell);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            if (drop.Count > 0)
            {
                writer.WriteStartArray(RimRunnerPipeProtocol.Keys.Drop);
                foreach (string key in drop)
                {
                    writer.WriteStringValue(key);
                }

                writer.WriteEndArray();
            }
        });
    }

    internal static string Absolute(string relativePath) =>
        Path.Combine(RimRunnerPaths.OutputRoot,
            relativePath.Replace(RimRunnerOutputPath.Separator, Path.DirectorySeparatorChar));

    private static void Check(string relativePath)
    {
        if (RimRunnerOutputPath.Fault(relativePath) is string fault)
        {
            throw new InvalidOperationException("产出路径无效：" + fault);
        }

        if (!RimRunnerPipeLink.Connected)
        {
            throw new InvalidOperationException("本实例未接入实例管道，产出 " + relativePath + " 无处提交");
        }
    }

    private static void Push(string relativePath, byte[] content, bool append, bool quiet)
    {
        Check(relativePath);
        RimRunnerPipeLink.Emit(RimRunnerPipeProtocol.Events.Output, writer =>
        {
            writer.WriteString(RimRunnerPipeProtocol.Keys.Path, relativePath);
            if (append)
            {
                writer.WriteBoolean(RimRunnerPipeProtocol.Keys.Append, true);
            }

            if (quiet)
            {
                writer.WriteBoolean(RimRunnerPipeProtocol.Keys.Quiet, true);
            }
        }, binary: content);
    }
}
