namespace RimRunner;

internal static class RimRunnerOldAgeInjurySkip
{
    private static int depth;

    internal static bool Active => depth > 0;

    internal static int SkippedCalls { get; private set; }

    internal static int PassedCalls { get; private set; }

    internal static void NoteCall(bool skipped)
    {
        if (skipped) SkippedCalls++;
        else PassedCalls++;
    }

    internal static IDisposable Enter()
    {
        RimRunnerEnvChecks.NoteUse(RimRunnerFacility.OldAgeInjurySkip);
        depth++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            depth--;
        }
    }
}

[HarmonyPatch(typeof(AgeInjuryUtility), nameof(AgeInjuryUtility.GenerateRandomOldAgeInjuries))]
internal static class Patch_AgeInjuryUtility_GenerateRandomOldAgeInjuries_RimRunnerSkip
{
    private static bool Prefix()
    {
        bool skip = RimRunnerOldAgeInjurySkip.Active;
        RimRunnerOldAgeInjurySkip.NoteCall(skip);
        return !skip;
    }
}
