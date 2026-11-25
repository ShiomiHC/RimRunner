namespace RimRunner;

internal static class RimRunnerObservationKeys
{
    private const string MarkerHead = "[观察点 ";

    internal const string RetentionRemainder = "retention-remainder";
    internal const string UnattributedAssembly = "unattributed-assembly";
    internal const string SharingViolation = "sharing-violation";
    internal const string DummyBaseValueGate = "dummy-base-value-gate";
    internal const string StaleAfterCompile = "stale-after-compile";
    internal const string RestoreMissedReload = "restore-missed-reload";
    internal const string ScreenRepeat = "screen-repeat";

    internal static string Marker(string key) => MarkerHead + key + "]";

    internal static string Line(string key, string text) => Marker(key) + " " + text;
}
