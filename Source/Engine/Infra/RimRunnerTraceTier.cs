namespace RimRunner;

internal static class RimRunnerTraceTier
{
    internal static readonly bool On = RimRunnerExperiments.IsOn(RimRunnerExperiments.Trace);
}
