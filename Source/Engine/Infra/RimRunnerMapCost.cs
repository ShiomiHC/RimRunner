using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;

namespace RimRunner;

/// <summary>按地图分桶的 tick 开销计量器，用例收尾时自动卸下。</summary>
public sealed class RimRunnerMapCostMeter : IDisposable
{
    private readonly Map[] maps;
    private readonly Harmony harmony;
    private bool released;
    private int rotationPhase;
    private int windows;
    private bool cameraSplitWarned;

    private readonly int openedAtTick;

    private RimRunnerMapCostMeter(Map[] maps, bool rotateOrder, bool sharedTemperature, Harmony harmony)
    {
        this.maps = maps;
        RotateOrder = rotateOrder;
        SharedTemperature = sharedTemperature;
        this.harmony = harmony;
        openedAtTick = Find.TickManager.TicksGame;
    }

    /// <summary>被测的图，按开表时给的顺序。</summary>
    public IReadOnlyList<Map> Maps => maps;

    /// <summary>是否每 tick 轮换各图的先后。</summary>
    public bool RotateOrder { get; }

    /// <summary>开表期间各被测图的室外与季节温度是否都取首张图的值。</summary>
    public bool SharedTemperature { get; }

    internal static RimRunnerMapCostMeter? Active { get; private set; }

    internal static string HarmonyOwner => RimRunnerHarmonyBootstrap.HarmonyId + ".mapcost";

    /// <summary>推 ticks 个 tick（1–5000）并记账，返回这一窗的读数。</summary>
    public RimRunnerMapCostWindow Measure(int ticks)
    {
        if (released)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 开销计量器已卸载（Dispose 或用例收尾）");
        }

        if (ticks is < 1 or > RimRunnerMapCost.MaxWindowTicks)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks,
                RimRunnerLog.Prefix + " 计量窗只接受 1–" + RimRunnerMapCost.MaxWindowTicks + " tick");
        }

        RimRunnerTimeDriver.ThrowIfAdvancing("开计量窗");
        string? fault = RimRunnerMapCost.MapFault(maps);
        if (fault != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + fault);
        }

        bool includesPatchJit = windows == 0 && Find.TickManager.TicksGame == openedAtTick;
        WarnIfCameraRateSplit("开窗前");
        RimRunnerMapCost.Begin(maps, RotateOrder, ticks, rotationPhase);
        try
        {
            RimRunnerTimeDriver.AdvanceBurst(ticks);
        }
        finally
        {
            RimRunnerMapCost.Stop();
        }

        WarnIfCameraRateSplit("收窗后");
        RimRunnerMapCostWindow window = RimRunnerMapCost.Collect(maps, RotateOrder, SharedTemperature, includesPatchJit);
        windows++;
        rotationPhase = (rotationPhase + window.Ticks) % maps.Length;
        return window;
    }

    private void WarnIfCameraRateSplit(string when)
    {
        if (cameraSplitWarned || RimRunnerTickRatePin.PinnedRate != 0) return;
        Map? current = RimRunnerCurrentMapProbe.EngineCurrentMap();
        if (current == null || Array.IndexOf(maps, current) < 0) return;
        string? split = RimRunnerMapCost.CameraRateSplit(maps);
        if (split == null) return;
        cameraSplitWarned = true;
        RimRunnerSentinel.Warn(RimRunnerWarningKey.TickRateUnpinned,
            "按图开销计量未钉 tick 档，被测图上同类物的交付档位随相机分岔", split + "（" + when + "）");
    }

    /// <summary>提前卸下补丁；用例收尾时引擎也会卸。</summary>
    public void Dispose()
    {
        if (released)
        {
            return;
        }

        RimRunnerTimeDriver.ThrowIfAdvancing("卸开销计量器");
        Release();
    }

    internal static RimRunnerMapCostMeter Open(Map[] maps, bool rotateOrder, bool sharedTemperature)
    {
        if (Active != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " 开销计量器已开（" + string.Join("、", Active.maps.Select(map => map.ToString())) + "），一条用例只持一份");
        }

        RimRunnerTimeDriver.ThrowIfAdvancing("开开销计量器");
        if (maps == null || maps.Length == 0)
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " 开销计量器至少要 1 张图", nameof(maps));
        }

        Map[] copy = maps.ToArray();
        string? fault = RimRunnerMapCost.MapFault(copy);
        if (fault != null)
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " " + fault, nameof(maps));
        }

        Harmony harmony = new(HarmonyOwner);
        try
        {
            RimRunnerMapCost.Install(harmony, rotateOrder);
        }
        catch
        {
            harmony.UnpatchAll(HarmonyOwner);
            throw;
        }

        RimRunnerMapCost.ShareTemperature(sharedTemperature ? copy : null);
        Active = new RimRunnerMapCostMeter(copy, rotateOrder, sharedTemperature, harmony);
        return Active;
    }

    internal static void EndCase()
    {
        Active?.Release();
    }

    private void Release()
    {
        released = true;
        if (Active != this)
        {
            return;
        }

        Active = null;
        RimRunnerMapCost.Stop();
        RimRunnerMapCost.ShareTemperature(null);
        harmony.UnpatchAll(HarmonyOwner);
    }
}

