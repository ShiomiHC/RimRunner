using ExampleMod;

namespace ExampleModTests
{
    // 产品描述符：测试程序集里恰好一个非抽象子类，引擎反射发现，缺失即抛
    public sealed class ExampleModDescriptor : RimRunnerProductDescriptor
    {
        // 产品每个 mod 的入口类型，一个 mod 一项
        public override Type[] ProbeTypes => new[] { typeof(ExampleModMod) };

        // 本产品 Def 的 defName 前缀，Def 找不到所属 mod 时据此认领
        public override string DefNameFallbackPrefix => "ExampleMod";

        // 能力按需登记；没有任何能力时方法体留空
        public override void Configure(RimRunnerProductCapabilities c)
        {
        }
    }
}
