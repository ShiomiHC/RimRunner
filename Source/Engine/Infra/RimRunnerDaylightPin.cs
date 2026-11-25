namespace RimRunner;

internal static class RimRunnerDaylightPin
{
    private static int suspendDepth;

    internal static bool Pinned { get => field && suspendDepth == 0; private set; }

    internal static bool Suspended => suspendDepth > 0;

    internal static void BeginCase()
    {
        Pinned = true;
        suspendDepth = 0;
        WriteAll();
    }

    internal static IDisposable Suspend()
    {
        suspendDepth++;
        WriteAll();
        return new SuspendScope();
    }

    internal static void Reset()
    {
        Pinned = false;
        suspendDepth = 0;
        WriteAll();
    }

    internal static void Write(SkyManager sky)
    {
        if (Pinned) sky.ForceSetCurSkyGlow(1f);
        else RimRunnerScreenshotSky.RestoreClockGlow(sky);
    }

    private static void WriteAll()
    {
        if (Current.Game == null) return;
        foreach (Map map in Find.Maps) Write(map.skyManager);
    }

    private sealed class SuspendScope : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            suspendDepth--;
            WriteAll();
        }
    }
}

[HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
[HarmonyPriority(Priority.Last)]
internal static class Patch_SkyManager_SkyManagerUpdate_RimRunnerDaylightPin
{
    private static void Postfix(SkyManager __instance)
    {
        if (RimRunnerDaylightPin.Pinned) __instance.ForceSetCurSkyGlow(1f);
    }
}

[HarmonyPatch(typeof(SkyManager), MethodType.Constructor, typeof(Map))]
internal static class Patch_SkyManager_Ctor_RimRunnerDaylightPin
{
    private static void Postfix(SkyManager __instance)
    {
        if (RimRunnerDaylightPin.Pinned) __instance.ForceSetCurSkyGlow(1f);
    }
}
