namespace RimRunnerCli;

public sealed class WaitWatch
{
    private readonly int timeoutMinutes;
    private readonly DateTime start;
    private readonly FrozenFrameWatch frozen = new();
    private readonly ProgressStallWatch stall;
    private DateTime? fedAt;

    private WaitWatch(int timeoutMinutes, DateTime start)
    {
        this.timeoutMinutes = timeoutMinutes;
        this.start = start;
        stall = new ProgressStallWatch(TimeSpan.FromMinutes(timeoutMinutes), start);
    }

    public static WaitWatch? For(int timeoutMinutes, DateTime start) =>
        timeoutMinutes == 0 ? null : new WaitWatch(timeoutMinutes, start);

    internal string? NextPulse(InstanceLink link)
    {
        InstanceLink.Pulse? pulse = link.LatestPulse;
        if (pulse == null || pulse.ReceivedAt == fedAt) return null;
        fedAt = pulse.ReceivedAt;
        return pulse.Body;
    }

    public string? Observe(DateTime now, string? body, string runId, int number) =>
        Observe(now, body, null, $"runId={runId}；实例 {ClaimStore.Named(number)}", "，未收到匹配报告");

    public string? Observe(DateTime now, string? body, string? extra, string where) =>
        Observe(now, body, extra, where, "");

    private string? Observe(DateTime now, string? body, string? extra, string where, string missing)
    {
        if (frozen.Observe(now, body) is TimeSpan stuck)
            return $"帧号 {stuck.TotalMinutes:0} 分钟未前进、推进 tick 与载档计数未变化，判为帧内无进展（{where}）";

        if (stall.Observe(now, body, extra) is not TimeSpan idle)
            return null;

        return (stall.EverAnswered
                   ? $"进度连续 {idle.TotalMinutes:0} 分钟未前移（上限 {timeoutMinutes} 分钟），判为无进展"
                   : $"{idle.TotalMinutes:0} 分钟里一条进度脉冲也未收到（上限 {timeoutMinutes} 分钟），判为无进展")
               + $"{missing}（本轮已等 {(now - start).TotalMinutes:0.0} 分钟；{where}）";
    }
}
