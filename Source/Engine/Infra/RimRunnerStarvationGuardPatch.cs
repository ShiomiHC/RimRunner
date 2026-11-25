namespace RimRunner;

[HarmonyPatch(typeof(Game), nameof(Game.UpdatePlay))]
internal static class Patch_GameUpdatePlay_RimRunnerStarvationGuard
{
    private static void Prefix()
    {
        RimRunnerGameComponent.PumpIfStarved();
    }
}
