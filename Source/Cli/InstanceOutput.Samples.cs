using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RimRunner;

namespace RimRunnerCli;

internal sealed partial class InstanceOutput
{
    private SampleStore.Kind? sampleKind;
    private SqliteSampleStore? sqliteStore;

    internal void AcceptSample(JsonElement root, ReadOnlyMemory<byte> line)
    {
        string caseId = PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.CaseId) ?? "";
        if (!queue.Writer.TryWrite(new SampleWrite(caseId, line)))
            Record(new Fault(SamplePath(caseId), "实例管道已关闭，未写入", Quiet: false));
    }

    internal void UseSamples(SampleStore.Kind kind) => queue.Writer.TryWrite(new SamplesUse(kind));

    private void UseSampleKind(SampleStore.Kind kind)
    {
        if (sampleKind != kind) CloseSampleStore();
        sampleKind = kind;
    }

    private void ApplySample(SampleWrite sample)
    {
        try
        {
            switch (sampleKind)
            {
                case SampleStore.Kind.Jsonl:
                    byte[] bytes = new byte[sample.Line.Length + 1];
                    sample.Line.Span.CopyTo(bytes);
                    bytes[^1] = (byte)'\n';
                    Apply(new Write(SamplePath(sample.CaseId), bytes, Append: true, Quiet: false));
                    break;
                case SampleStore.Kind.Sqlite:
                    sqliteStore ??= SqliteSampleStore.Open(SampleStore.DatabaseOf(Root));
                    sqliteStore.Append(sample.CaseId, Encoding.UTF8.GetString(sample.Line.Span));
                    break;
                default:
                    Record(new Fault(SamplePath(sample.CaseId), "本批未指定读数库的格式，样本未写入", Quiet: false));
                    break;
            }
        }
        catch (Exception exception) when (IsWriteFault(exception) || exception is SqliteException)
        {
            Record(new Fault(SamplePath(sample.CaseId), exception.Message, Quiet: false));
        }
    }

    private void CloseSampleStore()
    {
        sqliteStore?.Dispose();
        sqliteStore = null;
    }

    private string SamplePath(string caseId) =>
        sampleKind == SampleStore.Kind.Sqlite
            ? RimRunnerDataLayout.SamplesDirName + RimRunnerOutputPath.Separator + SqliteSampleStore.FileName
            : RimRunnerDataLayout.SamplesDirName + RimRunnerOutputPath.Separator + caseId + SampleStore.JsonlExtension;

    private sealed record SamplesUse(SampleStore.Kind Kind) : Item;

    private sealed record SampleWrite(string CaseId, ReadOnlyMemory<byte> Line) : Item;
}
