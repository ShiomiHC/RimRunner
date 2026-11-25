using System.Text.Json;

namespace RimRunnerCli;

internal static class StrictJson
{
    private static readonly JsonDocumentOptions Options = new() { AllowDuplicateProperties = false };

    internal static JsonDocument ParseObject(string text)
    {
        JsonDocument document = JsonDocument.Parse(text, Options);
        JsonValueKind kind = document.RootElement.ValueKind;
        if (kind == JsonValueKind.Object) return document;
        document.Dispose();
        throw new JsonException($"根不是 JSON 对象（是 {kind}）");
    }
}
