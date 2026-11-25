using RimRunner;
using RimWorld;
using Verse;

namespace RimRunnerExamples;

/// <summary>假人受一笔钝伤：名义账记下施加量，实伤账与 TakeDamage 的返回一致。</summary>
/// <remarks>只验单笔、恒穿甲、关内传播的钝伤在受击记录器名义账与实伤账上的读数。</remarks>
[RimRunnerGroup("examples")]
public sealed class Example_DamageLedger : RimRunnerRegression
{
    private const float Amount = 5f;

    public override string Id => "EXAMPLE-DAMAGE";
    public override RimRunnerLevel Level => RimRunnerLevel.World;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        // 生成入口把靶登记给引擎，用例末回收
        Pawn dummy = RimRunnerPawnGen.Target(ctx, RimRunnerDummySpec.Default, RimRunnerSpawnAt.Anchored);

        RimRunnerDamageRecorder.Start(dummy);
        DamageInfo hit = RimRunnerDamage.Make(DamageDefOf.Blunt, Amount, null, RimRunnerDamage.PickInjuryPart(dummy));
        DamageWorker.DamageResult result = dummy.TakeDamage(hit);
        // 受击记录器是进程内的静态表，不停止则后面用例的受击也一并记入
        RimRunnerDamageRecorder.Stop();

        ctx.AssertApprox("EXAMPLE-DAMAGE-Nominal", Amount, RimRunnerDamageRecorder.NominalTakenBy(dummy), 0.001f,
            RimRunnerExpectationSource.Literal("RimRunnerDamage.Make 的 amount"));
        ctx.AssertTrue("EXAMPLE-DAMAGE-Dealt", result.totalDamageDealt > 0f,
            "钝伤 " + Amount + " 落伤 " + result.totalDamageDealt,
            RimRunnerExpectationSource.Literal("恒穿甲的伤害对无护甲的假人落伤为正"));
        ctx.AssertApprox("EXAMPLE-DAMAGE-Settled", result.totalDamageDealt, RimRunnerDamageRecorder.SettledTakenBy(dummy), 0.001f,
            RimRunnerExpectationSource.Literal("TakeDamage 返回的 totalDamageDealt"));
    }
}
