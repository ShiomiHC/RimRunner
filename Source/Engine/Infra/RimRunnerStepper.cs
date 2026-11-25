namespace RimRunner;

internal interface IRimRunnerStepHost
{
    string? Refusal(RimRunnerWait wait);

    void TrackRestore(Action restore, string description);
}

internal sealed class RimRunnerStepper
{
    private const float ForcePauseLimitSeconds = 120f;

    private static RimRunnerStepper? stepping;

    private readonly Stack<(IEnumerator<RimRunnerWait> Steps, RimRunnerOp? Op)> stack = new();
    private readonly IRimRunnerStepHost host;
    private readonly string owner;
    private RimRunnerWait? waiting;
    private int framesLeft;
    private int ticksTarget;
    private bool ticksReached;
    private Exception? tickFault;
    private SpeedTakeover? speed;
    private float forcePauseSince = -1f;
    private int speedCorrections;

    internal RimRunnerStepper(IEnumerable<RimRunnerWait> body, IRimRunnerStepHost host, string owner)
    {
        stack.Push((body.GetEnumerator(), null));
        this.host = host;
        this.owner = owner;
    }

    internal bool Done => stack.Count == 0;

    internal bool Stepping => ReferenceEquals(stepping, this);

    internal static bool AnyStepping => stepping != null;

    internal bool WaitingOnFrames => waiting is RimRunnerFramesWait;

    internal bool WaitingOnTicks => waiting is RimRunnerVanillaSpeedWait;

    internal RimRunnerFrameSpeed? TicksSpeed => (waiting as RimRunnerVanillaSpeedWait)?.Speed;

    internal int Frames { get; private set; }

    internal int VanillaTicks { get; private set; }

    internal static void TrackRestore(Action restore, string description)
    {
        if (stepping == null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 原语在推进器之外登记复原（" + description + "）");
        }

        stepping.host.TrackRestore(restore, description);
    }

    internal void Resume()
    {
        if (stepping != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + owner + " 的正文在另一个正文（" + stepping.owner + "）里被推进");
        }

