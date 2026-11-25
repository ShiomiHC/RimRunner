namespace RimRunnerCli;

internal sealed class SampleOverlapException(IReadOnlyList<SampleOverlapException.Overlap> overlaps)
    : IOException(Describe(overlaps))
{
    public sealed record Overlap(string Source, string Target, IReadOnlyList<string> RunIds);

    public IReadOnlyList<Overlap> Overlaps { get; } = overlaps;

    private static string Describe(IReadOnlyList<Overlap> overlaps) =>
        StoreException.Detailed("读数库未追加任何行，以下 runId 已在目标里且行与源不匹配：", "[归集冲突]",
            overlaps.SelectMany(overlap => overlap.RunIds.Select(runId =>
                StoreException.Fact($"{overlap.Source} → {overlap.Target}", runId))),
            unit: "个");
}
