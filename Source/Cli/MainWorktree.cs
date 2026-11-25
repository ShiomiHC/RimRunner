namespace RimRunnerCli;

internal static class MainWorktree
{
    public static bool IsWorktree(string productRoot) => File.Exists(NearestDotGit(productRoot) ?? "");

    private static string? NearestDotGit(string productRoot)
    {
        for (string? dir = CommandContext.FullPath(productRoot); dir != null; dir = Path.GetDirectoryName(dir))
        {
            string dotGit = Path.Combine(dir, ".git");
            if (File.Exists(dotGit) || Directory.Exists(dotGit)) return dotGit;
        }

        return null;
    }

    public static string? GitTreeRoot(string productRoot) =>
        NearestDotGit(productRoot) is string dotGit ? Path.GetDirectoryName(dotGit) : null;

    public static string? Of(string productRoot)
    {
        string? dotGit = NearestDotGit(productRoot);
        if (dotGit == null || !File.Exists(dotGit))
        {
            return null;
        }

        string gitTreeRoot = Path.GetDirectoryName(dotGit)!;

        const string prefix = "gitdir:";
        string? line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
        if (line == null || !line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string gitDir = CommandContext.FullPath(Path.Combine(gitTreeRoot, line[prefix.Length..].Trim()));
        string commonDirFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonDirFile))
        {
            return null;
        }

        string commonDir = CommandContext.FullPath(
            Path.Combine(gitDir, File.ReadAllText(commonDirFile).Trim()));
        if (!string.Equals(Path.GetFileName(commonDir.TrimEnd(Path.DirectorySeparatorChar)), ".git",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string mainGitRoot = Path.GetDirectoryName(commonDir.TrimEnd(Path.DirectorySeparatorChar))!;
        string mainProductRoot = CommandContext.FullPath(Path.Combine(mainGitRoot, Path.GetRelativePath(gitTreeRoot, CommandContext.FullPath(productRoot))));
        return Directory.Exists(mainProductRoot) ? mainProductRoot : null;
    }
}
