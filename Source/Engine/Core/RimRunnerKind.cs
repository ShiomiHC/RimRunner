namespace RimRunner;

/// <summary>用例性质轴：结论由谁给出、何时固定。</summary>
public enum RimRunnerKind
{
    /// <summary>回归：断言即结论，每轮重算。</summary>
    Regression,
    /// <summary>读数：只产读数正文、截图、样本与诊断；缺省批不含，指定或选读数档才执行。</summary>
    /// <remarks>结果态为完成或未完成，不进通过率台账。</remarks>
    Readout
}
