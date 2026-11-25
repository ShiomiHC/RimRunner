using System.Text.Json;

namespace RimRunnerCli;

internal static class LedgerBook
{
    internal static string LockSection(string path) =>
        "ledger:" + PathIdentity.Normalize(path).ToUpperInvariant();

    internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    internal static void Exclusive(string path, Action action, TimeSpan? timeout = null) =>
        Hold(path, action, section => Leases.Lock(section, timeout ?? LockTimeout, "台账临界区"));

    internal static void ExclusiveForExecutor(string path, Action action, TimeSpan? timeout = null) =>
        Hold(path, action, section => Leases.LockForExecutor(section, timeout ?? LockTimeout, "台账临界区"));

    private static void Hold(string path, Action action, Func<string, IDisposable> lease)
    {
        string full = PathIdentity.Normalize(path);
        InstanceOutput.UnderGate(full, () =>
        {
            using IDisposable held = lease(LockSection(full));
            action();
        });
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static Dictionary<string, List<string>> Read(string path)
    {
        var rings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (!File.Exists(path)) return rings;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"台账不是 JSON 对象：{path}");
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                rings[property.Name] = property.Value.ValueKind == JsonValueKind.Array
                    ? [.. property.Value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()!)]
                    : [];
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"无法解析台账：{path}：{exception.Message}", exception);
        }

        return rings;
    }

    internal static void Write(string path, Dictionary<string, List<string>> rings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RimTooling.IO.AtomicFile.Write(path, JsonSerializer.Serialize(rings, Options) + "\n");
    }

    internal static void Append(Dictionary<string, List<string>> rings, string key, IEnumerable<string> cells, int cap)
    {
        if (!rings.TryGetValue(key, out List<string>? ring))
        {
            ring = [];
            rings[key] = ring;
        }

        ring.AddRange(cells);
        if (ring.Count > cap) ring.RemoveRange(0, ring.Count - cap);
    }
}
