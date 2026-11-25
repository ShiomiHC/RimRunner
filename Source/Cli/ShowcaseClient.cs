using System.Text.Json;

namespace RimRunnerCli;

public static class ShowcaseClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(70);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public delegate IReadOnlyList<string> ShowcaseFormatter(int status, string body, out int exitCode);

    public static int Stage(int number, string id, int? timeoutMinutes, Action<string> log) =>
        SendAndSettle(number, RimRunner.RimRunnerPipeProtocol.Ops.ShowcaseStage,
            writer => writer.WriteString("id", id), timeoutMinutes, "立场 " + id,
            ShowcasePresentation.Format, log);

    public static int Teardown(int number, int? timeoutMinutes, Action<string> log) =>
        SendAndSettle(number, RimRunner.RimRunnerPipeProtocol.Ops.ShowcaseTeardown, null, timeoutMinutes, "拆场",
            ShowcasePresentation.Format, log);

    public static string FetchSummary(int number)
    {
        (int status, string body) = Send(number, RimRunner.RimRunnerPipeProtocol.Ops.Showcase, null);
        return status == 0 ? "展台：状态查询失败（" + body + "）" : ShowcasePresentation.Summary(status, body);
    }

    public static int Step(int number, string name, int? timeoutMinutes, Action<string> log) =>
        SendAndSettle(number, RimRunner.RimRunnerPipeProtocol.Ops.ShowcaseStep,
            writer => writer.WriteString("name", name), timeoutMinutes, "步骤「" + name + "」",
            ShowcasePresentation.FormatStep, log);

    private static int SendAndSettle(int number, string op, Action<Utf8JsonWriter>? parameters, int? timeoutMinutes,
        string what, ShowcaseFormatter format, Action<string> log)
    {
        int timeout = TimeoutPolicy.Resolve(timeoutMinutes, log);
        ActivityLog.Touch(ActivityLog.Showcase, number, detail: op);
        (int status, string body) = Send(number, op, parameters);
        if (status != 202) return Report(status, body, format, log);

        CancellationToken gone = CommandContext.ClientGone;
        WaitWatch? watch = WaitWatch.For(timeout, DateTime.UtcNow);
        while (true)
        {
            (status, body) = Send(number, RimRunner.RimRunnerPipeProtocol.Ops.Showcase, null);
            if (status == 200 && ShowcasePresentation.IsSettled(body)) return Report(status, body, format, log);
            if (status is not 200 and not 503) return Report(status, body, format, log);
            if (watch != null
                && watch.Observe(DateTime.UtcNow, Pulse(watch, number), body,
                    $"{what}；实例 {ClaimStore.Named(number)}") is string failure)
            {
                log($"[FAIL] {failure}");
                return ExitCodes.Incomplete;
            }

            if (gone.WaitHandle.WaitOne(PollInterval))
            {
                log($"[FAIL] 发起命令的客户端已断开，停止等待{what}落定；实例上的{what}不取消");
                return ExitCodes.Incomplete;
            }
        }
    }

    private static string? Pulse(WaitWatch watch, int number)
    {
        if (InstanceLinks.Get(number) is { } link) return watch.NextPulse(link);
        InstanceLink.Reply reply = InstanceLinks.RequestLatestProgress(number, RequestTimeout);
        return reply.Ok ? reply.Body : null;
    }

    private static int Report(int status, string body, ShowcaseFormatter format, Action<string> log)
    {
        IReadOnlyList<string> lines = format(status, body, out int exitCode);
        foreach (string line in lines)
            log(line);
        return exitCode;
    }

    private static (int Status, string Body) Send(int number, string op, Action<Utf8JsonWriter>? parameters)
    {
        InstanceLink.Reply reply = InstanceLinks.Request(number, op, parameters, RequestTimeout);
        return reply.Status <= InstanceLink.Reply.NoLinkStatus
            ? (0, reply.ErrorText())
            : (reply.Status, reply.Body ?? "");
    }
}
