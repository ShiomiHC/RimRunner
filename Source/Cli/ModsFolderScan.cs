namespace RimRunnerCli;

public static class ModsFolderScan
{
    public sealed record ModsEntry(string Path, string? PackageId, string RealPath);

    public static IReadOnlyList<ModsEntry> Scan(string modsFolder)
    {
        IReadOnlyList<RimTooling.RimWorld.ModFolderEntry> entries =
            RimTooling.RimWorld.ModDirectoryScanner.Scan(CommandContext.FullPath(modsFolder));
        return [.. entries.Select(entry => new ModsEntry(entry.Path, entry.About?.PackageId, entry.RealPath))];
    }
}
