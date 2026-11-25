namespace RimRunner;

/// <summary>AddDiagnostic 的读数性质枚举。</summary>
/// <remarks>报告里写成员名首字母小写的串，如 staticScan。</remarks>
public enum RimRunnerDiagnosticKind
{
    /// <summary>覆盖度台账。</summary>
    Coverage,

    /// <summary>数值读数。</summary>
    Measurement,

    /// <summary>前置条件确认：被测行为的前提是否成立。</summary>
    Precondition,

    /// <summary>运行轨迹。</summary>
    Trace,

    /// <summary>状态快照。</summary>
    Snapshot,

    /// <summary>静态结构扫描（不随世界状态变）。</summary>
    StaticScan,

    /// <summary>引擎在 phase 收尾统一落的观测。</summary>
    Observation
}

internal static class RimRunnerDiagnosticKinds
{
    internal static string ToWire(RimRunnerDiagnosticKind kind)
    {
        return kind switch
        {
            RimRunnerDiagnosticKind.Coverage => "coverage",
            RimRunnerDiagnosticKind.Measurement => "measurement",
            RimRunnerDiagnosticKind.Precondition => "precondition",
            RimRunnerDiagnosticKind.Trace => "trace",
            RimRunnerDiagnosticKind.Snapshot => "snapshot",
            RimRunnerDiagnosticKind.StaticScan => "staticScan",
            RimRunnerDiagnosticKind.Observation => "observation",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }
}