/// <summary>一个计量窗的读数：各被测图逐 tick 的开销与不可归属余数。</summary>
public sealed class RimRunnerMapCostWindow
{
    private readonly Map[] maps;
    private readonly double[][] pre;
    private readonly double[][] things;
    private readonly double[][] post;
    private readonly long[] calls;
    private readonly float[] meanTemp;
    private readonly double[] total;

    internal RimRunnerMapCostWindow(Map[] maps, bool rotated, bool sharedTemperature, bool includesPatchJit, int ticks, double[][] pre, double[][] things,
        double[][] post, long[] calls, float[] meanTemp, double[] total)
    {
        this.maps = maps;
        Rotated = rotated;
        SharedTemperature = sharedTemperature;
        IncludesPatchJit = includesPatchJit;
        Ticks = ticks;
        this.pre = pre;
        this.things = things;
        this.post = post;
        this.calls = calls;
        this.meanTemp = meanTemp;
        this.total = total;
    }

    /// <summary>实际推进的 tick 数。</summary>
    /// <remarks>持续错误级日志熔断提前停下推进时，小于 Measure 要的数。</remarks>
    public int Ticks { get; }

    /// <summary>这一窗是否被持续错误级日志熔断提前停下（Ticks 小于 Measure 要的数）。</summary>
    public bool Truncated => Ticks < RequestedTicks;

    /// <summary>被测的图，按开表时给的顺序。</summary>
    public IReadOnlyList<Map> Maps => maps;

    /// <summary>这一窗是否轮换各图的先后。</summary>
    public bool Rotated { get; }

    /// <summary>这一窗各图是否同温（取首张图的温度）。</summary>
    public bool SharedTemperature { get; }

    /// <summary>这一窗是否含记账补丁首次执行的 JIT：开表后第一窗、且开表到这一窗之间没推过 tick 时为真。</summary>
    public bool IncludesPatchJit { get; }

    internal int PinnedRate { get; init; }

    internal int PinGeneration { get; init; }

    internal int SegmentSerial { get; init; }

    internal int RequestedTicks { get; init; }

    /// <summary>窗内该图 MapPreTick 的毫秒合计。</summary>
    public double PreMs(Map map) => pre[IndexOf(map)].Sum();

    /// <summary>窗内该图物体 DoTick（最外层）的毫秒合计。</summary>
    public double ThingsMs(Map map) => things[IndexOf(map)].Sum();

    /// <summary>窗内该图 MapPostTick 的毫秒合计。</summary>
    public double PostMs(Map map) => post[IndexOf(map)].Sum();

    /// <summary>窗内该图 Pre + Things + Post 的毫秒合计。</summary>
    public double TotalMs(Map map) => PerTickMs(map).Sum();

    /// <summary>窗内该图被计时的 DoTick 次数（最外层）。</summary>
    /// <remarks>计时含补丁自身开销；两批调用数相等时差值里对消，比值会被拉向 1。</remarks>
    public long ThingCalls(Map map) => calls[IndexOf(map)];

    /// <summary>窗内该图室外温度的逐 tick 平均。</summary>
    public float MeanOutdoorTemp(Map map) => meanTemp[IndexOf(map)];

    /// <summary>该图逐 tick 的 Pre + Things + Post 毫秒数组（副本）。</summary>
    public double[] PerTickMs(Map map)
    {
        int index = IndexOf(map);
        double[] result = new double[Ticks];
        for (int tick = 0; tick < Ticks; tick++)
        {
            result[tick] = pre[index][tick] + things[index][tick] + post[index][tick];
        }

        return result;
    }

