using System.Linq;
using RimRunner;
using Verse;

namespace RimRunnerExamples;

/// <summary>每件穿戴物都声明覆盖的部位组。</summary>
/// <remarks>只验 DefDatabase 里 IsApparel 的 ThingDef 的 apparel.bodyPartGroups 非空。</remarks>
[RimRunnerGroup("examples")]
public sealed class Example_ApparelBodyGroups : RimRunnerRegression
{
    public override string Id => "EXAMPLE-APPAREL";
    public override RimRunnerLevel Level => RimRunnerLevel.Defs;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        // 一条断言列全部违例者：逐个断言时，第一条未通过就看不到其余的
        var offenders = DefDatabase<ThingDef>.AllDefs
            .Where(def => def.IsApparel && (def.apparel.bodyPartGroups == null || def.apparel.bodyPartGroups.Count == 0))
            .Select(def => def.defName + "（" + (def.modContentPack?.PackageId ?? "?") + "）");
        ctx.AssertNoneOffending("EXAMPLE-APPAREL-Groups", offenders,
            RimRunnerExpectationSource.Literal("ApparelProperties.bodyPartGroups：穿戴时按它判与已穿的冲突"));
    }
}
