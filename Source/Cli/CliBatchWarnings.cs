using System.Text.Json.Serialization;

namespace RimRunnerCli;

public static class CliBatchWarnings
{
    public sealed class Entry
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }

    private static List<Entry> ProcessEntries { get; } = [];

    private static List<Entry> entries => CommandContext.Current?.BatchWarnings ?? ProcessEntries;

    private static HashSet<string> ProcessChecked { get; } = new(StringComparer.Ordinal);

    private static HashSet<string> checkedKeys => CommandContext.Current?.BatchChecks ?? ProcessChecked;

    public static void Checked(string key)
    {
        lock (checkedKeys)
        {
            checkedKeys.Add(key);
        }
    }

    public static IReadOnlyList<string> CheckedSnapshot()
    {
        lock (checkedKeys)
        {
            return [.. checkedKeys.Order(StringComparer.Ordinal)];
        }
    }

    public static void Add(string key, string text)
    {
        lock (entries)
        {
            if (!entries.Any(entry => entry.Key == key && entry.Text == text))
                entries.Add(new Entry { Key = key, Text = text });
        }
    }

    public static IReadOnlyList<Entry> Snapshot()
    {
        lock (entries)
        {
            return [.. entries];
        }
    }
}
