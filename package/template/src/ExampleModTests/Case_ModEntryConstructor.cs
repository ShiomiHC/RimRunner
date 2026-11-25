using ExampleMod;

namespace ExampleModTests
{
    // 游戏按 (ModContentPack) 签名实例化 Mod 子类，没有该构造函数时 mod 装载即失败。
    // Assembly 级：只用反射，不载档、不操作 Def 库
    /// <summary>产品 mod 的入口类带 (ModContentPack) 构造函数。</summary>
    /// <remarks>只验 ExampleModMod 有以 ModContentPack 为唯一参数的公有实例构造函数。</remarks>
    [RimRunnerGroup("smoke")]
    public sealed class Case_ModEntryConstructor : RimRunnerRegression
    {
        public override string Id => "EXM1";
        public override RimRunnerLevel Level => RimRunnerLevel.Assembly;

        public override void Run(RimRunnerRegressionContext ctx)
        {
            ConstructorInfo constructor = typeof(ExampleModMod).GetConstructor(new[] { typeof(ModContentPack) });
            ctx.AssertTrue("EXM1-Constructor", constructor != null,
                typeof(ExampleModMod).FullName + (constructor != null ? " 有" : " 没有") + " (ModContentPack) 构造函数",
                RimRunnerExpectationSource.Literal("Verse.Mod 子类由游戏按 (ModContentPack) 构造"));
        }
    }
}
