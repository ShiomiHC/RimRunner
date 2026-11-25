using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RimRunner;

internal sealed class RimRunnerPipeFrame : IDisposable
{
    public const int Protocol = 1;
    public const string ProtocolKey = "protocol";
    public const string BinaryKey = "bin";
    internal const int PrefixBytes = 4;

    internal const int MaxFrameBytes = 4 * 1024 * 1024;

    private readonly JsonDocument document;
    private readonly byte[] buffer;
    private readonly int binaryOffset;

    private RimRunnerPipeFrame(JsonDocument document, byte[] buffer, int binaryOffset)
    {
        this.document = document;
        this.buffer = buffer;
        this.binaryOffset = binaryOffset;
    }

    public JsonElement Root => document.RootElement;

    public ReadOnlyMemory<byte> Binary => buffer.AsMemory(binaryOffset);

    public void Dispose() => document.Dispose();

    public static byte[] Encode(ReadOnlySpan<byte> json, ReadOnlySpan<byte> binary = default)
    {
        byte[] frame = new byte[PrefixBytes + json.Length + binary.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length + binary.Length);
        json.CopyTo(frame.AsSpan(PrefixBytes));
        binary.CopyTo(frame.AsSpan(PrefixBytes + json.Length));
        return frame;
    }

    public static byte[] EncodeObject(Action<Utf8JsonWriter> members, ReadOnlySpan<byte> binary = default)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(ProtocolKey, Protocol);
            members(writer);
            if (binary.Length > 0)
            {
                writer.WriteNumber(BinaryKey, binary.Length);
            }

            writer.WriteEndObject();
        }

        return Encode(buffer.ToArray(), binary);
    }

    public static string? ProtocolMismatch(JsonElement root)
    {
        int theirs = root.TryGetProperty(ProtocolKey, out JsonElement value)
                     && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : 0;
        return theirs == Protocol ? null : "协议版本不匹配：对端 " + theirs + "，本端 " + Protocol;
    }

    public static bool IsPeerClosed(Exception exception) =>
        exception is IOException io && (io.HResult & 0xFFFF) is 109 or 232 or 233;

    public static RimRunnerPipeFrame? Read(Stream stream, int maxBytes)
    {
        byte[] prefix = new byte[PrefixBytes];
        if (!ReadExactly(stream, prefix, allowCleanEnd: true))
        {
            return null;
        }

        byte[] body = new byte[CheckedLength(prefix, maxBytes)];
        ReadExactly(stream, body, allowCleanEnd: false);
        return Parse(body);
    }

    public static async Task<RimRunnerPipeFrame?> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellation)
    {
        byte[] prefix = new byte[PrefixBytes];
        if (!await ReadExactlyAsync(stream, prefix, allowCleanEnd: true, cancellation).ConfigureAwait(false))
        {
            return null;
        }

        byte[] body = new byte[CheckedLength(prefix, maxBytes)];
        await ReadExactlyAsync(stream, body, allowCleanEnd: false, cancellation).ConfigureAwait(false);
        return Parse(body);
    }

    private static int CheckedLength(byte[] prefix, int maxBytes)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > maxBytes)
        {
            throw new RimRunnerPipeProtocolException(
                "帧长度 " + length + " 不在 1.." + maxBytes + " 之内：对端的报文不符合本协议");
        }

        return length;
    }

    private static RimRunnerPipeFrame Parse(byte[] body)
    {
        int jsonLength;
        try
        {
            var reader = new Utf8JsonReader(body, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new RimRunnerPipeProtocolException("帧正文不以 JSON 对象开头");
            }

            reader.Skip();
            jsonLength = (int)reader.BytesConsumed;
        }
        catch (JsonException exception)
        {
            throw new RimRunnerPipeProtocolException("帧正文不是 JSON：" + exception.Message);
        }

        JsonDocument document = JsonDocument.Parse(body.AsMemory(0, jsonLength));
        int binaryLength = body.Length - jsonLength;
        int declared = document.RootElement.TryGetProperty(BinaryKey, out JsonElement bin)
                       && bin.ValueKind == JsonValueKind.Number
            ? bin.GetInt32()
            : 0;
        if (declared != binaryLength)
        {
            document.Dispose();
            throw new RimRunnerPipeProtocolException(
                "帧尾字节数 " + binaryLength + " 与 JSON 里声明的 " + declared + " 不匹配");
        }

        return new RimRunnerPipeFrame(document, body, jsonLength);
    }

    private static bool ReadExactly(Stream stream, byte[] target, bool allowCleanEnd)
    {
        int filled = 0;
        while (filled < target.Length)
        {
            int read = stream.Read(target, filled, target.Length - filled);
            if (read == 0)
            {
                if (filled == 0 && allowCleanEnd)
                {
                    return false;
                }

                throw new EndOfStreamException("连接在半帧处断开");
            }

            filled += read;
        }

        return true;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] target, bool allowCleanEnd,
        CancellationToken cancellation)
    {
        int filled = 0;
        while (filled < target.Length)
        {
            int read = await stream.ReadAsync(target.AsMemory(filled), cancellation).ConfigureAwait(false);
            if (read == 0)
            {
                if (filled == 0 && allowCleanEnd)
                {
                    return false;
                }

                throw new EndOfStreamException("连接在半帧处断开");
            }

            filled += read;
        }

        return true;
    }
}

internal sealed class RimRunnerPipeProtocolException(string message) : Exception(message);