    /// <summary>该图按 blockTicks 一块求和的毫秒数组。</summary>
    /// <remarks>块长取 15 的倍数时，整窗在场且分档为 15 的物（含 RimRunnerTickRatePin 钉在 OffScreen 时）每块调 TickInterval 的次数相同。</remarks>
    /// <param name="map">被测的图。</param>
    /// <param name="blockTicks">块长，须整除 Ticks。</param>
    public double[] BlockMs(Map map, int blockTicks)
    {
        if (blockTicks < 1 || Ticks % blockTicks != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockTicks), blockTicks,
                RimRunnerLog.Prefix + (Truncated
                    ? " 这一窗因持续错误级日志熔断提前停止，实推 " + Ticks + " / " + RequestedTicks + " tick，块长 " + blockTicks + " 除不尽实推数"
                    : " 块长须整除窗长 " + Ticks + "：除不尽时末块短于其余块，与其余块不可比"));
        }

        double[] perTick = PerTickMs(map);
        double[] blocks = new double[Ticks / blockTicks];
        for (int tick = 0; tick < Ticks; tick++)
        {
            blocks[tick / blockTicks] += perTick[tick];
        }

        return blocks;
    }

    /// <summary>该图减去基线图后的每 tick 毫秒。</summary>
    /// <remarks>(TotalMs(map) − TotalMs(baseline)) ÷ Ticks。</remarks>
    /// <param name="map">被测的图。</param>
    /// <param name="baseline">同窗的基线图，通常是空图。</param>
    /// <exception cref="InvalidOperationException">这一窗没推进 tick 时抛。</exception>
    public double NetMsPerTick(Map map, Map baseline)
    {
        RequireAdvanced();
        return (TotalMs(map) - TotalMs(baseline)) / Ticks;
    }

    /// <summary>该图逐块减去基线图同块后的毫秒数组。</summary>
    /// <param name="map">被测的图。</param>
    /// <param name="baseline">同窗的基线图，通常是空图。</param>
    /// <param name="blockTicks">块长，须整除 Ticks。</param>
    public double[] NetBlockMs(Map map, Map baseline, int blockTicks) =>
        RimRunnerMapCostPairs.Differences(BlockMs(map, blockTicks), BlockMs(baseline, blockTicks));

    /// <summary>窗内 DoSingleTick 整拍的毫秒合计。</summary>
    public double TickTotalMs => total.Sum();

    /// <summary>逐 tick 的不可归属毫秒（整拍 − 各被测图合计）数组（副本）。</summary>
    /// <remarks>几张被测图共用这一份。</remarks>
    public double[] UnattributedPerTickMs()
    {
        double[] result = new double[Ticks];
        for (int tick = 0; tick < Ticks; tick++)
        {
            double attributed = 0;
            for (int index = 0; index < maps.Length; index++)
            {
                attributed += pre[index][tick] + things[index][tick] + post[index][tick];
            }

            result[tick] = total[tick] - attributed;
        }

        return result;
    }

    /// <summary>窗内不可归属毫秒合计（世界、GameComponent、tick 表自身等）。</summary>
    public double UnattributedMs => UnattributedPerTickMs().Sum();

    /// <summary>可读的自陈：各图 tile、平均室外温度、同温与否、每 tick 毫秒、计时次数、轮换与否。</summary>
    public string Describe()
    {
        StringBuilder text = new();
        if (IncludesPatchJit)
        {
            text.Append("开表后首窗，开表后没推过 tick：记账补丁首次执行的 JIT 计在其中\n");
        }

        text.Append("开销计量窗 ").Append(Ticks).Append(" tick，").Append(Rotated ? "每 tick 轮换各图先后" : "按 vanilla 固定先后")
            .Append(SharedTemperature ? "，各图同温（取 " + maps[0] + "）" : "，各图按自己的 tile 取温度")
            .Append("；整拍 ").Append((TickTotalMs / Math.Max(1, Ticks)).ToString("0.0000"))
            .Append(" ms/t，不可归属 ").Append((UnattributedMs / Math.Max(1, Ticks)).ToString("0.0000")).Append(" ms/t\n");
        for (int index = 0; index < maps.Length; index++)
        {
            Map map = maps[index];
            double perTick = Math.Max(1, Ticks);
            text.Append("  ").Append(map).Append(" tile ").Append(map.Tile).Append(' ')
                .Append(meanTemp[index].ToString("0.0")).Append("°C：")
                .Append((TotalMs(map) / perTick).ToString("0.0000")).Append(" ms/t（Pre ")
                .Append((PreMs(map) / perTick).ToString("0.0000")).Append(" / Things ")
                .Append((ThingsMs(map) / perTick).ToString("0.0000")).Append(" / Post ")
                .Append((PostMs(map) / perTick).ToString("0.0000")).Append("），DoTick 计时 ")
                .Append(calls[index]).Append(" 次\n");
        }

        return text.ToString();
    }

    private void RequireAdvanced()
    {
        if (Ticks == 0)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 这一窗没推进 tick（持续错误级日志熔断在第一拍前停下），没有每 tick 读数");
        }
    }

    private int IndexOf(Map map)
    {
        int index = Array.IndexOf(maps, map);
        if (index < 0)
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " " + map + " 不在这一窗的被测图里", nameof(map));
        }

        return index;
    }
}

