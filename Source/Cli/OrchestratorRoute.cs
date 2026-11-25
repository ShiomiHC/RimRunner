namespace RimRunnerCli;

internal static class OrchestratorRoute
{
    internal static bool Forwards(ParsedCommand parsed) =>
        !IsolatedRoot.Active && InPackage && CommandTable.RouteOf(parsed.Operation) == CommandRoute.Forward;

    internal static bool ForwardsIfRunning(ParsedCommand parsed) =>
        !IsolatedRoot.Active && InPackage && CommandTable.RouteOf(parsed.Operation) == CommandRoute.ForwardIfRunning;

    internal static bool InPackage => File.Exists(Path.Combine(AppContext.BaseDirectory, EnginePackage.ManifestFileName));
}
