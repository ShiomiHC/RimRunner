using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

public static class LiveDriveClient
{
    public const int WaitMinutesDefault = 2;
    public const string AfterWaitBackground = "background";
    public const string AfterWaitCancel = "cancel";
    public const int LimitMinutesDefault = RimRunnerLiveContract.Drive.DefaultLimitSeconds / 60;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CancelSettleLimit = TimeSpan.FromSeconds(30);

    public sealed record WaitPolicy(TimeSpan? Foreground, bool CancelAfterWait, int LimitSeconds);

    public static int Run(int number, string kind, string verb, IReadOnlyDictionary<string, string> args, bool json,
        Action<string> output, int? longEventSeconds = null, WaitPolicy? policy = null, byte[]? image = null)
    {
        Action<string> screen = output;
        Action<string> raw = line => CliLog.Machine(() => screen(line));
        output = line => FindingPresentation.ScreenCapped(line, screen);
        (int status, string body) = Send(number, RimRunnerPipeProtocol.Ops.Drive, writer =>
        {
            writer.WriteString(RimRunnerLiveContract.Drive.Kind, kind);
            writer.WriteString(RimRunnerLiveContract.Drive.Verb, verb);
            writer.WriteStartObject(RimRunnerLiveContract.Drive.Args);
            foreach (KeyValuePair<string, string> pair in args)
                writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject();
            if (longEventSeconds is int seconds)
                writer.WriteNumber(RimRunnerLiveContract.Drive.LongEventSeconds, seconds);
            if (policy != null)
                writer.WriteNumber(RimRunnerLiveContract.Drive.LimitSeconds, policy.LimitSeconds);
            if (image != null)
                writer.WriteBase64String(RimRunnerLiveContract.Drive.Assembly, image);
        });
        if (status == 400 && ErrorMessage(body) is { } argError)
        {
            output($"[FAIL] live {kind} {verb} 被拒，未执行：{argError}");
            return ExitCodes.Rejected;
        }

        if (status != 202)
            return Rejected(status, body, output);

        string? id = Id(body);
        if (id == null)
        {
            output("[FAIL] drive 受理回包缺少操作 id");
            return ExitCodes.Incomplete;
        }

        return Follow(number, id, json, output, raw, policy, cancelNow: false);
    }

    public static int Status(int number, string id, bool json, Action<string> output)
    {
        Action<string> screen = output;
        Action<string> raw = line => CliLog.Machine(() => screen(line));
        output = line => FindingPresentation.ScreenCapped(line, screen);
        (int status, string body) = Send(number, RimRunnerPipeProtocol.Ops.DriveStatus,
            writer => writer.WriteString(RimRunnerLiveContract.Drive.Id, id));
        if (status != 200)
            return Rejected(status, body, output, unknownIdRejected: true);

        using JsonDocument doc = JsonDocument.Parse(body);
        if (LivePresentation.SchemaMismatch(doc.RootElement, RimRunnerLiveContract.Schema.Drive) is { } mismatch)
        {
            output("[FAIL] " + mismatch);
            return ExitCodes.Incomplete;
        }

        if (DrivePresentation.IsSettled(doc.RootElement))
            return Settled(doc.RootElement, json, output, raw);
        if (json)
            raw(doc.RootElement.GetRawText());
        else
            output(DrivePresentation.Running(doc.RootElement));
        return ExitCodes.Incomplete;
    }

    public static int Cancel(int number, string id, bool json, Action<string> output)
    {
        Action<string> screen = output;
        Action<string> raw = line => CliLog.Machine(() => screen(line));
        output = line => FindingPresentation.ScreenCapped(line, screen);
        return Follow(number, id, json, output, raw, null, cancelNow: true);
    }

