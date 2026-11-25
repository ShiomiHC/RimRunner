using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using RimRunner;

namespace RimRunnerCli;

internal static class DescriptorKeyGate
{
    public static void Reject(JsonObject root, string source, Type rootType, string kindLabel,
        IReadOnlyDictionary<string, string>? elsewhere = null)
    {
        JsonElement element = JsonSerializer.SerializeToElement(root);
        var unknown = new List<string>();
        Collect(element, rootType, "", unknown, elsewhere);
        if (unknown.Count > 0)
        {
            throw new DescriptorException(
                $"{kindLabel}含未知键：{source}" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ",
                    unknown));
        }
    }

    private static void Collect(JsonElement node, Type type, string prefix, List<string> sink,
        IReadOnlyDictionary<string, string>? elsewhere = null)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            Type? element = ElementTypeOrNull(type);
            if (element == null) return;
            int index = 0;
            foreach (JsonElement item in node.EnumerateArray())
                Collect(item, element, $"{prefix.TrimEnd('.')}[{index++}].", sink);
            return;
        }

        if (node.ValueKind != JsonValueKind.Object) return;
        if (IsDictionary(type)) return;

        PropertyInfo[] properties = type.GetProperties();
        foreach (JsonProperty property in node.EnumerateObject())
        {
            PropertyInfo? match = properties.FirstOrDefault(candidate => string.Equals(
                candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                if (elsewhere != null && elsewhere.TryGetValue(property.Name, out string? file))
                {
                    sink.Add($"{prefix}{property.Name} 属于 {file}，不写在这里");
                    continue;
                }

                string level = prefix.Length == 0 ? "顶层" : prefix.TrimEnd('.') + " 下";
                sink.Add($"{prefix}{property.Name} 不是描述符可识别的键；{level}可识别的键：{DescriptorDocs.Siblings(type)}");
                continue;
            }

            Collect(property.Value, match.PropertyType, prefix + property.Name + ".", sink);
        }
    }

    private static bool IsDictionary(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    private static Type? ElementTypeOrNull(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? type.GetGenericArguments()[0]
            : null;
}
