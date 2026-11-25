namespace RimRunner;

/// <summary>按原版速度推 tick 的档位：游戏自己逐帧推 tick，帧照常渲染。</summary>
public enum RimRunnerFrameSpeed
{
    /// <summary>原版 Ultrafast 加 UltraSpeedBoost：每帧推到帧内预算用完。</summary>
    Max,
    /// <summary>原版 1 倍速（缺省）。</summary>
    Normal,
    /// <summary>原版 Fast 档（1.7 为 3 倍速）。</summary>
    Fast,
    /// <summary>原版 Superfast 档（1.7 通常 6 倍速；无地图 18、无事可做 12）。</summary>
    Superfast
}

internal static class RimRunnerFrameSpeedExtensions
{
    internal static TimeSpeed VanillaTimeSpeed(this RimRunnerFrameSpeed speed) => speed switch
    {
        RimRunnerFrameSpeed.Normal => TimeSpeed.Normal,
        RimRunnerFrameSpeed.Fast => TimeSpeed.Fast,
        RimRunnerFrameSpeed.Superfast => TimeSpeed.Superfast,
        _ => TimeSpeed.Ultrafast
    };
}

/// <summary>协程正文与原语让出的一次等待。</summary>
/// <remarks>Frames 让出若干帧、期间游戏暂停。Ticks 按原版速度逐帧推若干 tick、推完暂停；RealSeconds 同样推，推到墙钟过若干秒。RimRunnerOp 推到该原语走完。</remarks>
public abstract class RimRunnerWait
{
    private protected RimRunnerWait()
    {
    }

    /// <summary>让出 frames 帧，期间游戏暂停，帧照常渲染。</summary>
    /// <param name="frames">帧数，至少为 1。</param>
    public static RimRunnerWait Frames(int frames)
    {
        if (frames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), frames, "让出的帧数至少为 1");
        }

        return new RimRunnerFramesWait(frames);
    }

    /// <summary>按原版速度逐帧推 ticks 个 tick，推完暂停。</summary>
    /// <remarks>只有声明 IRimRunnerRealFrameCase 的用例能用；其余用例与 live 驱动 yield 它即抛。期间游戏被改速或普通暂停即重新施加，被窗口强制暂停连续 120 秒即判失败。</remarks>
    /// <param name="ticks">tick 数，至少为 1。</param>
    /// <param name="speed">推进档位，缺省 Normal。</param>
    public static RimRunnerWait Ticks(int ticks, RimRunnerFrameSpeed speed = RimRunnerFrameSpeed.Normal)
    {
        if (ticks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "推进的 tick 数至少为 1");
        }

        if (!Enum.IsDefined(speed))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "速度不在 RimRunnerFrameSpeed 之内");
        }

        return new RimRunnerTicksWait(ticks, speed);
    }

    /// <summary>按原版速度逐帧推，直到墙钟过 seconds 秒，推完暂停；结果是实际经过的墙钟、tick 与帧。</summary>
    /// <remarks>准入、暂停与改速的处置同 Ticks。</remarks>
    /// <param name="seconds">墙钟秒数，大于 0。</param>
    /// <param name="speed">推进档位，缺省 Normal。</param>
    public static RimRunnerOp<RimRunnerRealWaitResult> RealSeconds(double seconds, RimRunnerFrameSpeed speed = RimRunnerFrameSpeed.Normal)
    {
        if (!(seconds > 0) || double.IsInfinity(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "墙钟秒数须大于 0 且有限");
        }

        if (!Enum.IsDefined(speed))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "速度不在 RimRunnerFrameSpeed 之内");
        }

        return new RimRunnerOp<RimRunnerRealWaitResult>("RimRunnerWait.RealSeconds", op => RealSecondsSteps(op, new RimRunnerRealTimeWait(seconds, speed)));
    }

    private static IEnumerable<RimRunnerWait> RealSecondsSteps(RimRunnerOp<RimRunnerRealWaitResult> op, RimRunnerRealTimeWait wait)
    {
        yield return wait;

        op.SetResult(new RimRunnerRealWaitResult(wait.ElapsedSeconds, wait.Ticks, wait.Frames));
    }
}