    private static int Follow(int number, string id, bool json, Action<string> output, Action<string> raw,
        WaitPolicy? policy, bool cancelNow)
    {
        CancellationToken gone = CommandContext.ClientGone;
        DateTime started = DateTime.UtcNow;
        DateTime? cancelledAt = null;
        while (true)
        {
            bool waitedOut = policy?.Foreground is { } wait && DateTime.UtcNow - started >= wait;
            if (cancelledAt == null && (cancelNow || gone.IsCancellationRequested || waitedOut && policy!.CancelAfterWait))
            {
                (int cancelStatus, string cancelBody) = Send(number, RimRunnerPipeProtocol.Ops.DriveCancel,
                    writer => writer.WriteString(RimRunnerLiveContract.Drive.Id, id));
                if (cancelNow && cancelStatus is not (200 or 202))
                    return Rejected(cancelStatus, cancelBody, output, unknownIdRejected: true);
                if (waitedOut && !cancelNow && !gone.IsCancellationRequested && !json)
                    output($"[中止] 操作 {id} 前台等满 {Minutes(policy!.Foreground!.Value)} 仍未结束，"
                           + $"按 {RimRunnerConfig.LiveAfterWaitKey} = {AfterWaitCancel} 请求取消");
                cancelledAt = DateTime.UtcNow;
            }

            (int status, string body) = Send(number, RimRunnerPipeProtocol.Ops.DriveStatus,
                writer => writer.WriteString(RimRunnerLiveContract.Drive.Id, id));
            if (status != 200)
                return Rejected(status, body, output);

            using JsonDocument doc = JsonDocument.Parse(body);
            if (LivePresentation.SchemaMismatch(doc.RootElement, RimRunnerLiveContract.Schema.Drive) is { } mismatch)
            {
                output("[FAIL] " + mismatch);
                return ExitCodes.Incomplete;
            }

            if (DrivePresentation.IsSettled(doc.RootElement))
                return Settled(doc.RootElement, json, output, raw);

            if (cancelledAt is { } at && DateTime.UtcNow - at > CancelSettleLimit)
            {
                output($"[FAIL] 已请求取消操作 {id}，{(int)CancelSettleLimit.TotalSeconds}s 内回执仍未落定");
                return ExitCodes.Incomplete;
            }

            if (waitedOut && cancelledAt == null)
            {
                if (json)
                {
                    raw(doc.RootElement.GetRawText());
                    return ExitCodes.Incomplete;
                }

                output($"[后台操作] 操作 {id} 持续 {Minutes(policy!.Foreground!.Value)} 仍在执行，转入后台执行"
                       + (policy.LimitSeconds > 0 ? $"（总上限 {policy.LimitSeconds / 60} 分钟）" : ""));
                return ExitCodes.Incomplete;
            }

            Thread.Sleep(PollInterval);
        }
    }

    private static int Settled(JsonElement root, bool json, Action<string> output, Action<string> raw)
    {
        IReadOnlyList<string> lines = DrivePresentation.Format(root, out int exitCode);
        if (json)
            raw(root.GetRawText());
        else
            foreach (string line in lines)
                output(line);
        return exitCode;
    }

    private static string Minutes(TimeSpan span) =>
        span.TotalMinutes >= 1 && span.TotalMinutes == Math.Floor(span.TotalMinutes)
            ? $"{(int)span.TotalMinutes} 分钟"
            : $"{span.TotalSeconds:0.#} 秒";

    private static int Rejected(int status, string body, Action<string> output, bool unknownIdRejected = false)
    {
        if (status == 0)
        {
            output("[FAIL] " + body);
            return ExitCodes.Incomplete;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            foreach (string line in LivePresentation.FormatError(doc.RootElement))
                output(line);
        }
        catch (JsonException)
        {
            output($"[FAIL] 回包不是 JSON（状态码 {status}）");
            return ExitCodes.Incomplete;
        }

        return status is 409 or 400 or 503 || status == 404 && unknownIdRejected ? ExitCodes.Rejected : ExitCodes.Incomplete;
    }

    private static string? ErrorMessage(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(RimRunnerLiveContract.Error.Message, out JsonElement message)
                   && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Id(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(RimRunnerLiveContract.Drive.Id, out JsonElement id)
                   && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (int Status, string Body) Send(int number, string op, Action<Utf8JsonWriter>? parameters)
    {
        InstanceLink.Reply reply = InstanceLinks.Request(number, op, parameters, RequestTimeout);
        return reply.Status <= InstanceLink.Reply.NoLinkStatus
            ? (0, reply.ErrorText())
            : (reply.Status, reply.Body ?? "");
    }
}
