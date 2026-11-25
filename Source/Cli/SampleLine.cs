using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RimRunnerCli;

internal static class SampleLine
{
    internal const string SealKey = "seal";
    internal const string ModListKey = "modList";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal sealed record Row(string Key, string FieldsJson, string ValuesJson);

    internal sealed record Record(
        string RunId, string? Timestamp, string? CaseId, string? Series, string? Segment, long? WindowTicks,
        string? ModList, string? MvidsJson, string? ScopeJson, IReadOnlyList<Row> Rows, string? Raw);

    internal sealed record Seal(string RunId, string? Status, long? Segments, string? Timestamp);

    internal static bool IsSeal(string line, out string runId)
    {
        using JsonDocument document = ParseObject(line);
        runId = RunIdOf(document.RootElement);
        return document.RootElement.TryGetProperty(SealKey, out _);
    }

    internal static bool Parses(string line)
    {
        try
        {
            IsSeal(line, out _);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static Seal ParseSeal(string line)
    {
        using JsonDocument document = ParseObject(line);
        JsonElement root = document.RootElement;
        return new Seal(RunIdOf(root),
            root.TryGetProperty(SealKey, out JsonElement status) && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null,
            root.TryGetProperty("segments", out JsonElement segments) && segments.TryGetInt64(out long count)
                ? count
                : null,
            TextOf(root, "timestamp"));
    }

    internal static Record ParseRecord(string line)
    {
        Record? split = TrySplit(line);
        if (split != null && Render(split) == line) return split;

        using JsonDocument document = ParseObject(line);
        JsonElement root = document.RootElement;
        return new Record(RunIdOf(root), TextOf(root, "timestamp"), TextOf(root, "caseId"), TextOf(root, "series"),
            TextOf(root, "segment"),
            root.TryGetProperty("windowTicks", out JsonElement ticks) && ticks.TryGetInt64(out long value) ? value : null,
            TextOf(root, ModListKey), null, null, [], line);
    }

    internal static string Render(Record record)
    {
        if (record.Raw != null) return record.Raw;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("runId", record.RunId);
            writer.WriteString("timestamp", record.Timestamp);
            writer.WriteString("caseId", record.CaseId);
            writer.WriteString("series", record.Series);
            writer.WriteString("segment", record.Segment);
            writer.WriteNumber("windowTicks", record.WindowTicks!.Value);
            writer.WriteString(ModListKey, record.ModList);
            writer.WritePropertyName("assemblyMvids");
            writer.WriteRawValue(record.MvidsJson!, skipInputValidation: true);
            writer.WritePropertyName("scope");
            writer.WriteRawValue(record.ScopeJson!, skipInputValidation: true);
            writer.WriteStartArray("rows");
            foreach (Row row in record.Rows)
            {
                writer.WriteStartObject();
                writer.WriteString("key", row.Key);
                writer.WriteStartObject("values");
                string[] fields = JsonSerializer.Deserialize<string[]>(row.FieldsJson)!;
                List<string> values = RawItems(row.ValuesJson);
                for (int index = 0; index < fields.Length; index++)
                {
                    writer.WritePropertyName(fields[index]);
                    writer.WriteRawValue(values[index], skipInputValidation: true);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string RenameLegacyModListKey(string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        var reader = new Utf8JsonReader(bytes);
        int start = -1, length = 0;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return line;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals(ModListKey)) return line;
                if (start < 0 && reader.ValueTextEquals("modSet"))
                {
                    start = (int)reader.TokenStartIndex;
                    length = reader.ValueSpan.Length + 2;
                }

                reader.Skip();
            }
        }
        catch (JsonException)
        {
            return line;
        }

        if (start < 0) return line;
        return Encoding.UTF8.GetString(bytes, 0, start) + "\"" + ModListKey + "\""
            + Encoding.UTF8.GetString(bytes, start + length, bytes.Length - start - length);
    }

    private static Record? TrySplit(string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        var reader = new Utf8JsonReader(bytes);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            if (StringProperty(ref reader, "runId") is not string runId) return null;
            if (!NullableString(ref reader, "timestamp", out string? timestamp)) return null;
            if (!NullableString(ref reader, "caseId", out string? caseId)) return null;
            if (!NullableString(ref reader, "series", out string? series)) return null;
            if (!NullableString(ref reader, "segment", out string? segment)) return null;
            if (!Property(ref reader, "windowTicks") || reader.TokenType != JsonTokenType.Number
                || !reader.TryGetInt64(out long windowTicks)) return null;
            if (!NullableString(ref reader, ModListKey, out string? modList)) return null;
            if (RawObject(ref reader, bytes, "assemblyMvids") is not string mvids) return null;
            if (RawObject(ref reader, bytes, "scope") is not string scope) return null;
            if (!Property(ref reader, "rows") || reader.TokenType != JsonTokenType.StartArray) return null;

            var rows = new List<Row>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject) return null;
                if (StringProperty(ref reader, "key") is not string key) return null;
                if (!Property(ref reader, "values") || reader.TokenType != JsonTokenType.StartObject) return null;
                var fields = new List<string>();
                var values = new StringBuilder("[");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    fields.Add(reader.GetString()!);
                    reader.Read();
                    long start = reader.TokenStartIndex;
                    reader.Skip();
                    if (values.Length > 1) values.Append(',');
                    values.Append(Encoding.UTF8.GetString(bytes, (int)start, (int)(reader.BytesConsumed - start)));
                }

                if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject) return null;
                rows.Add(new Row(key, FieldsJson(fields), values.Append(']').ToString()));
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject || reader.Read()) return null;
            return new Record(runId, timestamp, caseId, series, segment, windowTicks, modList, mvids, scope, rows, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string FieldsJson(IReadOnlyList<string> fields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (string field in fields) writer.WriteStringValue(field);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static List<string> RawItems(string arrayJson)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(arrayJson);
        var reader = new Utf8JsonReader(bytes);
        reader.Read();
        var items = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            long start = reader.TokenStartIndex;
            reader.Skip();
            items.Add(Encoding.UTF8.GetString(bytes, (int)start, (int)(reader.BytesConsumed - start)));
        }

        return items;
    }

    private static bool Property(ref Utf8JsonReader reader, string name) =>
        reader.Read() && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals(name) && reader.Read();

    private static string? StringProperty(ref Utf8JsonReader reader, string name) =>
        Property(ref reader, name) && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

    private static bool NullableString(ref Utf8JsonReader reader, string name, out string? value)
    {
        value = null;
        if (!Property(ref reader, name)) return false;
        if (reader.TokenType == JsonTokenType.Null) return true;
        if (reader.TokenType != JsonTokenType.String) return false;
        value = reader.GetString();
        return true;
    }

    private static string? RawObject(ref Utf8JsonReader reader, byte[] bytes, string name)
    {
        if (!Property(ref reader, name) || reader.TokenType != JsonTokenType.StartObject) return null;
        long start = reader.TokenStartIndex;
        reader.Skip();
        return Encoding.UTF8.GetString(bytes, (int)start, (int)(reader.BytesConsumed - start));
    }

    private static JsonDocument ParseObject(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("读数库的行不是 JSON：" + exception.Message);
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose();
        throw new InvalidDataException("读数库的行不是 JSON 对象");
    }

    private static string RunIdOf(JsonElement root) =>
        TextOf(root, "runId") is { Length: > 0 } runId
            ? runId
            : throw new InvalidDataException("读数库的行没有 runId");

    private static string? TextOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
