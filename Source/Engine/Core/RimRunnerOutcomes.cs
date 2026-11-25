namespace RimRunner;

public static class RimRunnerOutcomes
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string UnclaimedFault = "UNCLAIMED_FAULT";
    public const string NoAssertions = "NO_ASSERTIONS";
    public const string SentinelFault = "SENTINEL_FAULT";
    public const string EngineFault = "ENGINE_FAULT";
    public const string Done = "DONE";
    public const string Incomplete = "INCOMPLETE";

    public static bool IsKnown(string status) =>
        status is Pass or Fail or UnclaimedFault or NoAssertions or SentinelFault or EngineFault or Done or Incomplete;

    public const char GlyphPass = 'o';
    public const char GlyphNoAssertions = RimRunnerArchiveName.NoAssertionsGlyph;
    public const char GlyphFail = 'X';
    public const char GlyphUnclaimedFault = 'U';

    public const char GlyphUnknown = '?';

    public static int Severity(string status) => status switch
    {
        EngineFault => 6,
        UnclaimedFault => 5,
        SentinelFault => 4,
        Fail or Incomplete => 3,
        NoAssertions => 2,
        Pass or Done => 1,
        _ => 7,
    };

    public static bool CountsAsPass(string status)
    {
        return status is Pass;
    }

    public static char GlyphOf(string status)
    {
        if (status == Pass) return GlyphPass;
        if (status == NoAssertions) return GlyphNoAssertions;
        if (status == Fail) return GlyphFail;
        if (status == UnclaimedFault) return GlyphUnclaimedFault;
        return GlyphUnknown;
    }
}
