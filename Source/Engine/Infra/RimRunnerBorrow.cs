using System.IO;

namespace RimRunner;

internal sealed class RimRunnerBorrowLayer
{
    internal const string Main = RimRunnerEngineArgs.MainLayerName;

    internal RimRunnerBorrowLayer(string name, string dataRoot, string outputRoot)
    {
        Name = name;
        DataRoot = dataRoot;
        OutputRoot = outputRoot;
    }

    internal string Name { get; }
    internal string DataRoot { get; }
    internal string OutputRoot { get; }

    internal string HistoryDir => Path.Combine(OutputRoot, RimRunnerDataLayout.HistoryDirName);
}

internal static class RimRunnerBorrow
{
    private static readonly List<KeyValuePair<string, List<string>>> layers = [];

    internal static void NoteLedger(string layer, string label)
    {
        Note(layer, label);
    }

    private static void Note(string layer, string what)
    {
        List<string>? items = layers.FirstOrDefault(pair => pair.Key == layer).Value;
        if (items == null)
        {
            items = [];
            layers.Add(new KeyValuePair<string, List<string>>(layer, items));
        }

        if (!items.Contains(what))
        {
            items.Add(what);
        }
    }

    internal static void ResetForRun()
    {
        layers.Clear();
    }

    internal static List<KeyValuePair<string, List<string>>> Snapshot()
    {
        List<string> order = RimRunnerPaths.BorrowLayers.Select(layer => layer.Name).ToList();
        return layers
            .OrderBy(pair => order.IndexOf(pair.Key) is int index && index >= 0 ? index : int.MaxValue)
            .Select(pair => new KeyValuePair<string, List<string>>(pair.Key, [.. pair.Value]))
            .ToList();
    }
}