/// <summary>一轮读数：同一段内若干测量窗按图合计。</summary>
/// <remarks>各窗须同一组被测图、同一段量、窗间没换过 tick 分档钉，否则 Of 当场抛。</remarks>
public sealed class RimRunnerMapCostRound
{
    private readonly RimRunnerMapCostWindow[] segments;

    private RimRunnerMapCostRound(RimRunnerMapCostWindow[] segments)
    {
        this.segments = segments;
    }

    /// <summary>由测量窗拼一轮。</summary>
    /// <param name="windows">同一段内 Measure 返回的测量窗，按量的先后，至少一个。</param>
    /// <exception cref="ArgumentException">windows 为空、含 null，或各窗的被测图不是同一组、同一顺序时抛。</exception>
    /// <exception cref="InvalidOperationException">各窗非同一段内测量，或窗间换过 tick 分档钉时抛。</exception>
    public static RimRunnerMapCostRound Of(params RimRunnerMapCostWindow[] windows)
    {
        if (windows == null || windows.Length == 0 || windows.Any(window => window == null))
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " 一轮须给至少一个测量窗，且不含 null", nameof(windows));
        }

        RimRunnerMapCostWindow first = windows[0];
        if (windows.Any(window => !window.Maps.SequenceEqual(first.Maps)))
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " 一轮里的测量窗须出自同一组被测图、同一顺序", nameof(windows));
        }

        if (windows.Any(window => window.SegmentSerial != first.SegmentSerial))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 一轮里的测量窗须在同一段（同一 phase）内量");
        }

        if (windows.Any(window => window.PinGeneration != first.PinGeneration))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 一轮里的测量窗之间换过 tick 分档钉：各窗的分档不同，合计不可比");
        }

        return new RimRunnerMapCostRound([.. windows]);
    }

    /// <summary>组成这一轮的测量窗，按量的先后。</summary>
    public IReadOnlyList<RimRunnerMapCostWindow> Segments => segments;

    /// <summary>被测的图，按开表时给的顺序。</summary>
    public IReadOnlyList<Map> Maps => segments[0].Maps;

    /// <summary>各窗实际推进的 tick 数之和。</summary>
    public int Ticks => segments.Sum(segment => segment.Ticks);

    /// <summary>是否有窗被持续错误级日志熔断提前停下。</summary>
    public bool Truncated => segments.Any(segment => segment.Truncated);

    /// <summary>该图各窗 Pre + Things + Post 的毫秒合计。</summary>
    public double TotalMs(Map map) => segments.Sum(segment => segment.TotalMs(map));

    /// <summary>该图各窗 MapPreTick 的毫秒合计。</summary>
    public double PreMs(Map map) => segments.Sum(segment => segment.PreMs(map));

    /// <summary>该图各窗物体 DoTick（最外层）的毫秒合计。</summary>
    public double ThingsMs(Map map) => segments.Sum(segment => segment.ThingsMs(map));

    /// <summary>该图各窗 MapPostTick 的毫秒合计。</summary>
    public double PostMs(Map map) => segments.Sum(segment => segment.PostMs(map));

    /// <summary>该图各窗被计时的 DoTick 次数之和（最外层）。</summary>
    public long ThingCalls(Map map) => segments.Sum(segment => segment.ThingCalls(map));

    /// <summary>该图逐窗的毫秒合计数组，一窗一项。</summary>
    public double[] SegmentMs(Map map) => segments.Select(segment => segment.TotalMs(map)).ToArray();

    /// <summary>各窗不可归属毫秒之和。</summary>
    /// <remarks>几张被测图共用这一份。</remarks>
    public double UnattributedMs => segments.Sum(segment => segment.UnattributedMs);

    /// <summary>该图减去基线图后的每 tick 毫秒。</summary>
    /// <remarks>(TotalMs(map) − TotalMs(baseline)) ÷ Ticks。</remarks>
    /// <param name="map">被测的图。</param>
    /// <param name="baseline">同轮的基线图，通常是空图。</param>
    /// <exception cref="InvalidOperationException">这一轮没推进 tick 时抛。</exception>
    public double NetMsPerTick(Map map, Map baseline)
    {
        int ticks = Ticks;
        if (ticks == 0)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 这一轮没推进 tick（持续错误级日志熔断在第一拍前停下），没有每 tick 读数");
        }

        return (TotalMs(map) - TotalMs(baseline)) / ticks;
    }
}

