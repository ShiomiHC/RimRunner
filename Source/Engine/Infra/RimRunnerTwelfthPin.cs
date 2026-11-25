using RimWorld.Planet;

namespace RimRunner;

internal static class RimRunnerTwelfthPin
{
    [ThreadStatic] private static int depth;

    internal static bool Active => depth > 0;

    [field: ThreadStatic]
    internal static int AnchorTicksAbs { get; private set; }

    internal static IDisposable Enter(int anchor)
    {
        Scope scope = new(AnchorTicksAbs);
        depth++;
        AnchorTicksAbs = anchor;
        return scope;
    }

    private sealed class Scope : IDisposable
    {
        private readonly int outerAnchor;
        private bool disposed;

        internal Scope(int outerAnchor)
        {
            this.outerAnchor = outerAnchor;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            depth--;
            AnchorTicksAbs = outerAnchor;
        }
    }
}

[HarmonyPatch(typeof(GenLocalDate), nameof(GenLocalDate.Twelfth), typeof(PlanetTile))]
internal static class Patch_GenLocalDate_Twelfth_RimRunnerTwelfthPin
{
    private static void Postfix(PlanetTile tile, ref Twelfth __result)
    {
        if (RimRunnerTwelfthPin.Active)
        {
            __result = GenDate.Twelfth(RimRunnerTwelfthPin.AnchorTicksAbs, Find.WorldGrid.LongLatOf(tile).x);
        }
    }
}
