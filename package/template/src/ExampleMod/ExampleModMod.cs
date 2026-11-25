using Verse;

namespace ExampleMod
{
    // 产品 mod 的入口类。测试工程的产品描述符以它为探针：引擎由探针反查 ModContentPack，识别哪些内容属于本产品
    public sealed class ExampleModMod : Mod
    {
        public ExampleModMod(ModContentPack content) : base(content) { }
    }
}
