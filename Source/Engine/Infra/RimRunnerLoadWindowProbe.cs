namespace RimRunner;

internal static class RimRunnerLoadWindowProbe
{
    internal const string LoadSegment = "load";
    internal const string TickSegment = "tick";

    internal static readonly string LoadSegmentText =
        RimRunnerLog.Prefix + " LOAD3 探针：载档本体段（LoadedGame）的错误级日志";

    internal static readonly string TickSegmentText =
        RimRunnerLog.Prefix + " LOAD3 探针：读档后首 tick 的错误级日志";

    private static string? ownerCaseId;
    private static Game? armedGame;

    internal static bool LoadSegmentFired { get; private set; }
    internal static bool TickSegmentFired { get; private set; }
    internal static int TickSegmentFiredAt { get; private set; } = -1;

    internal static readonly List<string> Deliveries = [];

    internal static string DescribeDelivery(string line, string segment, string caseId)
    {
        return line + "@" + segment + "→" + caseId;
    }

    internal static void Arm(string caseId)
    {
        ownerCaseId = caseId;
        armedGame = Current.Game;
        LoadSegmentFired = false;
        TickSegmentFired = false;
        TickSegmentFiredAt = -1;
        Deliveries.Clear();
    }

    internal static void Disarm()
    {
        ownerCaseId = null;
        armedGame = null;
    }

    internal static readonly string AckedLineText =
        RimRunnerLog.Prefix + " LOAD4 探针：载档本体段里由日志许可认领的错误级日志";

    private static bool ackedLineArmed;
    private static Game? ackedLineGame;

    internal static bool AckedLineFired { get; private set; }

    internal static void ArmAckedLine()
    {
        ackedLineArmed = true;
        ackedLineGame = Current.Game;
        AckedLineFired = false;
    }

    internal static void DisarmAckedLine()
    {
        ackedLineArmed = false;
        ackedLineGame = null;
    }

    internal static void OnLoadedGame()
    {
        if (ackedLineArmed && !AckedLineFired && !ReferenceEquals(Current.Game, ackedLineGame))
        {
            AckedLineFired = true;
            Log.Error(AckedLineText);
        }

        if (!FiresNow() || LoadSegmentFired)
        {
            return;
        }

        LoadSegmentFired = true;
        Log.Error(LoadSegmentText);
    }

    internal static void OnTick()
    {
        if (!FiresNow() || TickSegmentFired)
        {
            return;
        }

        TickSegmentFired = true;
        TickSegmentFiredAt = Find.TickManager?.TicksGame ?? -1;
        Log.Error(TickSegmentText);
    }

    private static bool FiresNow()
    {
        return ownerCaseId != null && !ReferenceEquals(Current.Game, armedGame);
    }

    internal static bool TryClaim(string caseId, string? line, string segment)
    {
        if (ownerCaseId == null || line == null)
        {
            return false;
        }

        string? text = line.StartsWith(RimRunnerLogMonitor.ErrorPrefix, StringComparison.Ordinal)
            ? line[RimRunnerLogMonitor.ErrorPrefix.Length..]
            : null;
        if (text != LoadSegmentText && text != TickSegmentText)
        {
            return false;
        }

        if (!string.Equals(caseId, ownerCaseId, StringComparison.Ordinal))
        {
            return false;
        }

        Deliveries.Add(DescribeDelivery(text == LoadSegmentText ? LoadSegment : TickSegment, segment, caseId));
        return true;
    }

    internal static bool IsProbeLine(string? line)
    {
        if (ownerCaseId == null || line == null
            || !line.StartsWith(RimRunnerLogMonitor.ErrorPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string text = line[RimRunnerLogMonitor.ErrorPrefix.Length..];
        return text == LoadSegmentText || text == TickSegmentText;
    }
}
