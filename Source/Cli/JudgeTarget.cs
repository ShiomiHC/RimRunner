namespace RimRunnerCli;

internal static class JudgeTarget
{
    internal static bool IsRunId(string token) =>
        RimRunner.RimRunnerRunId.IsValid(token) && !File.Exists(token) && !Directory.Exists(token);

    internal static IReadOnlyList<string> FindByRunId(string reportsDir, string runId)
    {
        if (!Directory.Exists(reportsDir)) return [];
        return
        [
            .. Directory.EnumerateFiles(reportsDir, "*.json")
                .Where(path => RimRunner.RimRunnerArchiveName.Parse(path)?.RunId == runId)
                .Order(StringComparer.Ordinal),
        ];
    }
}