        waiting = null;
        stepping = this;
        try
        {
            while (stack.Count > 0)
            {
                (IEnumerator<RimRunnerWait> steps, RimRunnerOp? op) = stack.Peek();
                if (!steps.MoveNext())
                {
                    stack.Pop();
                    steps.Dispose();
                    op?.MarkCompleted();
                    continue;
                }

                RimRunnerWait wait = steps.Current
                                     ?? throw new InvalidOperationException(RimRunnerLog.Prefix + " " + owner + " 的正文 yield null");
                if (wait is RimRunnerOp nested)
                {
                    stack.Push((nested.TakeSteps(), nested));
                    continue;
                }

                string? refusal = host.Refusal(wait);
                if (refusal != null)
                {
                    throw new InvalidOperationException(RimRunnerLog.Prefix + " " + refusal);
                }

                Begin(wait);
                return;
            }
        }
        finally
        {
            stepping = null;
        }
    }

    private void Begin(RimRunnerWait wait)
    {
        switch (wait)
        {
            case RimRunnerFramesWait frames:
                framesLeft = frames.Count;
                break;
            case RimRunnerTicksWait ticks:
                RimRunnerFrameSpeed target = ticks.Speed;
                ticksTarget = Find.TickManager.TicksGame + ticks.Count;
                ticksReached = false;
                forcePauseSince = -1f;
                BeginSpeed(target);
                break;
            case RimRunnerRealTimeWait realTime:
                realTime.StartRealtime = RimRunnerRealtime.PreciseSeconds;
                forcePauseSince = -1f;
                BeginSpeed(realTime.Speed);
                break;
            default:
                throw new InvalidOperationException(RimRunnerLog.Prefix + " 无法识别的等待 " + wait.GetType().Name);
        }

        waiting = wait;
    }

    internal void Frame()
    {
        if (tickFault != null)
        {
            Exception fault = tickFault;
            tickFault = null;
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + owner + " 推进到目标 tick 时暂停失败", fault);
        }

        switch (waiting)
        {
            case RimRunnerFramesWait:
                Frames++;
                if (--framesLeft <= 0)
                {
                    Resume();
                }

                return;
            case RimRunnerTicksWait:
                Frames++;
                if (ticksReached)
                {
                    ticksReached = false;
                    Resume();
                    return;
                }

                MaintainSpeed();
                return;
            case RimRunnerRealTimeWait realTime:
                Frames++;
                realTime.Frames++;
                double now = RimRunnerRealtime.PreciseSeconds;
                if (now - realTime.StartRealtime >= realTime.Seconds)
                {
                    realTime.ElapsedSeconds = now - realTime.StartRealtime;
                    ReleaseSpeed();
                    Resume();
                    return;
                }

                MaintainSpeed();
                return;
        }
    }

    internal void Tick()
    {
        if (waiting is RimRunnerRealTimeWait realTime && !Stepping)
        {
            VanillaTicks++;
            realTime.Ticks++;
            return;
        }

        if (waiting is not RimRunnerTicksWait || ticksReached || Stepping)
        {
            return;
        }

        VanillaTicks++;
        if (Find.TickManager.TicksGame < ticksTarget)
        {
            return;
        }

        ticksReached = true;
        try
        {
            ReleaseSpeed();
        }
        catch (Exception exception)
        {
            tickFault = exception;
        }
    }

    private void MaintainSpeed()
    {
        float now = RimRunnerRealtime.Seconds;
        if (Find.TickManager.ForcePaused)
        {
            if (forcePauseSince < 0f)
            {
                forcePauseSince = now;
                return;
            }

            if (now - forcePauseSince < ForcePauseLimitSeconds)
            {
                return;
            }

            string windows = Find.WindowStack == null
                ? "<no WindowStack>"
                : string.Join(",", Find.WindowStack.Windows
                    .Where(window => window.forcePause)
                    .Select(window => window.GetType().Name)
                    .Distinct());
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + owner + " 按原版速度推 tick 时被强制暂停超过 "
                                                + ForcePauseLimitSeconds + "s：speed=" + Find.TickManager.CurTimeSpeed
                                                + " windows=[" + windows + "]");
        }

        forcePauseSince = -1f;
        TimeSpeed target = ((RimRunnerVanillaSpeedWait)waiting!).Speed.VanillaTimeSpeed();
        if (Find.TickManager.CurTimeSpeed == target)
        {
            return;
        }

        RimRunnerTimeDriver.EnsureVanillaSpeed(target);
        if (++speedCorrections == 1)
        {
            RimRunnerEngineLog.Info(owner + " 按原版速度推 tick 时检测到自动暂停或变速，已重新施加 " + target);
        }
    }

    private void BeginSpeed(RimRunnerFrameSpeed target)
    {
        RimRunnerTimeDriver.BeginVanillaSpeed(target.VanillaTimeSpeed(), out bool boost, out bool neverForceNormal);
        speed = new SpeedTakeover(boost, neverForceNormal);
    }

    private void ReleaseSpeed()
    {
        if (speed is not { } taken)
        {
            return;
        }

        speed = null;
        RimRunnerTimeDriver.RestoreVanillaSpeed(TimeSpeed.Paused, taken.UltraSpeedBoost, taken.NeverForceNormalSpeed);
    }

    internal List<Exception> Dispose()
    {
        List<Exception> faults = [];
        waiting = null;
        while (stack.Count > 0)
        {
            (IEnumerator<RimRunnerWait> steps, _) = stack.Pop();
            try
            {
                steps.Dispose();
            }
            catch (Exception exception)
            {
                faults.Add(exception);
            }
        }

        try
        {
            ReleaseSpeed();
        }
        catch (Exception exception)
        {
            faults.Add(exception);
        }

        return faults;
    }

    private readonly record struct SpeedTakeover(bool UltraSpeedBoost, bool NeverForceNormalSpeed);
}
