namespace RimRunner;

/// <summary>tick 分档钉：把相机判档钉成固定档，由 ctx.PinTickRate 返回，用例收尾时自动卸。</summary>
/// <remarks>作用于全部地图上按相机判档的物，子弹钉 15 时取 15、钉 1–5 时取 1。自带固定档的物保持原档。</remarks>
public sealed class RimRunnerTickRatePin : IDisposable
{
    /// <summary>离屏档，也是 vanilla 的分档上界。</summary>
    public const int OffScreen = 15;

    private static RimRunnerTickRatePin? active;

    private int rate;
    private bool released;

    private RimRunnerTickRatePin(int rate)
    {
        this.rate = rate;
    }

    internal static int PinnedRate
    {
        get;
        private set
        {
            if (field != value) Generation++;
            field = value;
        }
    }

    internal static int Generation { get; private set; }

    /// <summary>钉住的档：赋值即换档，只收 1–5 与 15；推 tick 期间或卸下后赋值即抛。</summary>
    /// <remarks>换档须落在测量窗的热身之前。RimRunnerProfilerBridge 的分段读数在上次 Reset 后换过档即抛。</remarks>
    public int Rate
    {
        get => rate;
        set
        {
            if (released)
            {
                throw new InvalidOperationException(RimRunnerLog.Prefix
                    + " tick 分档钉已解除（Dispose 或用例收尾），无法再切换档位");
            }

            RequireRate(value);
            RimRunnerTimeDriver.ThrowIfAdvancing("更换 tick 分档率");
            rate = value;
            PinnedRate = value;
        }
    }

    /// <summary>提前卸下，回到 vanilla 的相机判档，推 tick 期间调用即抛，已卸下的再次调用直接返回。</summary>
    public void Dispose()
    {
        if (released)
        {
            return;
        }

        RimRunnerTimeDriver.ThrowIfAdvancing("卸下 tick 分档钉");
        Release();
    }

    internal static RimRunnerTickRatePin Pin(int rate)
    {
        RequireRate(rate);
        if (active != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " tick 分档已钉在 " + active.rate
                + "。一条用例只持一份");
        }

        RimRunnerTimeDriver.ThrowIfAdvancing("钉住 tick 分档率");
        active = new RimRunnerTickRatePin(rate);
        PinnedRate = rate;
        return active;
    }

    internal static void EndCase()
    {
        active?.Release();
    }

    private void Release()
    {
        released = true;
        if (active == this)
        {
            active = null;
            PinnedRate = 0;
        }
    }

    private static void RequireRate(int rate)
    {
        if (rate is (< 1 or > 5) and not OffScreen)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate,
                RimRunnerLog.Prefix + " tick 分档率只接受 vanilla 能产出的档：1–5（视野内，缩放档 + 1）或 15（离屏）");
        }
    }
}

[HarmonyPatch(typeof(GenTicks), nameof(GenTicks.GetCameraUpdateRate))]
internal static class Patch_GenTicks_GetCameraUpdateRate_RimRunnerTickRatePin
{
    private static bool Prefix(ref int __result)
    {
        int rate = RimRunnerTickRatePin.PinnedRate;
        if (rate == 0) return true;
        __result = rate;
        return false;
    }
}

[HarmonyPatch(typeof(Projectile), nameof(Projectile.UpdateRateTicks), MethodType.Getter)]
internal static class Patch_Projectile_UpdateRateTicks_RimRunnerTickRatePin
{
    private static bool Prefix(ref int __result)
    {
        int rate = RimRunnerTickRatePin.PinnedRate;
        if (rate == 0) return true;
        __result = rate == RimRunnerTickRatePin.OffScreen ? RimRunnerTickRatePin.OffScreen : 1;
        return false;
    }
}
