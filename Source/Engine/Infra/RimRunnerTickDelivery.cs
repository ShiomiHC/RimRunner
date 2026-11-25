using System.Runtime.CompilerServices;

namespace RimRunner;

/// <summary>按 Thing 记录 DoTick 实际交付的 Σdelta 与批次数。</summary>
/// <remarks>逐 tick 交付时，推进 N tick 后 Σdelta 与 N 之差小于 VanillaMaxChunkTicks。没经 DoTick 的 tick 数记进 Gaps。本 phase 末全部撤下，下个 phase 须重新 Arm。</remarks>
public static class RimRunnerTickDelivery
{
    /// <summary>vanilla 单批 tick 上限 15。</summary>
    public const int VanillaMaxChunkTicks = 15;

    /// <summary>一个 Thing 的交付账。</summary>
    public readonly struct Ledger(int delivered, int chunks, int gaps)
    {
        /// <summary>窗内交付的 Σdelta：DoTick 交给 TickInterval 的 tick 数合计。</summary>
        public readonly int Delivered = delivered;
        /// <summary>批次数。</summary>
        public readonly int Chunks = chunks;
        /// <summary>窗内没经 DoTick 的 tick 数：离场、在不推进内容的容器里、在冻结图上。</summary>
        public readonly int Gaps = gaps;

        /// <summary>可读文本。</summary>
        public override string ToString() =>
            "Σdelta=" + Delivered + "（" + Chunks + " 批"
            + (Chunks > 0 ? "，均 " + ((float)Delivered / Chunks).ToString("0.0") + " tick/批" : "") + "）"
            + (Gaps > 0 ? " 没经 DoTick " + Gaps + " tick" : "");
    }

    private const int GapTolerance = 1;

    private sealed class Tally
    {
        internal int Delivered;
        internal int Chunks;
        internal int Visits;
        internal int ArmedAt;
        internal bool Warned;
    }

    private static readonly Dictionary<Thing, Tally> armed = [];

    /// <summary>开始对该 Thing 记账，已在记的清零重记。</summary>
    /// <remarks>只收 tickerType 为 Normal 的 Thing，其余当场抛。批长随相机判档，ctx.PinTickRate 钉住后恒定。</remarks>
    public static void Arm(Thing thing)
    {
        if (thing == null)
        {
            throw new ArgumentNullException(nameof(thing));
        }

        if (thing.def.tickerType != TickerType.Normal)
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " RimRunnerTickDelivery.Arm：" + thing.LabelShort
                + " 的 tickerType 是 " + thing.def.tickerType + "，只有 Normal 的 Thing 经 TickInterval 记账", nameof(thing));
        }

        armed[thing] = new Tally { ArmedAt = Find.TickManager.TicksGame };
        AnyArmed = true;
    }

    /// <summary>是否在记账。</summary>
    public static bool IsArmed(Thing thing) => thing != null && armed.ContainsKey(thing);

    /// <summary>保持记账并读取当前账。</summary>
    /// <remarks>Gaps 超过 1 时输出 TICK-DELIVERY-GAP，每次 Arm 至多一条。</remarks>
    public static Ledger Peek(Thing thing)
    {
        if (thing == null || !armed.TryGetValue(thing, out Tally? tally))
        {
            throw new InvalidOperationException("RimRunnerTickDelivery：" + thing?.LabelShort + " 未 Arm，无账可读");
        }

        int window = Find.TickManager.TicksGame - tally.ArmedAt;
        int gaps = Math.Max(0, window - tally.Visits);
        if (gaps > GapTolerance && !tally.Warned)
        {
            tally.Warned = true;
            RimRunnerSentinel.Warn(RimRunnerWarningKey.TickDeliveryGap,
                "记账窗内有 tick 没经 Thing.DoTick：这些 tick 不交付，Σdelta 的缺额不来自批长",
                thing.LabelShort + "（" + thing.ThingID + "）窗长 " + window + " tick，经 DoTick " + tally.Visits + " 次");
        }

        return new Ledger(tally.Delivered, tally.Chunks, gaps);
    }

    /// <summary>停止记账并返回账本。</summary>
    /// <remarks>读账口径同 Peek。</remarks>
    public static Ledger Disarm(Thing thing)
    {
        Ledger ledger = Peek(thing);
        armed.Remove(thing);
        AnyArmed = armed.Count > 0;
        return ledger;
    }

    internal static void Reset()
    {
        armed.Clear();
        AnyArmed = false;
    }

    internal static bool AnyArmed { get; private set; }

    internal static void Visit(Thing thing)
    {
        if (armed.TryGetValue(thing, out Tally? tally)) tally.Visits++;
    }

    internal static void Record(Thing thing, int delta)
    {
        if (!armed.TryGetValue(thing, out Tally? tally)) return;
        tally.Delivered += delta;
        tally.Chunks++;
    }
}

[HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
internal static class Patch_Thing_DoTick_RimRunnerTickDelivery
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "tickDelta")]
    private static extern ref int TickDelta(Thing thing);

    private static void Prefix(Thing __instance, out int __state)
    {
        __state = -1;
        if (!RimRunnerTickDelivery.AnyArmed || __instance.Destroyed
            || __instance.def.tickerType != TickerType.Normal
            || !RimRunnerTickDelivery.IsArmed(__instance)
            || RimRunnerIsolatedMap.IsFrozen(__instance))
        {
            return;
        }

        RimRunnerTickDelivery.Visit(__instance);
        __state = TickDelta(__instance);
    }

    private static void Postfix(Thing __instance, int __state)
    {
        if (__state < 0 || __instance.Destroyed || TickDelta(__instance) != 0) return;
        RimRunnerTickDelivery.Record(__instance, __state + 1);
    }
}
