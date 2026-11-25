namespace RimRunnerCli;

internal static class PathIdentity
{
    internal static string Normalize(string path) =>
        RimTooling.IO.PathIdentity.Normalize(CommandContext.FullPath(path));

    internal static bool Same(string left, string right) =>
        RimTooling.IO.PathIdentity.Same(CommandContext.FullPath(left), CommandContext.FullPath(right));

    internal static bool IsUnder(string candidatePath, string rootDir, bool includeSelf = false) =>
        RimTooling.IO.PathIdentity.IsUnder(
            CommandContext.FullPath(candidatePath),
            CommandContext.FullPath(rootDir),
            includeSelf);
}
