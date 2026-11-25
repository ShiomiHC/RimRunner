namespace RimRunner;

/// <summary>袋装图出口落图作用域：作用域内落图的 PocketMapExit 回填到给定入口。</summary>
public static class RimRunnerPocketPortals
{
    /// <summary>作用域内落图的 PocketMapExit 回填到 entrance，结束时还原。</summary>
    public static IDisposable EnterSpawnContext(MapPortal entrance)
    {
        MapPortal previous = PocketMapUtility.currentlyGeneratingPortal;
        PocketMapUtility.currentlyGeneratingPortal = entrance;
        return new RestoreGeneratingPortal(previous);
    }

    private sealed class RestoreGeneratingPortal(MapPortal previous) : IDisposable
    {
        private readonly MapPortal previous = previous;
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            PocketMapUtility.currentlyGeneratingPortal = previous;
        }
    }
}
