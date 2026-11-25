namespace RimRunner;

/// <summary>一个方法在一段采样窗内的聚合读数。</summary>
public sealed class RimRunnerProfilerRow
{
    /// <summary>DPA 的 profiler 键，形如 命名空间.类型:方法。</summary>
    public string? Key;

    /// <summary>段内总耗时 ms。</summary>
    /// <remarks>整段合计，不是每 tick；换算与归一由调用方做。</remarks>
    public double TotalMs;

    /// <summary>段内单 tick 峰值 ms。</summary>
    public double PeakTickMs;

    /// <summary>段内调用次数。</summary>
    /// <remarks>除以段长才是每 tick 调用频次。</remarks>
    public long Calls;

    /// <summary>段内有非零耗时的 tick 数。</summary>
    /// <remarks>远小于段长即说明这一行为间歇调用；此时 TotalMs / 段长 不描述真实的单次调用。</remarks>
    public int ActiveTicks;
}
