namespace RimRunner;

[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.HandleEventsHighPriority))]
internal static class Patch_WindowStack_HandleEventsHighPriority_RimRunnerShowcaseKeys
{
    private static void Prefix()
    {
        if (RimRunnerProcessMode.Showcase)
        {
            Window_RimRunnerShowcase.HandleHotkeys();
        }
    }
}

[HarmonyPatch(typeof(DebugWindowsOpener), "DrawButtons")]
internal static class Patch_DebugWindowsOpener_DrawButtons_RimRunnerShowcase
{
    private static void Postfix(WidgetRow ___widgetRow, ref float ___widgetRowFinalX)
    {
        if (!RimRunnerProcessMode.Showcase || Current.ProgramState != ProgramState.Playing)
        {
            return;
        }

        bool open = Find.WindowStack.WindowOfType<Window_RimRunnerShowcase>() != null;
        bool toggled = open;
        ___widgetRow.ToggleableIcon(ref toggled, TexButton.Play, "RimRunner 展台面板");
        if (toggled != open)
        {
            Window_RimRunnerShowcase.Toggle();
        }

        ___widgetRowFinalX = ___widgetRow.FinalX;
    }
}
