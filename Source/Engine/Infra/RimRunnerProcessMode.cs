namespace RimRunner;

internal static class RimRunnerProcessMode
{
    internal static readonly string Current = Resolve();

    internal static bool Showcase => string.Equals(Current, RimRunnerInstanceMode.Showcase, StringComparison.Ordinal);

    internal static bool Film => string.Equals(Current, RimRunnerInstanceMode.Film, StringComparison.Ordinal);

    internal static bool Stages => Showcase || Film;

    private static string Resolve()
    {
        if (CommandIs(RimRunnerEngineArgs.ShowcaseCommand))
        {
            return RimRunnerInstanceMode.Showcase;
        }

        return CommandIs(RimRunnerEngineArgs.FilmCommand)
            ? RimRunnerInstanceMode.Film
            : RimRunnerInstanceMode.Batch;
    }

    private static bool CommandIs(string expected)
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable)
               && GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.Command, out string command)
               && string.Equals(command, expected, StringComparison.Ordinal);
    }
}
