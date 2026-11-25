using System.Runtime.ExceptionServices;

namespace RimRunner;

[HarmonyPatch(typeof(Ability), nameof(Ability.Activate), typeof(LocalTargetInfo), typeof(LocalTargetInfo))]
internal static class Patch_Ability_Activate_RimRunnerRandControl
{
    private static void Prefix(Ability __instance, out bool __state)
    {
        __state = RimRunnerRandControl.TryPushScheduledAbilitySeed(__instance);
    }

    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        RimRunnerRandControl.PopScheduledAbilitySeed(__state);
        if (__exception != null) ExceptionDispatchInfo.Capture(__exception).Throw();
        return null;
    }
}
