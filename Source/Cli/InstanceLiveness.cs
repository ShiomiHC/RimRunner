namespace RimRunnerCli;

public static class InstanceLiveness
{
    public static string Describe(string busyVerdict, string? runState) => busyVerdict switch
    {
        "no-instance" => "无进程",
        "stop-unresponsive" => "无应答",
        "stop-idle" => "空闲",
        "refuse" => $"正在执行（runState={runState}）",
        _ => $"预期外的状态回应：{busyVerdict}（runState={runState}）",
    };

    public static string ShortLabel(string busyVerdict) => busyVerdict switch
    {
        "no-instance" => "无进程",
        "stop-unresponsive" => "无应答",
        "stop-idle" => "空闲",
        "refuse" => "正在执行",
        _ => "预期外的状态回应：" + busyVerdict,
    };

    public static string GoneLabel(string? stopNote) => stopNote != null ? "已停" : "无进程";

    public static string? IdleNote(InstanceProbe.PingSnapshot? ping)
    {
        if (ping is not { RunState: "idle", IdleSeconds: double idle }) return null;
        string text = "空闲 " + ReportPresentation.Elapsed((long)(idle * 1000));
        return ping.IdleLimitMinutes is double minutes
            ? text + $"，满 {IdleExit.Minutes(minutes)}停止"
            : text + "，不因空闲停止";
    }

    internal static (string Short, string Long)? LoadPhase(InstanceProbe.InstanceFacts? facts, bool starting,
        DateTimeOffset now)
    {
        if (facts is not { LaunchedAt: DateTimeOffset launched, LoadedAt: null }) return null;
        int seconds = (int)Math.Max(0, (now - launched).TotalSeconds);
        return starting
            ? ("加载中", $"加载中（进程于 {seconds}s 前启动）")
            : ("未完成加载", $"未完成加载：进程于 {seconds}s 前启动，启动该进程的命令已结束，实例尚未进档");
    }

    public static string ColdStartPhase(int number, ColdStartLock.Holder holder, IEnumerable<AdmissionLedger.QueueEntry> queue,
        DateTimeOffset now)
    {
        string head = $"启动中 {(int)Math.Max(0, (now - holder.StartedAt).TotalSeconds)}s"
                      + (holder.Label != null ? $"（{holder.Label}）" : "");
        return QueuePhase(number, queue, now) is { } queued ? $"{head} · {queued}" : head;
    }

    public static string? QueuePhase(int number, IEnumerable<AdmissionLedger.QueueEntry> queue,
        DateTimeOffset now)
    {
        AdmissionLedger.QueueEntry? entry = queue.FirstOrDefault(e => e.Number == number);
        if (entry == null) return null;
        int seconds = (int)Math.Max(0, (now - entry.EnqueuedAt).TotalSeconds);
        return $"排队 {seconds}s" + (entry.Waiting == null ? "" : $"：{entry.Waiting}");
    }
}
