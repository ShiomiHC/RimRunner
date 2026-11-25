namespace RimRunner;

/// <summary>共享场景装配器的基类，在 Configure 里经 Scenario 登记。</summary>
/// <remarks>引擎在每条 World 级用例的 phase 0 按 Stage 递增调 Ensure，时机在环境清扫之后、残留与组件基线开拍之前。</remarks>
public abstract class RimRunnerScenario
{
    /// <summary>装配顺序键，小的先装配。</summary>
    public abstract int Stage { get; }
    /// <summary>装配场景；须可重复调用并回填当前 ctx 的引用。</summary>
    /// <remarks>多阶段用例只在 phase 0 调；这里登记的 ctx.Defer 落清理栈最底，其余收尾执行完才轮到它。</remarks>
    public abstract void Ensure(RimRunnerContext ctx);

    /// <summary>续段归一：多阶段用例每个续段读档后、续段体之前按 Stage 递增调，缺省不做事。</summary>
    /// <remarks>只调 World 级；小图成员在引擎补推读档那一拍之前调，主图成员在那一拍之后；这里不能推 tick，也不能调 DeferScenarioRestore。</remarks>
    public virtual void Resume(RimRunnerContext ctx)
    {
    }
}