/// <summary>两条等长逐块序列的同块配对运算：相比或相减。</summary>
public static class RimRunnerMapCostPairs
{
    /// <summary>逐块 numerators[i] ÷ denominators[i]，分母不为正的块跳过。</summary>
    /// <remarks>返回长度小于入参长度即有块被跳过。</remarks>
    /// <param name="numerators">分子序列。</param>
    /// <param name="denominators">分母序列，与分子等长。</param>
    /// <exception cref="ArgumentException">两条序列为 null 或不等长时抛。</exception>
    public static double[] Ratios(double[] numerators, double[] denominators)
    {
        RequirePaired(numerators, denominators, nameof(Ratios));
        List<double> ratios = new(numerators.Length);
        for (int block = 0; block < numerators.Length; block++)
        {
            if (denominators[block] > 0.0)
            {
                ratios.Add(numerators[block] / denominators[block]);
            }
        }

        return [.. ratios];
    }

    /// <summary>逐块 minuends[i] − subtrahends[i]。</summary>
    /// <param name="minuends">被减序列。</param>
    /// <param name="subtrahends">减数序列，与被减序列等长。</param>
    /// <exception cref="ArgumentException">两条序列为 null 或不等长时抛。</exception>
    public static double[] Differences(double[] minuends, double[] subtrahends)
    {
        RequirePaired(minuends, subtrahends, nameof(Differences));
        double[] differences = new double[minuends.Length];
        for (int block = 0; block < minuends.Length; block++)
        {
            differences[block] = minuends[block] - subtrahends[block];
        }

        return differences;
    }

    private static void RequirePaired(double[] left, double[] right, string operation)
    {
        if (left == null || right == null || left.Length != right.Length)
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " " + operation + " 的两条序列须等长且非 null（"
                + (left?.Length.ToString() ?? "null") + " / " + (right?.Length.ToString() ?? "null") + "）");
        }
    }
}

internal static class RimRunnerMapCost
{
    internal const int MaxWindowTicks = 5000;

