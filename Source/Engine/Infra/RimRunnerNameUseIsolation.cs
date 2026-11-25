namespace RimRunner;

internal static class RimRunnerNameUseIsolation
{
    [ThreadStatic] private static int depth;

    private static readonly GenderPossibility[] SolidNameLists =
        (GenderPossibility[])Enum.GetValues(typeof(GenderPossibility));

    internal static bool Active => depth > 0;

    internal static int SolidNameDraws { get; private set; }

    internal static IDisposable Enter()
    {
        depth++;
        return new Scope();
    }

    internal static void BeforeSolidNameDraw()
    {
        foreach (GenderPossibility gp in SolidNameLists)
        {
            PawnNameDatabaseSolid.GetListForGender(gp).Sort(CompareContent);
        }

        SolidNameDraws++;
    }

    private static int CompareContent(NameTriple a, NameTriple b)
    {
        int c = string.CompareOrdinal(a.First, b.First);
        if (c != 0)
        {
            return c;
        }

        c = string.CompareOrdinal(a.Last, b.Last);
        return c != 0 ? c : string.CompareOrdinal(a.Nick, b.Nick);
    }

    private sealed class Scope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            depth--;
        }
    }
}

[HarmonyPatch(typeof(NameUseChecker), nameof(NameUseChecker.AllPawnsNamesEverUsed), MethodType.Getter)]
internal static class Patch_NameUseChecker_AllPawnsNamesEverUsed_RimRunnerIsolation
{
    private static void Postfix(ref IEnumerable<Name> __result)
    {
        if (RimRunnerNameUseIsolation.Active)
        {
            __result = [];
        }
    }
}

[HarmonyPatch(typeof(PawnBioAndNameGenerator), nameof(PawnBioAndNameGenerator.TryGetRandomUnusedSolidName))]
internal static class Patch_PawnBioAndNameGenerator_TryGetRandomUnusedSolidName_RimRunnerIsolation
{
    private static void Prefix()
    {
        if (RimRunnerNameUseIsolation.Active)
        {
            RimRunnerNameUseIsolation.BeforeSolidNameDraw();
        }
    }
}
