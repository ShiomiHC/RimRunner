namespace RimRunner;

/// <summary>测试程序集的诊断流水入口：写进引擎日志、并进本命令的 RimRunner 日志，下一轮冷启动仍保留。</summary>
/// <remarks>任意线程、无 ctx 可调；行首自动带测试程序集名与当前用例 Id；手动开的游戏或实例管道未连接时退回 Player.log。只写诊断流水，不进错误级与警告级日志监控；要人看的警告走断言或 Log.Warning。</remarks>
public static class RimRunnerTrace
{
    /// <summary>写一条诊断流水。</summary>
    /// <remarks>每条当场写出，不累积到用例结束。</remarks>
    /// <param name="text">正文，可多行；续行在 RimRunner 日志里缩进两格归属本条。</param>
    public static void Write(string text) =>
        RimRunnerEngineLog.InfoTagged(Tag(RimRunnerObservationBuffer.ActiveCaseId) + text);

    internal static string Tag(string? caseId) =>
        RimRunnerLog.Prefix + (caseId == null ? "" : "[" + caseId + "]") + " ";
}
