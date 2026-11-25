namespace RimRunner;

/// <summary>把登记 pawn 的环境温度钉成定值，同地图其余对象保持原温度。</summary>
/// <remarks>只改该 pawn 的 AmbientTemperature 读数，所在格与房间的温度保持原值。</remarks>
public static class RimRunnerAmbientControl
{
    private static readonly RimRunnerPawnFixtureMap<Pin> FixedTemperatures = new(
        "RimRunnerAmbientControl.Fix");

    /// <summary>作用域内钉住 pawn 的环境温度。</summary>
    /// <remarks>Dispose 还原为本作用域开之前那一层，之前没钉即解除钉住。Remove 之后 Dispose 保持 Remove 后的状态。用例末（多阶段为 phase 末）仍处于钉住、不随交棒走的由引擎撤下并记 FIXTURE-RESIDUE。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">celsius 非有限或不在 -273.15 到 1000 之间时抛。</exception>
    public static IDisposable Fix(Pawn pawn, float celsius)
    {
        if (pawn == null) throw new ArgumentNullException(nameof(pawn));
        if (float.IsNaN(celsius) || float.IsInfinity(celsius)
                                 || celsius < -273.15f || celsius > 1000f)
        {
            throw new ArgumentOutOfRangeException(nameof(celsius), celsius,
                "测试环境温度须为 RoomTempTracker 可承载的有限值");
        }

        FixedTemperatures.TryGetValue(pawn, out Pin? previous);
        Pin pin = new(celsius, previous);
        FixedTemperatures.Set(pawn, pin);
        return new FixedTemperatureScope(pawn, pin);
    }

    /// <summary>解除该 pawn 的环境温度钉住。</summary>
    public static void Remove(Pawn? pawn)
    {
        if (pawn != null) FixedTemperatures.Remove(pawn);
    }

    /// <summary>读该 pawn 当前钉的温度，未钉返回 false。</summary>
    public static bool TryGet(Pawn? pawn, out float celsius)
    {
        if (pawn != null && FixedTemperatures.TryGetValue(pawn, out Pin? pin))
        {
            celsius = pin.Celsius;
            return true;
        }

        celsius = default;
        return false;
    }

    private sealed class Pin(float celsius, Pin? previous)
    {
        internal float Celsius { get; } = celsius;
        internal Pin? Previous { get; } = previous;
        internal bool Closed;
    }

    private sealed class FixedTemperatureScope(Pawn pawn, Pin pin) : IDisposable
    {
        public void Dispose()
        {
            if (pin.Closed) return;
            pin.Closed = true;
            if (!FixedTemperatures.TryGetValue(pawn, out Pin? current) || current != pin) return;
            Pin? restore = pin.Previous;
            while (restore is { Closed: true })
            {
                restore = restore.Previous;
            }

            if (restore != null)
            {
                FixedTemperatures.Set(pawn, restore);
            }
            else
            {
                FixedTemperatures.Remove(pawn);
            }
        }
    }
}

[HarmonyPatch(typeof(Thing), nameof(Thing.AmbientTemperature), MethodType.Getter)]
internal static class Patch_Thing_AmbientTemperature_RimRunnerFixed
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(Thing __instance, ref float __result)
    {
        if (__instance is Pawn pawn
            && RimRunnerAmbientControl.TryGet(pawn, out float celsius))
        {
            __result = celsius;
        }
    }
}
