namespace RimRunnerCli;

internal static class SiblingLedgers
{
    internal static string[] Names(string ledgerPath)
    {
        string? dir = Path.GetDirectoryName(ledgerPath);
        if (dir == null || !Directory.Exists(dir)) return [];
        return [
            .. Directory.GetFiles(dir, "passrate*.json")
                .Where(path => !string.Equals(Path.GetFileName(path), Path.GetFileName(ledgerPath),
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.GetFileName(path))
        ];
    }

    internal static Func<string, string?>? ForReport(OrchestratorDescriptor descriptor, string productRoot, EngineReport report)
    {
        if (string.IsNullOrEmpty(report.LedgerFile)) return null;
        string ledgerPath = Path.Combine(descriptor.ResolveOutputDir(productRoot),
            RimRunner.RimRunnerDataLayout.HistoryDirName, report.LedgerFile);
        return caseId => OtherLedgersLine(ledgerPath, caseId);
    }

    internal static string? OtherLedgersLine(string ledgerPath, string caseId)
    {
        string? dir = Path.GetDirectoryName(ledgerPath);
        if (dir == null || !Directory.Exists(dir)) return null;
        var bands = new List<string>();
        foreach (string sibling in Names(ledgerPath))
        {
            PassRateLedger.LedgerEntry? entry = null;
            try
            {
                entry = new PassRateLedger(Path.Combine(dir, sibling)).Read()
                    .FirstOrDefault(item => string.Equals(item.CaseId, caseId, StringComparison.Ordinal));
            }
            catch (StoreException)
            {
            }

            if (entry != null) bands.Add($"{sibling} {Program.OutcomeStrip(entry.Outcomes, full: true)}");
        }

        return bands.Count > 0 ? $"其他台账：{string.Join(" / ", bands)}" : null;
    }
}