    internal struct Stamp
    {
        internal long At;
        internal int Map;
        internal bool Counted;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "thingLists")]
    private static extern ref List<List<Thing>> ThingLists(TickList list);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TickInterval")]
    private static extern int TickIntervalOf(TickList list);

    private static bool recording;
    private static bool rotate;
    private static Map[] measured = [];
    private static int count;
    private static int capacity;
    private static int tickIndex;
    private static int phase;
    private static int depth;
    private static int[] byMapsOrder = [];
    private static int[] slotById = [];
    private static int preSeen;
    private static int postSeen;

    private static long[][] pre = null!;
    private static long[][] things = null!;
    private static long[][] post = null!;
    private static long[] calls = null!;
    private static double[] tempSum = null!;
    private static long[] total = null!;

    private static readonly List<Thing> others = [];
    private static List<Thing>[] groups = [];

    internal const int TraceTicks = 32;
    internal static readonly List<List<int>> PreTrace = [];
    internal static readonly List<List<int>> TickListTrace = [];

    private static Map[]? climate;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "map")]
    private static extern ref Map TemperatureMap(MapTemperature temperature);

    internal static void ShareTemperature(Map[]? maps)
    {
        climate = maps != null && maps.Length > 1 ? maps : null;
    }

    internal static void OutdoorTempPostfix(MapTemperature __instance, ref float __result)
    {
        Map? source = ClimateSource(__instance);
        if (source != null)
        {
            __result = source.mapTemperature.OutdoorTemp;
        }
    }

    internal static void SeasonalTempPostfix(MapTemperature __instance, ref float __result)
    {
        Map? source = ClimateSource(__instance);
        if (source != null)
        {
            __result = source.mapTemperature.SeasonalTemp;
        }
    }

    private static Map? ClimateSource(MapTemperature temperature)
    {
        Map[]? maps = climate;
        if (maps == null)
        {
            return null;
        }

        Map map = TemperatureMap(temperature);
        for (int index = 1; index < maps.Length; index++)
        {
            if (maps[index] == map)
            {
                return maps[0];
            }
        }

        return null;
    }

    internal static string? CameraRateSplit(Map[] maps)
    {
        Dictionary<ThingDef, (Thing? InView, Thing? OffScreen)> byDef = [];
        foreach (Map map in maps)
        {
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing.def.tickerType == TickerType.Never) continue;
                int rate = thing.UpdateRateTicks;
                byDef.TryGetValue(thing.def, out (Thing? InView, Thing? OffScreen) entry);
                if (rate < RimRunnerTickRatePin.OffScreen) entry.InView ??= thing;
                else entry.OffScreen ??= thing;
                byDef[thing.def] = entry;
                if (entry is { InView: { } inView, OffScreen: { } offScreen })
                {
                    return thing.def.defName + "：" + inView.ThingID + "（" + inView.MapHeld + "）档 " + inView.UpdateRateTicks
                           + "，" + offScreen.ThingID + "（" + offScreen.MapHeld + "）档 " + offScreen.UpdateRateTicks;
                }
            }
        }

        return null;
    }

    internal static string? MapFault(Map[] maps)
    {
        if (maps.Any(map => map == null))
        {
            return "开销计量器的图里有 null";
        }

        if (maps.Distinct().Count() != maps.Length)
        {
            return "开销计量器的图有重复：" + string.Join("、", maps.Select(map => map.ToString()));
        }

        foreach (Map map in maps)
        {
            if (map.Disposed || !Find.Maps.Contains(map))
            {
                return map + " 已不在场，无法计量";
            }

            if (RimRunnerIsolatedMap.IsFrozenMap(map))
            {
                return map + " 是冻结的图（主图或停放中的多阶段小图）：计量结果恒为 0";
            }
        }

        return null;
    }

    internal static void Install(Harmony harmony, bool rotateOrder)
    {
        HarmonyMethod first(string name) =>
            new(AccessTools.Method(typeof(RimRunnerMapCost), name), Priority.First);
        HarmonyMethod last(string name) =>
            new(AccessTools.Method(typeof(RimRunnerMapCost), name), Priority.Last);

        harmony.Patch(Require(AccessTools.Method(typeof(Thing), nameof(Thing.DoTick)), "Thing.DoTick"),
            prefix: first(nameof(ThingPrefix)), finalizer: last(nameof(ThingFinalizer)));
        harmony.Patch(Require(AccessTools.Method(typeof(Map), nameof(Map.MapPreTick)), "Map.MapPreTick"),
            prefix: first(nameof(PrePrefix)), finalizer: last(nameof(PreFinalizer)));
        harmony.Patch(Require(AccessTools.Method(typeof(Map), nameof(Map.MapPostTick)), "Map.MapPostTick"),
            prefix: first(nameof(PostPrefix)), finalizer: last(nameof(PostFinalizer)));
        MethodInfo singleTick = Require(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)),
            "TickManager.DoSingleTick");
        harmony.Patch(singleTick, prefix: first(nameof(SingleTickPrefix)), finalizer: last(nameof(SingleTickFinalizer)),
            transpiler: rotateOrder ? new HarmonyMethod(AccessTools.Method(typeof(RimRunnerMapCost), nameof(DispatchTranspiler))) : null);
        if (rotateOrder)
        {
            harmony.Patch(Require(AccessTools.Method(typeof(TickList), nameof(TickList.Tick)), "TickList.Tick"),
                prefix: first(nameof(TickListPrefix)));
        }

        RimRunnerPatchGate.ThrowIfAny(harmony.GetPatchedMethods());
    }

    private static MethodInfo Require(MethodInfo method, string name) =>
        method ?? throw new InvalidOperationException(RimRunnerLog.Prefix + " 开销计量器无法解析 " + name);

    internal static void Begin(Map[] maps, bool rotateOrder, int ticks, int rotationPhase)
    {
        measured = maps;
        count = maps.Length;
        IndexMaps(maps);
        rotate = rotateOrder && count > 1;
        capacity = ticks;
        tickIndex = -1;
        phase = rotationPhase;
        depth = 0;
        pre = NewTable(count, ticks);
        things = NewTable(count, ticks);
        post = NewTable(count, ticks);
        calls = new long[count];
        tempSum = new double[count];
        total = new long[ticks];
        PreTrace.Clear();
        TickListTrace.Clear();
        recording = true;
    }

    internal static void Stop()
    {
        recording = false;
        depth = 0;
    }

    internal static RimRunnerMapCostWindow Collect(Map[] maps, bool rotated, bool sharedTemperature, bool includesPatchJit)
    {
        int ticks = Math.Min(tickIndex + 1, capacity);
        double toMs = 1000.0 / Stopwatch.Frequency;
        double[][] toTable(long[][] raw) => raw.Select(row => row.Take(ticks).Select(value => value * toMs).ToArray()).ToArray();
        float[] meanTemp = tempSum.Select(sum => ticks > 0 ? (float)(sum / ticks) : float.NaN).ToArray();
        return new RimRunnerMapCostWindow(maps, rotated && maps.Length > 1, sharedTemperature && maps.Length > 1, includesPatchJit, ticks, toTable(pre), toTable(things),
            toTable(post), calls.ToArray(), meanTemp, total.Take(ticks).Select(value => value * toMs).ToArray())
        {
            PinnedRate = RimRunnerTickRatePin.PinnedRate,
            PinGeneration = RimRunnerTickRatePin.Generation,
            SegmentSerial = RimRunnerContext.SegmentSerial,
            RequestedTicks = capacity
        };
    }

    private static long[][] NewTable(int rows, int ticks) =>
        Enumerable.Range(0, rows).Select(_ => new long[ticks]).ToArray();

    private static void IndexMaps(Map[] maps)
    {
        if (byMapsOrder.Length < maps.Length)
        {
            byMapsOrder = new int[maps.Length];
        }

        if (groups.Length < maps.Length)
        {
            int kept = groups.Length;
            Array.Resize(ref groups, maps.Length);
            for (int index = kept; index < groups.Length; index++)
            {
                groups[index] = [];
            }
        }

        int maxId = maps.Max(map => map.uniqueID);
        slotById = new int[maxId + 1];
        for (int index = 0; index < maps.Length; index++)
        {
            slotById[maps[index].uniqueID] = index + 1;
        }
    }

    private static int IndexOf(Map map)
    {
        if (map == null)
        {
            return -1;
        }

        int id = map.uniqueID;
        return (uint)id < (uint)slotById.Length ? slotById[id] - 1 : -1;
    }

    private static bool InWindow => recording && tickIndex >= 0 && tickIndex < capacity;

    private static void SingleTickPrefix(out long __state)
    {
        __state = -1;
        if (!recording)
        {
            return;
        }

        tickIndex++;
        preSeen = 0;
        postSeen = 0;
        List<Map> all = Find.Maps;
        int filled = 0;
        for (int position = 0; position < all.Count && filled < count; position++)
        {
            int index = IndexOf(all[position]);
            if (index >= 0)
            {
                byMapsOrder[filled++] = index;
            }
        }

        if (tickIndex < TraceTicks)
        {
            PreTrace.Add([]);
            TickListTrace.Add([]);
        }

        __state = Stopwatch.GetTimestamp();
    }

    private static void SingleTickFinalizer(long __state)
    {
        if (__state < 0 || !InWindow)
        {
            return;
        }

        total[tickIndex] += Stopwatch.GetTimestamp() - __state;
        for (int index = 0; index < count; index++)
        {
            tempSum[index] += measured[index].mapTemperature.OutdoorTemp;
        }
    }

    private static int Rotation => (phase + tickIndex) % Math.Max(1, count);

    private static IEnumerable<CodeInstruction> DispatchTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo preTick = AccessTools.Method(typeof(Map), nameof(Map.MapPreTick));
        MethodInfo postTick = AccessTools.Method(typeof(Map), nameof(Map.MapPostTick));
        MethodInfo preDispatch = AccessTools.Method(typeof(RimRunnerMapCost), nameof(DispatchPreTick));
        MethodInfo postDispatch = AccessTools.Method(typeof(RimRunnerMapCost), nameof(DispatchPostTick));
        List<CodeInstruction> codes = instructions.ToList();
        int preHits = 0;
        int postHits = 0;
        foreach (CodeInstruction code in codes)
        {
            if (code.Calls(preTick))
            {
                code.opcode = OpCodes.Call;
                code.operand = preDispatch;
                preHits++;
            }
            else if (code.Calls(postTick))
            {
                code.opcode = OpCodes.Call;
                code.operand = postDispatch;
                postHits++;
            }
        }

        if (preHits != 1 || postHits != 1)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 开销计量器的轮换要 DoSingleTick 里 MapPreTick 与 MapPostTick 各恰好一处调用，实际 MapPreTick "
                                                + preHits + " 处、MapPostTick " + postHits + " 处");
        }

        return codes;
    }

    internal static void DispatchPreTick(Map map) => Rotated(map, ref preSeen, record: true).MapPreTick();

    internal static void DispatchPostTick(Map map) => Rotated(map, ref postSeen, record: false).MapPostTick();

    private static Map Rotated(Map map, ref int seen, bool record)
    {
        if (!rotate || !InWindow || IndexOf(map) < 0)
        {
            return map;
        }

        int slot = seen++;
        if (slot >= count)
        {
            return map;
        }

        int index = byMapsOrder[(slot + Rotation) % count];
        if (record && tickIndex < TraceTicks)
        {
            PreTrace[tickIndex].Add(index);
        }

        return measured[index];
    }

    private static void TickListPrefix(TickList __instance)
    {
        if (!rotate || !InWindow)
        {
            return;
        }

        List<List<Thing>> lists = ThingLists(__instance);
        List<Thing> bucket = lists[Find.TickManager.TicksGame % TickIntervalOf(__instance)];
        others.Clear();
        for (int index = 0; index < count; index++)
        {
            groups[index].Clear();
        }

        bool any = false;
        for (int position = 0; position < bucket.Count; position++)
        {
            Thing thing = bucket[position];
            int index = thing.Destroyed ? -1 : IndexOf(thing.MapHeld);
            if (index < 0)
            {
                others.Add(thing);
            }
            else
            {
                groups[index].Add(thing);
                any = true;
            }
        }

        if (!any)
        {
            return;
        }

        bucket.Clear();
        bucket.AddRange(others);
        List<int>? trace = tickIndex < TraceTicks && TickListTrace[tickIndex].Count == 0 ? TickListTrace[tickIndex] : null;
        for (int slot = 0; slot < count; slot++)
        {
            int index = byMapsOrder[(slot + Rotation) % count];
            bucket.AddRange(groups[index]);
            if (trace != null && groups[index].Count > 0)
            {
                trace.Add(index);
            }
        }

        others.Clear();
        for (int index = 0; index < count; index++)
        {
            groups[index].Clear();
        }
    }

    private static void ThingPrefix(Thing __instance, out Stamp __state)
    {
        __state = default;
        if (!recording)
        {
            return;
        }

        __state.Counted = true;
        if (depth++ > 0 || !InWindow)
        {
            __state.At = -1;
            return;
        }

        int index = IndexOf(__instance.MapHeld);
        __state.Map = index;
        __state.At = index < 0 ? -1 : Stopwatch.GetTimestamp();
    }

    private static void ThingFinalizer(Stamp __state)
    {
        if (!__state.Counted)
        {
            return;
        }

        depth--;
        if (__state.At < 0 || !InWindow)
        {
            return;
        }

        things[__state.Map][tickIndex] += Stopwatch.GetTimestamp() - __state.At;
        calls[__state.Map]++;
    }

    private static void PrePrefix(Map __instance, out Stamp __state) => __state = MapStamp(__instance);

    private static void PreFinalizer(Stamp __state)
    {
        if (__state.At >= 0 && __state.Counted && InWindow)
        {
            pre[__state.Map][tickIndex] += Stopwatch.GetTimestamp() - __state.At;
        }
    }

    private static void PostPrefix(Map __instance, out Stamp __state) => __state = MapStamp(__instance);

    private static void PostFinalizer(Stamp __state)
    {
        if (__state.At >= 0 && __state.Counted && InWindow)
        {
            post[__state.Map][tickIndex] += Stopwatch.GetTimestamp() - __state.At;
        }
    }

    private static Stamp MapStamp(Map map)
    {
        Stamp stamp = default;
        if (!InWindow)
        {
            return stamp;
        }

        int index = IndexOf(map);
        if (index < 0)
        {
            return stamp;
        }

        stamp.Counted = true;
        stamp.Map = index;
        stamp.At = Stopwatch.GetTimestamp();
        return stamp;
    }
}

[HarmonyPatch(typeof(MapTemperature), nameof(MapTemperature.OutdoorTemp), MethodType.Getter)]
internal static class Patch_MapTemperature_OutdoorTemp_RimRunnerMapCost
{
    private static bool Prepare() => GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(MapTemperature __instance, ref float __result) => RimRunnerMapCost.OutdoorTempPostfix(__instance, ref __result);
}

[HarmonyPatch(typeof(MapTemperature), nameof(MapTemperature.SeasonalTemp), MethodType.Getter)]
internal static class Patch_MapTemperature_SeasonalTemp_RimRunnerMapCost
{
    private static bool Prepare() => GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(MapTemperature __instance, ref float __result) => RimRunnerMapCost.SeasonalTempPostfix(__instance, ref __result);
}
