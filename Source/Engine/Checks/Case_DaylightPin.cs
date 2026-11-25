using System.Runtime.CompilerServices;

namespace RimRunner;

/// <summary>天光恒钉 1.0；AllowNightSky 作用域内按 vanilla 取值，退出即恢复恒钉。</summary>
/// <remarks>只验运行中地图的 SkyManager：CurSkyGlow 与 curSkyGlowInt 字段、露天格的 GroundGlowAt、用例期间新建的 SkyManager，以及 AllowNightSky 作用域内外。</remarks>
[RimRunnerEnvCheck]
public class Case_DaylightPin : RimRunnerRegression
{
    private const string Source = "引擎承诺：用例期间各图天光恒为正午，新建的图建成即为正午，ctx.AllowNightSky 作用域内按钟点取天光；"
                                  + "不成立时受光照影响的行为（睡眠、心情、作物、视野）随钟点改变，同一用例改在另一个时刻执行，结论随之改变";

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "curSkyGlowInt")]
    private static extern ref float SkyGlowOf(SkyManager sky);

    public override string Id => "DAY1";
    public override RimRunnerLevel Level => RimRunnerLevel.World;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        IntVec3 open = ctx.Map!.AllCells.First(c => !c.Roofed(ctx.Map) && c.InBounds(ctx.Map));
        bool pinnedBefore = ctx.Map.skyManager.CurSkyGlow == 1f
                            && ctx.Map.glowGrid.GroundGlowAt(open) >= 0.3f;
        RimRunnerTimeDriver.AdvanceBurst(1);
        bool pinnedAfter = ctx.Map.skyManager.CurSkyGlow == 1f
                           && ctx.Map.glowGrid.GroundGlowAt(open) >= 0.3f;
        ctx.AssertTrue("DAY1-Pinned", pinnedBefore && pinnedAfter,
            "用例期天光未钉住：天光=" + ctx.Map.skyManager.CurSkyGlow.ToString("0.###")
            + "（应 1），露天格 " + open + " 的地面光照=" + ctx.Map.glowGrid.GroundGlowAt(open).ToString("0.###")
            + "（应 ≥ 0.3）；推 1 tick 前成立=" + pinnedBefore + "，推后成立=" + pinnedAfter,
            RimRunnerExpectationSource.Literal(Source));
        ctx.AssertTrue("DAY1-Field", SkyGlowOf(ctx.Map.skyManager) == 1f,
            "用例期天光的存储值=" + SkyGlowOf(ctx.Map.skyManager).ToString("0.###")
            + "（应 1）：直接读存储值的代码读到的是钟点天光",
            RimRunnerExpectationSource.Literal(Source));
        float fresh = SkyGlowOf(new SkyManager(ctx.Map));
        ctx.AssertTrue("DAY1-NewMap", fresh == 1f,
            "用例期新建的图天光=" + fresh.ToString("0.###") + "（应 1）",
            RimRunnerExpectationSource.Literal(Source));

        using (ctx.AllowNightSky())
        {
            ctx.Map.skyManager.ForceSetCurSkyGlow(0f);
            ctx.AssertTrue("DAY1-NightScope", ctx.Map.skyManager.CurSkyGlow == 0f,
                "ctx.AllowNightSky 作用域内把天光设为 0 后读到 " + ctx.Map.skyManager.CurSkyGlow.ToString("0.###")
                + "（应 0）",
                RimRunnerExpectationSource.Literal(Source));
        }

        ctx.AssertTrue("DAY1-Restored", SkyGlowOf(ctx.Map.skyManager) == 1f,
            "ctx.AllowNightSky 退出后天光=" + SkyGlowOf(ctx.Map.skyManager).ToString("0.###")
            + "（应 1）：作用域之后的用例体不再钉正午",
            RimRunnerExpectationSource.Literal(Source));
    }
}
