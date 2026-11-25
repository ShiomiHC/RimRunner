namespace RimRunner;

/// <summary>豁免声明：本方法直调 pawn 生成、不经生成根。</summary>
/// <remarks>方法体（含 lambda 与局部函数）直调 PawnGenerator.GeneratePawn 或引擎的 GenerateIsolatedPawn、GenerateRaidMemberPawn 时挂上；无直调即判陈旧。</remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false)]
public sealed class RimRunnerDirectGenerationAttribute(string reason) : Attribute
{
    /// <summary>不经生成根的理由，不得空白。</summary>
    public string Reason { get; } = reason;
}
