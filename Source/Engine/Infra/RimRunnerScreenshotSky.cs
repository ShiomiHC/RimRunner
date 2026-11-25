using System.Runtime.CompilerServices;
using RimWorld.Planet;

namespace RimRunner;

internal static class RimRunnerScreenshotSky
{
    internal const float Glow = 1f;

    internal const float DayPercent = 0.47f;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CurrentSkyTarget")]
    private static extern SkyTarget CurrentSkyTarget(SkyManager sky);

    private static bool suppressed;

    internal static bool Pinned => RimRunnerScreenshotDriver.DaySkyPinned && !suppressed;

    internal static void RestoreClockGlow(SkyManager sky)
    {
        SuppressPin();
        try
        {
            sky.ForceSetCurSkyGlow(CurrentSkyTarget(sky).glow);
        }
        finally
        {
            RestorePin();
        }
    }

    private static void SuppressPin() => suppressed = true;

    private static void RestorePin() => suppressed = false;
}

[HarmonyPatch(typeof(GenCelestial), nameof(GenCelestial.CelestialSunGlow), typeof(PlanetTile), typeof(int))]
internal static class Patch_GenCelestial_CelestialSunGlow_RimRunnerScreenshotSky
{
    private static bool Prefix(ref float __result)
    {
        if (!RimRunnerScreenshotSky.Pinned) return true;
        __result = RimRunnerScreenshotSky.Glow;
        return false;
    }
}

[HarmonyPatch(typeof(GenLocalDate), nameof(GenLocalDate.DayPercent), typeof(PlanetTile))]
internal static class Patch_GenLocalDate_DayPercent_RimRunnerScreenshotSky
{
    private static void Postfix(ref float __result)
    {
        if (RimRunnerScreenshotSky.Pinned) __result = RimRunnerScreenshotSky.DayPercent;
    }
}

[HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
internal static class Patch_SkyManager_SkyManagerUpdate_RimRunnerScreenshotSky
{
    private static void Postfix(SkyManager __instance)
    {
        if (RimRunnerScreenshotSky.Pinned) RimRunnerScreenshotSky.RestoreClockGlow(__instance);
    }
}