internal sealed class RimRunnerFramesWait(int frames) : RimRunnerWait
{
    internal int Count { get; } = frames;
}

internal abstract class RimRunnerVanillaSpeedWait(RimRunnerFrameSpeed speed) : RimRunnerWait
{
    internal RimRunnerFrameSpeed Speed { get; } = speed;
}

internal sealed class RimRunnerTicksWait(int ticks, RimRunnerFrameSpeed speed) : RimRunnerVanillaSpeedWait(speed)
{
    internal int Count { get; } = ticks;
}

internal sealed class RimRunnerRealTimeWait(double seconds, RimRunnerFrameSpeed speed) : RimRunnerVanillaSpeedWait(speed)
{
    internal double Seconds { get; } = seconds;
    internal double StartRealtime;
    internal double ElapsedSeconds;
    internal int Ticks;
    internal int Frames;
}

/// <summary>RimRunnerWait.RealSeconds 的结果：实际经过的墙钟、推进的 tick 与帧。</summary>
/// <param name="ElapsedSeconds">从开始推到停下的墙钟秒数，不小于请求的秒数。</param>
/// <param name="Ticks">期间按原版速度推进的 tick 数。</param>
/// <param name="Frames">期间经过的帧数。</param>
public sealed record RimRunnerRealWaitResult(double ElapsedSeconds, int Ticks, int Frames);

/// <summary>一个跨帧原语的执行：yield 它即推到它走完，之后读 Result。</summary>
/// <remarks>只能 yield 一次；走完前读 Result 即抛。</remarks>
public abstract class RimRunnerOp : RimRunnerWait
{
    private static readonly List<RimRunnerOp> unyielded = [];

    private bool taken;

    private protected RimRunnerOp(string name)
    {
        Name = name;
        unyielded.Add(this);
    }

    /// <summary>原语名，出错时进报错行。</summary>
    public string Name { get; }

    /// <summary>已走完。</summary>
    public bool Completed { get; private protected set; }

    internal IEnumerator<RimRunnerWait> TakeSteps()
    {
        if (taken)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 原语 " + Name + " 已经 yield 过一次，每次执行另调一次原语");
        }

        taken = true;
        unyielded.Remove(this);
        return Steps();
    }

    internal static List<string> TakeUnyielded()
    {
        List<string> names = unyielded.Select(op => op.Name).ToList();
        unyielded.Clear();
        return names;
    }

    private protected abstract IEnumerator<RimRunnerWait> Steps();

    internal abstract void MarkCompleted();
}

/// <summary>一个带结果的跨帧原语的执行，T 是结果类型。</summary>
public sealed class RimRunnerOp<T> : RimRunnerOp
{
    private readonly Func<RimRunnerOp<T>, IEnumerable<RimRunnerWait>> body;
    private T? result;
    private bool hasResult;

    internal RimRunnerOp(string name, Func<RimRunnerOp<T>, IEnumerable<RimRunnerWait>> body) : base(name)
    {
        this.body = body;
    }

    /// <summary>原语的结果；走完之前读即抛。</summary>
    public T Result
    {
        get
        {
            if (!Completed)
            {
                throw new InvalidOperationException(RimRunnerLog.Prefix + " 原语 " + Name + " 未走完时读取 Result");
            }

            return result!;
        }
    }

    internal void SetResult(T value)
    {
        result = value;
        hasResult = true;
    }

    private protected override IEnumerator<RimRunnerWait> Steps() => body(this).GetEnumerator();

    internal override void MarkCompleted()
    {
        if (!hasResult)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 原语 " + Name + " 走完却没有给出结果");
        }

        Completed = true;
    }
}
