namespace RimRunner;

internal static partial class RimRunnerExperiments
{
    internal const string Trace = "trace";

    internal const string ForceMainMap = "forceMainMap";

    internal static readonly string[] All = [Trace, ForceMainMap];

    internal static string Join(System.Func<string, bool> isOn)
    {
        string joined = "";
        foreach (string name in All)
        {
            if (isOn(name))
            {
                joined = joined.Length == 0 ? name : joined + "," + name;
            }
        }

        return joined;
    }

    internal static bool Listed(string? list, string name) =>
        !string.IsNullOrEmpty(list) && System.Array.IndexOf(list!.Split(','), name) >= 0;
}
