namespace RimRunnerCli;

internal static class FirstSaveBuild
{
    internal enum Verdict
    {
        Present,
        FirstBuild,
        Missing,
    }

    public static Verdict Judge(string productRoot, OrchestratorDescriptor descriptor, string? modList,
        string? saveOwner, out string savePath) =>
        Judge(productRoot, descriptor, modList, saveOwner, out savePath, out _);

    public static Verdict Judge(string productRoot, OrchestratorDescriptor descriptor, string? modList,
        string? saveOwner, out string savePath, out string? ledgerOwner)
    {
        ledgerOwner = null;
        savePath = Path.Combine(descriptor.ResolveSavesDir(productRoot),
            descriptor.BaselineSaveNameFor(saveOwner) + ".rws");
        if (File.Exists(savePath))
            return Verdict.Present;
        if (HasLedger(productRoot, descriptor, modList))
            ledgerOwner = modList;
        else if (HasLedger(productRoot, descriptor, saveOwner))
            ledgerOwner = saveOwner;
        else
            return Verdict.FirstBuild;
        return Verdict.Missing;
    }

    private static bool HasLedger(string productRoot, OrchestratorDescriptor descriptor, string? modList)
    {
        var roots = new List<string> { productRoot };
        if (TreeScope.Of(productRoot).MainProductRoot is string mainProductRoot)
            roots.Add(mainProductRoot);
        foreach (string stem in new[] { RimRunner.RimRunnerDataLayout.PassRateLedgerStem, RimRunner.RimRunnerLedgerFold.SavePrintsStem })
        {
            if (RimRunner.RimRunnerDataLayout.LedgerFileName(stem, modList) is not string name)
                return true;
            foreach (string root in roots)
            {
                if (File.Exists(Path.Combine(descriptor.ResolveOutputDir(root), RimRunner.RimRunnerDataLayout.HistoryDirName, name)))
                    return true;
            }
        }

        return false;
    }
}
