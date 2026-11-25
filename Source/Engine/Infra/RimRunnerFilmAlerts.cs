using System.Runtime.CompilerServices;

namespace RimRunner;

internal static class RimRunnerFilmAlerts
{
    private const int ReadoutStartTicks = 600;

    private static bool unavailable;
    private static FieldInfo? bounceField;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "activeAlerts")]
    private static extern ref List<Alert> ActiveAlerts(AlertsReadout readout);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "AllAlerts")]
    private static extern ref List<Alert> AllAlerts(AlertsReadout readout);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "curAlertIndex")]
    private static extern ref int CurAlertIndex(AlertsReadout readout);

    internal static void Settle()
    {
        if (unavailable || Find.UIRoot is not UIRoot_Play play)
        {
            return;
        }

        try
        {
            AlertsReadout readout = play.alerts;
            List<Alert> active = ActiveAlerts(readout);
            List<Alert> all = AllAlerts(readout);
            HashSet<Alert> roster = new(all);
            List<Alert> others = active.Where(alert => !roster.Contains(alert)).ToList();
            active.Clear();
            CurAlertIndex(readout) = 0;
            if (Mathf.Max(Find.TickManager.TicksGame, Find.TutorialState.endTick) < ReadoutStartTicks
                || Find.Storyteller.def.disableAlerts)
            {
                return;
            }

            foreach (Alert alert in all)
            {
                try
                {
                    alert.Recalculate();
                }
                catch (Exception)
                {
                    continue;
                }

                if (alert.Active)
                {
                    active.Add(alert);
                }
            }

            active.AddRange(others);
            bounceField ??= AccessTools.Field(typeof(Alert), "alertBounce")
                            ?? throw new MissingFieldException(nameof(Alert), "alertBounce");
            foreach (Alert alert in active)
            {
                bounceField.SetValue(alert, null);
            }
        }
        catch (MissingMemberException exception)
        {
            unavailable = true;
            RimRunnerEngineFault.Raise("无法解析 AlertsReadout 的私有字段（" + exception.Message
                                       + "）。拍摄开拍时不重建警报读出：同一镜头在命令里第一个拍摄与在回载后拍摄，"
                                       + "警报的出现帧与位置不保证相同");
        }
    }
}

[HarmonyPatch(typeof(AlertsReadout), nameof(AlertsReadout.AlertsReadoutUpdate))]
internal static class Patch_AlertsReadout_AlertsReadoutUpdate_RimRunnerFilm
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Film;
    }

    private static bool Prefix()
    {
        return !RimRunnerFilmClock.SkipsFrame;
    }
}
