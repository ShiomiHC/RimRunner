namespace RimRunner;

internal enum RimRunnerPhase
{
    None,
    Case,
    Scene,
    Enumeration
}

public partial class RimRunnerContext
{
    internal RimRunnerPhase Phase { get; private set; } = RimRunnerPhase.Case;

    internal static RimRunnerPhase AmbientPhase => Active?.Phase ?? RimRunnerPhase.None;

    private protected void RequireCasePhase(string entryPoint)
    {
        string? refusal = Phase switch
        {
            RimRunnerPhase.Case => null,
            RimRunnerPhase.Scene => "场面态不支持 " + entryPoint + "：该入口依赖用例窗、报告或消费台账",
            RimRunnerPhase.Enumeration or RimRunnerPhase.None => throw new InvalidOperationException(
                RimRunnerLog.Prefix + " " + entryPoint + " 只经断言路径进来，阶段不该是 " + Phase)
        };
        if (refusal != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + refusal);
        }
    }

    private protected void RejectInEnumeration(string entryPoint)
    {
        string? refusal = Phase switch
        {
            RimRunnerPhase.Case or RimRunnerPhase.Scene => null,
            RimRunnerPhase.Enumeration => EnumerationRefusal(entryPoint),
            RimRunnerPhase.None => throw PhaseNone()
        };
        if (refusal != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + refusal);
        }
    }

    private void RequireBody(string entryPoint)
    {
        RejectInEnumeration(entryPoint);
        if (Active != this)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + entryPoint + " 只在用例体或场面步骤内可调");
        }
    }

    private static string EnumerationRefusal(string entryPoint) =>
        "分片枚举（ShardCells）里不能调 " + entryPoint + "：枚举只读、不记报告";

    private static InvalidOperationException PhaseNone() =>
        new(RimRunnerLog.Prefix + " ctx 的阶段不该是 None");

    internal static RimRunnerContext CreateEnumeration(RimRunnerLevel level)
    {
        return new RimRunnerContext
        {
            Map = level == RimRunnerLevel.World ? RimRunnerCurrentMapProbe.EngineCurrentMap() : null,
            Phase = RimRunnerPhase.Enumeration
        };
    }

    internal IDisposable EnterEnumeration()
    {
        if (Phase != RimRunnerPhase.Enumeration)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " EnterEnumeration 只给枚举 ctx 用");
        }

        return new ActiveSwap(this);
    }

    internal List<string> EnumerationFaults { get; } = [];

    internal List<KeyValuePair<string, string>> EnumerationWarnings { get; } = [];
}
