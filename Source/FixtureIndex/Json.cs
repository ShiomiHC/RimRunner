using System.Globalization;
using System.Text;

namespace RimRunnerFixtureIndex;

internal static class Json
{
    internal static string Write(object? value)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value, 0);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object? value, int depth)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case bool flag:
                sb.Append(flag ? "true" : "false");
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case IEnumerable<KeyValuePair<string, object?>> obj:
                WriteObject(sb, obj.ToList(), depth);
                break;
            case System.Collections.IEnumerable list:
                WriteArray(sb, list.Cast<object?>().ToList(), depth);
                break;
            default:
                throw new InvalidOperationException($"不支持的 JSON 值类型：{value.GetType()}");
        }
    }

    private static void WriteObject(StringBuilder sb, List<KeyValuePair<string, object?>> fields, int depth)
    {
        if (fields.Count == 0) { sb.Append("{}"); return; }
        sb.Append("{\n");
        for (int i = 0; i < fields.Count; i++)
        {
            Indent(sb, depth + 1);
            WriteString(sb, fields[i].Key);
            sb.Append(": ");
            WriteValue(sb, fields[i].Value, depth + 1);
            sb.Append(i + 1 < fields.Count ? ",\n" : "\n");
        }
        Indent(sb, depth);
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, List<object?> items, int depth)
    {
        if (items.Count == 0) { sb.Append("[]"); return; }
        sb.Append("[\n");
        for (int i = 0; i < items.Count; i++)
        {
            Indent(sb, depth + 1);
            WriteValue(sb, items[i], depth + 1);
            sb.Append(i + 1 < items.Count ? ",\n" : "\n");
        }
        Indent(sb, depth);
        sb.Append(']');
    }

    private static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 2);

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    bool loneSurrogate = char.IsSurrogate(c)
                        && !(char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        && !(char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(s[i - 1]));
                    if (c < 0x20 || loneSurrogate) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
