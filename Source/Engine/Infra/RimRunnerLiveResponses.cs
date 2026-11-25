using System.Text.Json;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    internal static string InjectSchema(string schema, Action<Utf8JsonWriter> members)
    {
        return RimRunnerJson.Write(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString(RimRunnerLiveContract.KeySchema, schema);
                members(writer);
                writer.WriteEndObject();
            },
            false);
    }

    private static LiveResponse BusyResponse(RimRunnerRunSnapshot activeRun)
    {
        return JsonResponse(409, InjectSchema(RimRunnerLiveContract.Schema.Error, writer =>
        {
            writer.WriteString(RimRunnerLiveContract.Error.Message, "runner busy");
            writer.WriteString(
                RimRunnerLiveContract.Error.State, activeRun.State.ToString().ToLowerInvariant());
            writer.WriteString(RimRunnerLiveContract.Error.ActiveCommand, activeRun.Command);
            writer.WriteString(RimRunnerLiveContract.Error.ActiveRunId, activeRun.RunId);
            writer.WriteString(RimRunnerLiveContract.Error.Hint,
                "runner 忙时 stat、hediffs、pawns、run、save、drive 一律 409；"
                + "仍可查：progress、screenshot、drive.status");
        }));
    }

    private static LiveResponse ErrorWithAvailablePawns(
        int statusCode, string message, IEnumerable<Pawn> pawns)
    {
        return ErrorWithAvailable(statusCode, message, pawns.Select(GetPawnNick));
    }

    private static LiveResponse ErrorWithAvailable(
        int statusCode, string message, IEnumerable<string?> values)
    {
        return JsonResponse(statusCode, InjectSchema(RimRunnerLiveContract.Schema.Error, writer =>
        {
            writer.WriteString(RimRunnerLiveContract.Error.Message, message);
            writer.WriteStartArray(RimRunnerLiveContract.Error.Available);
            foreach (string? value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }));
    }

    private static LiveResponse ErrorResponse(int statusCode, string message)
    {
        return JsonResponse(statusCode, InjectSchema(RimRunnerLiveContract.Schema.Error, writer =>
            writer.WriteString(RimRunnerLiveContract.Error.Message, message)));
    }

    private static LiveResponse JsonResponse(int statusCode, string body)
    {
        return new LiveResponse
        {
            StatusCode = statusCode,
            Body = body
        };
    }

    internal sealed class LiveResponse
    {
        public int StatusCode;
        public required string Body;
    }
}
