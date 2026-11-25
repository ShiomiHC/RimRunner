using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RimRunner;

/// <summary>JSON 通道：严格解析期望表，以及报告字符串转义。</summary>
public static class RimRunnerJson
{
    /// <summary>解析 JSON 文本为对象、列表或基元。</summary>
    /// <remarks>读成 float：超过 2^24 的整数往返即失精（时间戳、累计 tick 数写成字符串）；重复键、根值后非空白、NaN/Infinity 或超深嵌套即抛；错误为 FormatException，带字符位置与附近文本。</remarks>
    public static object? Parse(string text)
    {
        if (text == null)
        {
            throw new FormatException(
                RimRunnerLog.Prefix + " 无法解析 JSON @ 位置 0: 输入文本为 null；附近文本: \"\"");
        }

        return new Parser(text).Parse();
    }

    internal static string Write(Action<Utf8JsonWriter> body, bool indented)
    {
        ArrayBufferWriter<byte> buffer = new();
        Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
        {
            Indented = indented,
            IndentSize = 4,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        body(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>往 sb 写一个已转义的 JSON 字符串。</summary>
    /// <remarks>孤立代理项替成 U+FFFD，与 Write 同一口径；成对的代理项原样写出。</remarks>
    public static void WriteString(StringBuilder sb, string? value)
    {
        if (value == null)
        {
            sb.Append("null");
            return;
        }

        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            switch (character)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(character) && i + 1 < value.Length
                        && char.IsLowSurrogate(value[i + 1]))
                    {
                        sb.Append(character);
                        sb.Append(value[++i]);
                    }
                    else if (char.IsSurrogate(character))
                    {
                        sb.Append('\uFFFD');
                    }
                    else
                    {
                        sb.Append(character);
                    }
                    break;
            }
        }

        sb.Append('"');
    }

    private sealed class Parser(string text)
    {
        private const int MaxDepth = 256;

        private readonly string text = text;
        private readonly byte[] utf8 = Encoding.UTF8.GetBytes(text);

        public object? Parse()
        {
            if (text.AsSpan().TrimStart(" \t\r\n").IsEmpty)
            {
                throw CreateError(0, "JSON 文本为空");
            }

            Utf8JsonReader reader = new(utf8, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = MaxDepth,
            });
            try
            {
                Advance(ref reader);
                object? value = ParseValue(ref reader);
                if (reader.Read())
                {
                    throw CreateError(reader.TokenStartIndex, "根值后存在非空白字符");
                }

                return value;
            }
            catch (JsonException exception)
            {
                throw CreateError(ByteOffsetOf(exception, reader.BytesConsumed), "语法错误（" + exception.Message + "）");
            }
            catch (InvalidOperationException exception)
            {
                throw CreateError(reader.TokenStartIndex, "字符串无法解码（" + exception.Message + "）");
            }
        }

        private static void Advance(ref Utf8JsonReader reader)
        {
            if (!reader.Read())
            {
                throw new JsonException("缺少 JSON 值");
            }
        }

        private object? ParseValue(ref Utf8JsonReader reader)
        {
            return reader.TokenType switch
            {
                JsonTokenType.StartObject => ParseObject(ref reader),
                JsonTokenType.StartArray => ParseArray(ref reader),
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.Null => null,
                JsonTokenType.Number => ParseNumber(ref reader),
                _ => throw CreateError(reader.TokenStartIndex, "当前记号不是 JSON 值"),
            };
        }

        private Dictionary<string, object?> ParseObject(ref Utf8JsonReader reader)
        {
            Dictionary<string, object?> result = new(StringComparer.Ordinal);
            while (true)
            {
                Advance(ref reader);
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return result;
                }

                long keyStart = reader.TokenStartIndex;
                string key = reader.GetString()!;
                if (result.ContainsKey(key))
                {
                    throw CreateError(keyStart, "对象键重复");
                }

                Advance(ref reader);
                result.Add(key, ParseValue(ref reader));
            }
        }

        private List<object?> ParseArray(ref Utf8JsonReader reader)
        {
            List<object?> result = [];
            while (true)
            {
                Advance(ref reader);
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return result;
                }

                result.Add(ParseValue(ref reader));
            }
        }

        private float ParseNumber(ref Utf8JsonReader reader)
        {
            if (!float.TryParse(reader.ValueSpan,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture,
                    out float value) || float.IsNaN(value) || float.IsInfinity(value))
            {
                throw CreateError(reader.TokenStartIndex, "数字超出 float 可表示范围");
            }

            return value;
        }

        private long ByteOffsetOf(JsonException exception, long fallback)
        {
            if (exception.LineNumber is not long line || exception.BytePositionInLine is not long column)
            {
                return fallback;
            }

            long offset = 0;
            for (long seen = 0; seen < line && offset < utf8.Length; offset++)
            {
                if (utf8[offset] == (byte)'\n') seen++;
            }

            return offset + column;
        }

        private FormatException CreateError(long byteOffset, string message)
        {
            int position = Encoding.UTF8.GetCharCount(utf8, 0, (int)Math.Min(byteOffset, utf8.Length));
            int snippetStart = Math.Max(0, position - 16);
            int snippetEnd = Math.Min(text.Length, position + 16);
            string snippet = text[snippetStart..snippetEnd]
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
            return new FormatException(
                RimRunnerLog.Prefix + " 无法解析 JSON @ 位置 " + position + ": " + message
                + "；附近文本: \"" + snippet + "\"");
        }
    }
}
