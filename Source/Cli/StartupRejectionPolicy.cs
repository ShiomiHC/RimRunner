namespace RimRunnerCli;

internal static class StartupRejectionPolicy
{
    internal static InstanceRuntime Record(InstanceRuntime runtime, EngineReport? report)
    {
        if (report == null)
            return runtime;

        List<EngineReportRejection> startup =
            [.. report.Rejections.Where(r => r.Gate == RimRunner.RimRunnerRejectionGate.Startup)];
        return runtime with
        {
            StartupRejection = startup.Count > 0 ? ReportValidator.DescribeRejections(startup) : null,
        };
    }

    public static string? ReuseRefusal(string? startupRejection) =>
        startupRejection == null
            ? null
            : $"实例上一轮批次被拒：{startupRejection}；启动错误级日志在进程启动时确定，同一份名单下本进程上的每一轮都同样被拒";
}
