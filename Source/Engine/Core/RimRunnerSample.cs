namespace RimRunner;

public sealed class RimRunnerSampleRecord
{
    public required string Series;

    public required string Segment;

    public int WindowTicks;

    public Dictionary<string, string> Scope = [];

    public List<RimRunnerSampleRow> Rows = [];
}

/// <summary>样本记录里的一行：一个对象在这段窗内的若干实测量。</summary>
public sealed class RimRunnerSampleRow
{
    /// <summary>行标识，同一 series 跨轮按它对齐。</summary>
    public string Key;

    /// <summary>实测量表：只存读侧无法算出的量。</summary>
    /// <remarks>同行量与 windowTicks 一步能算出的（每 tick 成本、比值、百分比）交给读侧；跨窗、跨图统计量可存，其口径写进 AddSample 的 scope。</remarks>
    public Dictionary<string, double> Values = [];

    /// <summary>构造：给定行标识。</summary>
    /// <exception cref="ArgumentException">key 为空或全空白时抛。</exception>
    public RimRunnerSampleRow(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " 读数行的 Key 不能为空——跨轮对齐全靠它", nameof(key));
        }

        Key = key;
    }

    /// <summary>写入一个实测量并返回本行（可连续调用）。</summary>
    /// <exception cref="ArgumentException">量名为空或全空白，或本行已有同名量时抛。</exception>
    /// <exception cref="ArgumentOutOfRangeException">value 为 NaN 或无穷时抛。</exception>
    public RimRunnerSampleRow Set(string name, double value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " 读数的量名不能为空", nameof(name));
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                RimRunnerLog.Prefix + " 读数行 " + Key + " 的量 " + name
                + " 不是有限数（" + value + "）");
        }

        if (Values.ContainsKey(name))
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " 读数行 " + Key + " 重复写入量 " + name, nameof(name));
        }

        Values[name] = value;
        return this;
    }
}

/// <summary>样本窗：AddSample 的段长来源，Close 时定格。</summary>
/// <remarks>关后可交多条；未关的窗交给 AddSample 即抛。开、关、交须在开出它的那一段（同一 phase）内。</remarks>
public sealed class RimRunnerSampleWindow
{
    private readonly int openTick;
    private readonly int pinnedAtOpen;
    private readonly int generationAtOpen;
    private int generationAtClose;
    private int ticks;

    private RimRunnerSampleWindow(int segmentSerial, bool fromMeter, int openTick, int pinnedAtOpen, int generationAtOpen)
    {
        SegmentSerial = segmentSerial;
        FromMeterSource = fromMeter;
        this.openTick = openTick;
        this.pinnedAtOpen = pinnedAtOpen;
        this.generationAtOpen = generationAtOpen;
    }

    internal int SegmentSerial { get; }

    internal bool FromMeterSource { get; }

    internal string PinLabel { get; private set; } = RimRunnerReadoutContext.NoPinLabel;

    /// <summary>段长（tick）：关窗时定格。</summary>
    /// <exception cref="InvalidOperationException">窗未关时读即抛。</exception>
    public int Ticks
    {
        get
        {
            if (!Closed)
            {
                throw new InvalidOperationException(RimRunnerLog.Prefix
                    + " 样本窗未 Close，段长未定");
            }

            return ticks;
        }
    }

    /// <summary>窗是否已关。</summary>
    public bool Closed { get; private set; }

    /// <summary>关窗：段长定格为开窗以来推进的 tick 数，之后再推进不计入。</summary>
    /// <exception cref="InvalidOperationException">已关，或不在开出它的那一段（跨 phase、跨用例）时抛。</exception>
    /// <exception cref="ArgumentOutOfRangeException">开窗以来没推进过 tick 时抛。</exception>
    public void Close()
    {
        RequireCurrentSegment("关窗");
        if (Closed)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 样本窗已关：段长在第一次关窗时已定");
        }

        int advanced = Find.TickManager.TicksGame - openTick;
        if (advanced <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Ticks), advanced,
                RimRunnerLog.Prefix + " 本窗推进 0 tick：窗须在推进前开");
        }

        generationAtClose = RimRunnerTickRatePin.Generation;
        PinLabel = generationAtClose != generationAtOpen ? RimRunnerReadoutContext.MixedPin : PinName(pinnedAtOpen);
        ticks = advanced;
        Closed = true;
    }

    /// <summary>由按图开销计量的测量窗合成一个已关的样本窗：段长取各窗实际推进数之和。</summary>
    /// <param name="windows">同一段内 Measure 返回的测量窗，至少一个。</param>
    /// <exception cref="ArgumentException">windows 为空或含 null 时抛。</exception>
    /// <exception cref="InvalidOperationException">有测量窗不是在当前这一段量的时抛。</exception>
    public static RimRunnerSampleWindow FromMeter(params RimRunnerMapCostWindow[] windows)
    {
        if (windows == null || windows.Length == 0 || windows.Any(window => window == null))
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " FromMeter 须给至少一个测量窗，且不含 null", nameof(windows));
        }

        if (windows.Any(window => window.SegmentSerial != RimRunnerContext.SegmentSerial))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " FromMeter 收到非本段测量的测量窗：样本窗只在开出它的那一段（同一 phase）内有效");
        }

        RimRunnerSampleWindow window = new(RimRunnerContext.SegmentSerial, fromMeter: true, openTick: 0,
            pinnedAtOpen: windows[0].PinnedRate, generationAtOpen: windows[0].PinGeneration)
        {
            ticks = windows.Sum(each => each.Ticks),
            Closed = true
        };
        window.PinLabel = windows.Any(each => each.PinGeneration != windows[0].PinGeneration)
            ? RimRunnerReadoutContext.MixedPin
            : PinName(windows[0].PinnedRate);
        return window;
    }

    internal static RimRunnerSampleWindow Open() =>
        new(RimRunnerContext.SegmentSerial, fromMeter: false, Find.TickManager.TicksGame,
            RimRunnerTickRatePin.PinnedRate, RimRunnerTickRatePin.Generation);

    internal void RequireCurrentSegment(string action)
    {
        if (SegmentSerial != RimRunnerContext.SegmentSerial)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 样本窗不能跨段" + action
                + "：只在开出它的那一段（同一 phase、同一用例）内有效");
        }
    }

    private static string PinName(int rate) => rate == 0 ? RimRunnerReadoutContext.NoPinLabel : rate.ToString();
}
