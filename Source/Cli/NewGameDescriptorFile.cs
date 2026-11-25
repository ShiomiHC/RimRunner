using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimRunner;
using RimTooling.IO;

namespace RimRunnerCli;

internal static class NewGameDescriptorFile
{
    internal static JsonObject PresetDefaults() =>
        (JsonObject)JsonNode.Parse(RealisticJson, documentOptions: NodeOptions)!;

    private const string RealisticJson = """
        {
          "preset": "realistic",
          "saveName": null,
          "seed": null,
          "scenario": {
            "base": "Crashlanded",
            "removeParts": [],
            "startingPawnCount": null,
            "pawnChoiceCount": null,
            "playerFaction": null,
            "arriveMethod": null,
            "disableIncidents": []
          },
          "storyteller": {
            "def": "Cassandra",
            "difficultyDef": "Rough",
            "difficulty": null,
            "runtimeGate": true
          },
          "world": {
            "planetCoverage": 0.3,
            "rainfall": "Normal",
            "temperature": "Normal",
            "population": "Normal",
            "landmarkDensity": "Normal"
          },
          "ideo": {
            "classicMode": true
          },
          "startingTile": {
            "biome": "TemperateForest",
            "hilliness": "Flat",
            "requireRiver": false,
            "requireCoast": false
          },
          "map": {
            "size": 200,
            "generatorDefOnSave": null
          },
          "output": { "scrubEngineComponent": true }
        }
        """;

    private static readonly JsonDocumentOptions NodeOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static RimRunnerNewGameDescriptor Load(
        string productRoot, OrchestratorDescriptor orchestrator, string? modList, out string mergedJson)
    {
        var overlays = new List<(string Source, JsonObject Node)>();
        string productPath = orchestrator.ResolveNewGameFile(productRoot);
        AddOverlayIfPresent(overlays, productPath);
        if (!string.IsNullOrWhiteSpace(modList))
        {
            AddOverlayIfPresent(overlays, Path.Combine(
                productRoot, orchestrator.ModListsDir.Replace('/', Path.DirectorySeparatorChar),
                modList + RimRunnerNewGameDescriptor.ModListSuffix));
        }

        string preset = RimRunnerNewGameDescriptor.PresetRealistic;
        string presetSource = "内置 realistic 预设";
        foreach ((string source, JsonObject node) in overlays)
        {
            if (node["preset"] is JsonValue value && value.TryGetValue(out string? declared)
                && !string.IsNullOrWhiteSpace(declared))
            {
                preset = declared.Trim();
                presetSource = source;
            }
        }

        if (preset != RimRunnerNewGameDescriptor.PresetRealistic)
        {
            throw new DescriptorException(
                $"preset 无法识别：{preset}（声明处：{presetSource}）；允许：{RimRunnerNewGameDescriptor.PresetRealistic}");
        }

        JsonObject merged = (JsonObject)JsonNode.Parse(RealisticJson, documentOptions: NodeOptions)!;
        foreach ((_, JsonObject node) in overlays)
        {
            MergeInto(merged, node);
        }

        merged["saveName"] = orchestrator.BaselineSaveNameFor(modList);

        if (IsBlank(merged["seed"]))
        {
            merged["seed"] = orchestrator.SaveNamePrefix;
        }

        RimRunnerNewGameDescriptor descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<RimRunnerNewGameDescriptor>(
                merged.ToJsonString(), RimRunnerNewGameDescriptor.MergedJson)
                ?? throw new JsonException("根元素为 null");
        }
        catch (JsonException exception)
        {
            throw new DescriptorException(
                $"建档描述符合并后无法解析为模型（层：{LayerList(overlays)}）\n  {DescriptorDocs.KeyOfJsonPath(exception.Path)}{exception.Message}");
        }

        List<string> problems = descriptor.Problems();
        if (problems.Count > 0)
        {
            throw new DescriptorException(
                $"建档描述符不合格（层：{LayerList(overlays)}）" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ",
                    DescriptorDocs.Annotate(typeof(RimRunnerNewGameDescriptor), problems)));
        }

        mergedJson = JsonSerializer.Serialize(descriptor, RimRunnerNewGameDescriptor.MergedJson);
        return descriptor;
    }

    private static bool IsBlank(JsonNode? node) =>
        node is not JsonValue value || !value.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text);

    private static string LayerList(List<(string Source, JsonObject Node)> overlays) =>
        overlays.Count == 0
            ? "仅内置 realistic 预设"
            : "内置 realistic 预设 + " + string.Join(" + ", overlays.Select(layer => layer.Source));

    private static void AddOverlayIfPresent(
        List<(string Source, JsonObject Node)> sink, string path)
    {
        (bool exists, TomlTable table) = TomlFile.Read(path, Path.GetFileName(path), message => new DescriptorException(message));
        if (!exists)
        {
            return;
        }

        JsonObject node = TomlFile.ToJson(table, path, Path.GetFileName(path), message => new DescriptorException(message));
        DescriptorKeyGate.Reject(node, path, typeof(RimRunnerNewGameDescriptor), "建档描述符");
        foreach ((PropertyInfo property, DescriptorKeyAttribute key) in DescriptorDocs.Keys(typeof(RimRunnerNewGameDescriptor)))
        {
            string name = DescriptorDocs.KeyOf(property);
            if (key.FilledByCli && node.Any(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)))
                throw new DescriptorException($"建档描述符写有 {name}：{path}\n  {key.Summary}");
        }

        sink.Add((path, node));
    }

    private static void MergeInto(JsonObject target, JsonObject overlay)
    {
        foreach (KeyValuePair<string, JsonNode?> pair in overlay)
        {
            if (pair.Value is JsonObject nested && target[pair.Key] is JsonObject existing)
            {
                MergeInto(existing, nested);
                continue;
            }

            target[pair.Key] = pair.Value?.DeepClone();
        }
    }
}
