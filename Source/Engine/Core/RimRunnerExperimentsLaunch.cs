using Verse;

namespace RimRunner;

internal static partial class RimRunnerExperiments
{
    internal static string Active => field ??= GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.Experiments, out string list)
        ? Join(name => Listed(list, name))
        : "";

    internal static bool IsOn(string name) => Listed(Active, name);
}
