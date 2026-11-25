using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimRunner;

internal static class RimRunnerUiLevels
{
    private static readonly List<string> SceneFaults = [];
    private static bool screenshotModeWas;

    internal static RimRunnerUiLevel? Applied { get; private set; }

    internal static bool HidesMapOverlays => Applied == RimRunnerUiLevel.Scene;

    internal static bool DrawsMapLayers => Applied == RimRunnerUiLevel.Map;

    internal static bool PatchesWanted => GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);

    internal static void SceneFault(string reason)
    {
        if (SceneFaults.Contains(reason))
        {
            return;
        }

        SceneFaults.Add(reason);
        RimRunnerEngineLog.Warn("Scene 档不可用：" + reason);
    }

    internal static string? Apply(RimRunnerUiLevel level)
    {
        if (level == RimRunnerUiLevel.Scene && SceneFaults.Count > 0)
        {
            return "Scene 档不可用：" + string.Join("；", SceneFaults);
        }

        RimRunnerUiLevel? was = Applied;
        if (was == null)
        {
            screenshotModeWas = Find.UIRoot.screenshotMode.Active;
        }

        Find.UIRoot.screenshotMode.Active = level != RimRunnerUiLevel.Screen;
        Applied = level;
        if (was == RimRunnerUiLevel.Scene && level != RimRunnerUiLevel.Scene)
        {
            RebuildZones();
        }

        return null;
    }

    internal static void Restore()
    {
        if (Applied is not RimRunnerUiLevel was)
        {
            return;
        }

        Applied = null;
        Find.UIRoot.screenshotMode.Active = screenshotModeWas;
        if (was == RimRunnerUiLevel.Scene)
        {
            RebuildZones();
        }
    }

    private static void RebuildZones()
    {
        RimRunnerCurrentMapProbe.EngineCurrentMap()?.mapDrawer.WholeMapChanged(MapMeshFlagDefOf.Zone);
    }

    internal static void DrawGUIOverlayUnlessScene(Thing thing)
    {
        if (!HidesMapOverlays || thing is Mote)
        {
            thing.DrawGUIOverlay();
        }
    }
}

[HarmonyPatch(typeof(ThingOverlays), nameof(ThingOverlays.ThingOverlaysOnGUI))]
internal static class Patch_ThingOverlays_ThingOverlaysOnGUI_RimRunnerScene
{
    private static readonly MethodInfo DrawGUIOverlay = AccessTools.Method(typeof(Thing), nameof(Thing.DrawGUIOverlay));

    private static readonly MethodInfo Replacement =
        AccessTools.Method(typeof(RimRunnerUiLevels), nameof(RimRunnerUiLevels.DrawGUIOverlayUnlessScene));

    private static bool Prepare()
    {
        if (!RimRunnerUiLevels.PatchesWanted)
        {
            return false;
        }

        MethodInfo original = AccessTools.Method(typeof(ThingOverlays), nameof(ThingOverlays.ThingOverlaysOnGUI));
        int hits = PatchProcessor.GetOriginalInstructions(original).Count(code => code.Calls(DrawGUIOverlay));
        if (hits != 1)
        {
            RimRunnerUiLevels.SceneFault("ThingOverlays.ThingOverlaysOnGUI 里 Thing.DrawGUIOverlay 的调用有 " + hits
                                         + " 处（预期 1），无法停止绘制名牌与血条");
            return false;
        }

        return true;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction code in instructions)
        {
            if (code.Calls(DrawGUIOverlay))
            {
                code.opcode = System.Reflection.Emit.OpCodes.Call;
                code.operand = Replacement;
            }

            yield return code;
        }
    }
}

[HarmonyPatch]
internal static class Patch_SectionLayerZones_Visible_RimRunnerScene
{
    private static MethodBase? target;

    private static bool Prepare()
    {
        if (!RimRunnerUiLevels.PatchesWanted)
        {
            return false;
        }

        target ??= typeof(SectionLayer).Assembly.GetType("Verse.SectionLayer_Zones", throwOnError: false) is { } zones
            ? AccessTools.PropertyGetter(zones, nameof(SectionLayer.Visible))
            : null;
        if (target == null)
        {
            RimRunnerUiLevels.SceneFault("无法解析 Verse.SectionLayer_Zones.Visible，无法停止绘制区域层");
            return false;
        }

        return true;
    }

    private static MethodBase TargetMethod() => target!;

    private static void Postfix(ref bool __result)
    {
        if (RimRunnerUiLevels.HidesMapOverlays)
        {
            __result = false;
        }
    }
}

[HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.DrawDesignations))]
internal static class Patch_DesignationManager_DrawDesignations_RimRunnerScene
{
    private static bool Prepare()
    {
        return RimRunnerUiLevels.PatchesWanted;
    }

    private static bool Prefix()
    {
        return !RimRunnerUiLevels.HidesMapOverlays;
    }
}

[HarmonyPatch(typeof(OverlayDrawer), nameof(OverlayDrawer.DrawOverlay))]
internal static class Patch_OverlayDrawer_DrawOverlay_RimRunnerScene
{
    private static bool Prepare()
    {
        return RimRunnerUiLevels.PatchesWanted;
    }

    private static void Prefix(ref OverlayTypes overlayType)
    {
        if (RimRunnerUiLevels.HidesMapOverlays)
        {
            overlayType &= OverlayTypes.BurningWick;
        }
    }
}

[HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
internal static class Patch_RootPlay_Update_RimRunnerMap
{
    private static bool Prepare()
    {
        return RimRunnerUiLevels.PatchesWanted;
    }

    private static void Prefix(out bool __state)
    {
        __state = RimRunnerUiLevels.DrawsMapLayers && Find.UIRoot.screenshotMode.Active;
        if (__state)
        {
            Find.UIRoot.screenshotMode.Active = false;
        }
    }

    private static void Finalizer(bool __state)
    {
        if (__state)
        {
            Find.UIRoot.screenshotMode.Active = true;
        }
    }
}
