using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using RimRunner;
using RimTooling.IO;

namespace RimRunnerCli;

internal static class DescriptorDocs
{
    internal static string KeyOf(PropertyInfo property) =>
        JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    internal static IEnumerable<(PropertyInfo Property, DescriptorKeyAttribute Key)> Keys(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (property, property.GetCustomAttribute<DescriptorKeyAttribute>()))
            .Where(pair => pair.Item2 != null)
            .Select(pair => (pair.property, pair.Item2!));

    internal static bool IsSection(Type type) =>
        type.IsClass && type != typeof(string) && Keys(type).Any();

    internal static Type? ListElement(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? type.GetGenericArguments()[0]
            : null;

    internal static bool IsDictionary(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    internal static DescriptorKeyAttribute? Find(Type root, string path)
    {
        Type type = root;
        DescriptorKeyAttribute? found = null;
        foreach (string segment in path.Replace("[]", "").Split('.'))
        {
            (PropertyInfo Property, DescriptorKeyAttribute Key) = Keys(type).FirstOrDefault(pair =>
                string.Equals(KeyOf(pair.Property), segment, StringComparison.OrdinalIgnoreCase));
            if (Property == null) return null;
            found = Key;
            Type next = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
            type = ListElement(next) ?? next;
        }

        return found;
    }

    internal static string PathOf(string problem)
    {
        int end = problem.IndexOfAny([' ', '（', '：']);
        string head = end < 0 ? problem : problem[..end];
        var builder = new StringBuilder();
        int depth = 0;
        foreach (char c in head)
        {
            if (c == '[') { depth++; builder.Append("[]"); continue; }
            if (c == ']') { depth--; continue; }
            if (depth == 0) builder.Append(c);
        }

        return builder.ToString();
    }

    internal static IEnumerable<string> Annotate(Type root, IEnumerable<string> problems)
    {
        foreach (string problem in problems)
        {
            yield return problem;
            if (Find(root, PathOf(problem)) is DescriptorKeyAttribute key)
                yield return "  " + key.Summary;
        }
    }

    internal static string KeyOfJsonPath(string? path) =>
        string.IsNullOrEmpty(path) || path == "$" ? "" : path.TrimStart('$', '.') + "：";

    internal static string Siblings(Type type) =>
        string.Join("、", Keys(type).Select(pair => KeyOf(pair.Property)));
}

internal static class DescriptorTemplate
{
    internal const string Command = "descriptor-template";

    private static readonly JsonSerializerOptions DefaultsOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly (string Target, Type Type, string FileName)[] Targets =
    [
        ("manifest", typeof(ManifestDocument), ManifestDocument.FileName),
        ("runtime", typeof(RuntimeDocument), RuntimeDocument.FileName),
        ("preflight", typeof(PreflightDocument), PreflightDocument.FileName),
        ("excluded", typeof(ExcludedDocument), ExcludedDocument.FileName),
    ];

    internal static int Execute(string searchDir, IReadOnlyList<string> args, Func<string, int> usage)
    {
        string[] names = [.. Targets.Select(target => target.Target), "newgame"];
        if (args.Count != 1 || !names.Contains(args[0]))
            return usage($"{Command} 只收一个参数：{string.Join("、", names)}");

        string? productRoot = OrchestratorDescriptor.FindProductRoot(searchDir);
        if (productRoot == null)
            return usage($"找不到 {OrchestratorDescriptor.FileName}（从 {searchDir} 向上）");

        try
        {
            JsonObject? manifest = ReadIfPresent(Path.Combine(productRoot, OrchestratorDescriptor.FileName));
            string dir = WorkspaceDir(manifest);
            if (args[0] == "newgame")
            {
                string path = OrchestratorDescriptor.WorkspaceFile(productRoot, dir + "/data", OrchestratorDescriptor.DefaultNewGameFileName);
                CliLog.Machine(() => Console.Write(NewGame(ReadIfPresent(path), path)));
                return 0;
            }

            (_, Type type, string fileName) = Targets.First(target => target.Target == args[0]);
            string source = type == typeof(ManifestDocument)
                ? Path.Combine(productRoot, fileName)
                : OrchestratorDescriptor.WorkspaceFile(productRoot, dir, fileName);
            JsonObject? written = type == typeof(ManifestDocument) ? manifest : ReadIfPresent(source);
            CliLog.Machine(() => Console.Write(Document(type, written ?? [], source, dir)));
            return 0;
        }
        catch (DescriptorException exception)
        {
            Console.WriteLine($"[FAIL] {exception.Message}");
            return ExitPolicy.FromException(exception);
        }
    }

    private static string WorkspaceDir(JsonObject? manifest) =>
        manifest != null && manifest.TryGetPropertyValue("dir", out JsonNode? node) && node is JsonValue value
                         && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text.Replace('\\', '/').Trim().Trim('/')
            : ManifestDocument.DefaultDir;

    private static JsonObject? ReadIfPresent(string path)
    {
        string file = Path.GetFileName(path);
        (bool exists, TomlTable table) = TomlFile.Read(path, file, message => new DescriptorException(message));
        return exists ? TomlFile.ToJson(table, path, file, message => new DescriptorException(message)) : null;
    }

    internal static string Document(Type type, JsonObject written, string source = "", string dir = ManifestDocument.DefaultDir)
    {
        DescriptorKeyGate.Reject(written, source, type, OrchestratorDescriptor.KindLabel(type), DescriptorFile.Elsewhere(type, dir));
        return Render(type, written, DefaultsOf(type), absentSectionsAreNoOp: false);
    }

    internal static string NewGame(JsonObject? written, string source = "")
    {
        if (written != null)
            DescriptorKeyGate.Reject(written, source, typeof(RimRunnerNewGameDescriptor), "建档描述符");
        return Render(typeof(RimRunnerNewGameDescriptor), written ?? [], NewGameDescriptorFile.PresetDefaults(),
            absentSectionsAreNoOp: true);
    }

    private enum Shape { Inline, Table, Map, TableArray }

    private static string Render(Type type, JsonObject written, JsonObject defaults, bool absentSectionsAreNoOp)
    {
        var sink = new StringBuilder();
        WriteTable(sink, type, written, defaults, "", commented: false, absentSectionsAreNoOp);
        return sink.ToString().TrimStart('\n');
    }

    private static void WriteTable(StringBuilder sink, Type type, JsonObject? written, JsonObject? defaults,
        string prefix, bool commented, bool absentSectionsAreNoOp)
    {
        var members = new List<(PropertyInfo Property, DescriptorKeyAttribute Key, string Name, JsonNode? Value,
            bool Present, JsonNode? Fallback, Type Type, Shape Shape)>();
        foreach ((PropertyInfo property, DescriptorKeyAttribute key) in DescriptorDocs.Keys(type))
        {
            string name = DescriptorDocs.KeyOf(property);
            JsonNode? value = Lookup(written, name, out bool present);
            present &= value != null;
            if (key.MaintainerOnly && !present) continue;
            if (key.FilledByCli) continue;
            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            members.Add((property, key, name, value, present, Lookup(defaults, name, out _), propertyType,
                ShapeOf(propertyType, present ? value : null)));
        }

        foreach (var member in members.Where(member => member.Shape == Shape.Inline))
        {
            Summary(sink, member.Key);
            bool lineCommented = commented || !member.Present;
            JsonNode? shown = member.Present ? member.Value : member.Fallback;
            Line(sink, lineCommented, $"{Key(member.Name)} = {(shown is null ? Placeholder(member.Type) : Value(shown))}");
        }

        foreach (var member in members.Where(member => member.Shape != Shape.Inline))
        {
            string path = prefix.Length == 0 ? Key(member.Name) : prefix + "." + Key(member.Name);
            sink.Append('\n');
            Summary(sink, member.Key);
            switch (member.Shape)
            {
                case Shape.Table:
                {
                    bool open = !commented && (member.Present || absentSectionsAreNoOp);
                    Line(sink, !open, $"[{path}]");
                    JsonObject? fallback = member.Fallback as JsonObject ?? DefaultsOf(member.Type);
                    WriteTable(sink, member.Type, member.Value as JsonObject, fallback, path, !open,
                        absentSectionsAreNoOp);
                    break;
                }
                case Shape.Map:
                {
                    bool lineCommented = commented || !member.Present;
                    Line(sink, lineCommented, $"[{path}]");
                    if ((member.Present ? member.Value : member.Fallback) is JsonObject map)
                        foreach ((string entry, JsonNode? entryValue) in map)
                            Line(sink, lineCommented, $"{Key(entry)} = {Value(entryValue)}");
                    break;
                }
                case Shape.TableArray:
                {
                    Type element = DescriptorDocs.ListElement(member.Type)!;
                    JsonObject itemDefaults = DefaultsOf(element);
                    if (member.Present && member.Value is JsonArray items)
                    {
                        bool first = true;
                        foreach (JsonNode? item in items)
                        {
                            if (!first) sink.Append('\n');
                            first = false;
                            Line(sink, commented, $"[[{path}]]");
                            WriteTable(sink, element, item as JsonObject, itemDefaults, path, commented,
                                absentSectionsAreNoOp);
                        }
                    }
                    else
                    {
                        Line(sink, true, $"[[{path}]]");
                        WriteTable(sink, element, null, itemDefaults, path, true, absentSectionsAreNoOp);
                    }

                    break;
                }
            }
        }
    }

    private static Shape ShapeOf(Type type, JsonNode? written)
    {
        if (DescriptorDocs.ListElement(type) is Type element && DescriptorDocs.IsSection(element))
            return written is JsonArray { Count: 0 } ? Shape.Inline : Shape.TableArray;
        if (DescriptorDocs.IsSection(type)) return Shape.Table;
        if (DescriptorDocs.IsDictionary(type)) return Shape.Map;
        return Shape.Inline;
    }

    private static void Summary(StringBuilder sink, DescriptorKeyAttribute key) =>
        sink.Append("# ").Append(key.Required ? "必填。" : "").Append(key.Summary).Append('\n');

    private static string Placeholder(Type type)
    {
        if (DescriptorDocs.ListElement(type) is Type element)
            return $"[{Placeholder(element)}, …]";
        if (type == typeof(string)) return "<字符串>";
        if (type == typeof(bool)) return "<true / false>";
        if (type == typeof(int) || type == typeof(long)) return "<整数>";
        if (type == typeof(float) || type == typeof(double)) return "<数>";
        return "<值>";
    }

    private static string Value(JsonNode? node) => node switch
    {
        null => "<值>",
        JsonArray array => "[" + string.Join(", ", array.Select(Value)) + "]",
        JsonObject map => "{ " + string.Join(", ", map.Select(pair => $"{Key(pair.Key)} = {Value(pair.Value)}")) + " }",
        JsonValue value when value.TryGetValue(out string? text) => Quote(text),
        JsonValue value when value.GetValueKind() == JsonValueKind.True => "true",
        JsonValue value when value.GetValueKind() == JsonValueKind.False => "false",
        _ => node.ToJsonString(),
    };

    private static string Key(string name) => TomlDocument.FormatKey(name);

    private static string Quote(string text) => TomlDocument.FormatString(text);

    private static JsonObject DefaultsOf(Type type)
    {
        object instance = Activator.CreateInstance(type)!;
        var node = new JsonObject();
        foreach ((PropertyInfo property, _) in DescriptorDocs.Keys(type))
        {
            object? value = property.GetValue(instance);
            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            node[DescriptorDocs.KeyOf(property)] = value != null && DescriptorDocs.IsSection(propertyType)
                ? DefaultsOf(propertyType)
                : JsonSerializer.SerializeToNode(value, property.PropertyType, DefaultsOptions);
        }

        return node;
    }

    private static JsonNode? Lookup(JsonObject? node, string name, out bool present)
    {
        present = false;
        if (node == null) return null;
        foreach ((string key, JsonNode? value) in node)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) continue;
            present = true;
            return value;
        }

        return null;
    }

    private static void Line(StringBuilder sink, bool commented, string text) =>
        sink.Append(commented ? "# " : "").Append(text).Append('\n');
}
