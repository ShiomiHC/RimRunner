using System.Text.Json;

namespace RimRunner;

internal sealed class RimRunnerRequestParams
{
    private readonly JsonElement root;

    internal RimRunnerRequestParams(JsonElement request)
    {
        if (!request.TryGetProperty(RimRunnerPipeProtocol.Params, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new RimRunnerRequestException(400, RimRunnerPipeProtocol.Params + " 须是 JSON 对象");
        }

        root = value;
    }

    internal void RequireOnly(params string[] allowed)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                throw new RimRunnerRequestException(400, "未识别的参数名：" + property.Name + "。可用参数："
                                                         + (allowed.Length == 0 ? "无" : string.Join("、", allowed)));
            }
        }
    }

    internal string? Text(string key)
    {
        if (!TryGet(key, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw TypeMismatch(key, "字符串", value);
    }

    internal string RequireText(string key, string meaning)
    {
        string? value = Text(key);
        return string.IsNullOrEmpty(value)
            ? throw new RimRunnerRequestException(400, "需要 " + key + " 参数（" + meaning + "）")
            : value;
    }

    internal bool? Bool(string key)
    {
        if (!TryGet(key, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw TypeMismatch(key, "布尔", value),
        };
    }

    internal int? Int(string key)
    {
        if (!TryGet(key, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : throw TypeMismatch(key, "整数", value);
    }

    internal IReadOnlyList<string>? Texts(string key)
    {
        if (!TryGet(key, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw TypeMismatch(key, "字符串数组", value);
        }

        var items = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            items.Add(item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw TypeMismatch(key, "字符串数组", value));
        }

        return items;
    }

    internal IReadOnlyDictionary<string, string>? TextMap(string key)
    {
        if (!TryGet(key, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw TypeMismatch(key, "以字符串为值的对象", value);
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()!
                : throw TypeMismatch(key + "." + property.Name, "字符串", property.Value);
        }

        return map;
    }

    internal IReadOnlyList<JsonElement>? Objects(string key)
    {
        if (!TryGet(key, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object))
        {
            throw TypeMismatch(key, "对象数组", value);
        }

        return [.. value.EnumerateArray()];
    }

    internal IReadOnlyList<double>? Numbers(string key)
    {
        if (!TryGet(key, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw TypeMismatch(key, "数字数组", value);
        }

        var items = new List<double>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            items.Add(item.ValueKind == JsonValueKind.Number
                ? item.GetDouble()
                : throw TypeMismatch(key, "数字数组", value));
        }

        return items;
    }

    internal List<KeyValuePair<string, string>> Pairs(string key, string head)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        if (!TryGet(key, out JsonElement value))
        {
            return pairs;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw TypeMismatch(key, "对象数组", value);
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty(RimRunnerPipeProtocol.PairKey, out JsonElement pairKey)
                || pairKey.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(pairKey.GetString())
                || !item.TryGetProperty(RimRunnerPipeProtocol.PairText, out JsonElement text)
                || text.ValueKind != JsonValueKind.String)
            {
                throw new RimRunnerRequestException(400, key + " 的每一项须是 {" + RimRunnerPipeProtocol.PairKey + ", "
                                                         + RimRunnerPipeProtocol.PairText + "} 形式的对象，两项都是字符串，"
                                                         + RimRunnerPipeProtocol.PairKey + "（" + head
                                                         + "）不能为空：" + item.GetRawText());
            }

            pairs.Add(new KeyValuePair<string, string>(pairKey.GetString()!, text.GetString()!));
        }

        return pairs;
    }

    private bool TryGet(string key, out JsonElement value)
    {
        value = default;
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(key, out value)
               && value.ValueKind != JsonValueKind.Null;
    }

    private static RimRunnerRequestException TypeMismatch(string key, string expected, JsonElement value)
    {
        return new RimRunnerRequestException(400, key + " 须是" + expected + "，收到：" + value.GetRawText());
    }
}

internal sealed class RimRunnerRequestException(int status, string message) : Exception(message)
{
    internal int Status { get; } = status;
}
