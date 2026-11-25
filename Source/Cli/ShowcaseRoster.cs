using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

public static class ShowcaseRoster
{
    public sealed class Roster
    {
        public string Path { get; init; } = "";
        public string GeneratedAt { get; init; } = "";
        public IReadOnlyDictionary<string, string>? AssemblyMvids { get; init; }
        public IReadOnlyList<Entry> Showcases { get; init; } = [];
    }

    public sealed class Entry
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public IReadOnlyList<string> Steps { get; init; } = [];
        public IReadOnlyList<string> RequiresPackages { get; init; } = [];
    }

    public static Roster? TryLoadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Parse(File.ReadAllText(path), path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Roster? TryParse(string text, string path)
    {
        try
        {
            return Parse(text, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Roster Parse(string text, string path)
    {
        using JsonDocument document = StrictJson.ParseObject(text);
        JsonElement root = document.RootElement;
        return new Roster
        {
            Path = path,
            GeneratedAt = PlainStr(root, RimRunnerDataLayout.ShowcaseManifestKeys.GeneratedAt),
            AssemblyMvids = MvidsOf(root),
            Showcases =
            [
                .. root.TryGetProperty(RimRunnerDataLayout.ShowcaseManifestKeys.Showcases, out JsonElement items)
                   && items.ValueKind == JsonValueKind.Array
                    ? items.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.Object)
                        .Select(item => new Entry
                        {
                            Id = PlainStr(item, RimRunnerDataLayout.ShowcaseManifestKeys.Id),
                            Title = PlainStr(item, RimRunnerDataLayout.ShowcaseManifestKeys.Title),
                            Steps = Strs(item, RimRunnerDataLayout.ShowcaseManifestKeys.Steps),
                            RequiresPackages = Strs(item,
                                RimRunnerDataLayout.ShowcaseManifestKeys.RequiresPackages),
                        })
                    : []
            ],
        };
    }

    private static string PlainStr(JsonElement item, string key) =>
        item.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static IReadOnlyDictionary<string, string>? MvidsOf(JsonElement root)
    {
        if (!root.TryGetProperty(RimRunnerDataLayout.ShowcaseManifestKeys.AssemblyMvids, out JsonElement value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return value.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "",
                StringComparer.Ordinal);
    }

    public static int Print(Roster roster)
    {
        foreach (string line in Format(roster))
            Console.WriteLine(line);
        return 0;
    }

    public static string Missing(string path) =>
        $"无展台名册：{path}（名册由 CLI 在冷启动前从编译产物生成）";

    public static string Unparsable(string path) =>
        $"无法解析展台名册：{path}（名册由 CLI 在冷启动前从编译产物生成）";

    public static IReadOnlyList<string> Format(Roster roster)
    {
        var lines = new List<string> { $"展台名册（生成于 {roster.GeneratedAt}）" };
        foreach (Entry entry in roster.Showcases.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            lines.Add($"{entry.Id} · {entry.Title}"
                      + (entry.Steps.Count == 0 ? "" : "（步骤：" + string.Join(" · ", entry.Steps) + "）"));
        }

        return lines;
    }

    private static List<string> Strs(JsonElement item, string key) =>
        item.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ?
            [
                .. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!)
            ]
            : [];
}
