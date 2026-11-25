namespace RimRunner;

internal static class RimRunnerSustainedErrorBreaker
{
    internal const int DefaultPumpThreshold = RimRunnerEngineArgs.BreakerDefaultPumps;
    internal const int DefaultErrorThreshold = RimRunnerEngineArgs.BreakerDefaultErrors;
    private const int FirstErrorChars = 160;

    internal sealed class Counter
    {
        internal readonly int PumpThreshold;
        internal readonly int ErrorThreshold;
        internal int DirtyPumps;
        internal int PumpObservations;
        internal int TickObservations;
        internal bool Tripped;
        private int lastSeenErrors;

        internal Counter(int pumpThreshold, int errorThreshold)
        {
            PumpThreshold = pumpThreshold;
            ErrorThreshold = errorThreshold;
        }

        internal bool OnPump(int errorsInWindow)
        {
            PumpObservations++;
            DirtyPumps = errorsInWindow > lastSeenErrors ? DirtyPumps + 1 : 0;
            lastSeenErrors = errorsInWindow;
            if (DirtyPumps >= PumpThreshold)
            {
                Tripped = true;
            }

            return Tripped;
        }

        internal bool OnTick(int errorsInWindow)
        {
            TickObservations++;
            if (errorsInWindow >= ErrorThreshold)
            {
                Tripped = true;
            }

            return Tripped;
        }
    }

    private static RimRunnerLogCapture? armedCapture;
    private static Counter? counter;
    private static int armFrame;
    private static int armTick;
    private static int unobservedTicks;
    private static string? pendingTrip;
    private static string? pendingDriveTrip;
    private static string? pendingTripStack;

    internal static int RunObservations { get; private set; }
    internal static int RunTrips { get; private set; }

    internal static int PumpThreshold => ReadThreshold(RimRunnerEngineArgs.BreakerPumps, DefaultPumpThreshold);
    internal static int ErrorThreshold => ReadThreshold(RimRunnerEngineArgs.BreakerErrors, DefaultErrorThreshold);

    internal static bool Tripped => counter?.Tripped == true;

    internal static bool Armed => counter != null;

    internal static void ResetForRun()
    {
        Disarm();
        RunObservations = 0;
        RunTrips = 0;
        pendingTrip = null;
        pendingDriveTrip = null;
        pendingTripStack = null;
    }

    internal static void Arm(RimRunnerLogCapture capture)
    {
        if (capture == null)
        {
            return;
        }

        armedCapture = capture;
        counter = new Counter(PumpThreshold, ErrorThreshold);
        idleCounter = null;
        armFrame = RimRunnerFrame.Current;
        armTick = CurrentTick();
        unobservedTicks = 0;
    }

    internal static void NoteUnobservedTicks(int ticks)
    {
        if (counter != null)
        {
            unobservedTicks += ticks;
        }
    }

    internal static void Disarm()
    {
        if (counter != null)
        {
            int observations = counter.PumpObservations + counter.TickObservations;
            RunObservations += observations;
            if (IsIdleWindow(observations, RimRunnerFrame.Current - armFrame, CurrentTick() - armTick, unobservedTicks))
            {
                RimRunnerEngineFault.Raise("持续错误级日志熔断在" + (armedCapture?.Owner ?? "（无名窗）")
                                           + "空转：过帧或推 tick 却零次读数，持续错误级日志未经检查");
            }
        }

        armedCapture = null;
        counter = null;
    }

    internal static void ObserveOnPump()
    {
        if (counter == null)
        {
            ObserveIdle();
            return;
        }

        if (counter.Tripped)
        {
            return;
        }

        if (counter.OnPump(RimRunnerLogMonitor.CountErrors(armedCapture)))
        {
            RecordTrip("连续 " + counter.DirtyPumps + " 次泵到新错误级日志");
        }
    }

    internal static bool ShouldStop()
    {
        if (counter == null)
        {
            return false;
        }

        if (counter.Tripped)
        {
            return true;
        }

        int errors = RimRunnerLogMonitor.CountErrors(armedCapture);
        if (counter.OnTick(errors))
        {
            RecordTrip("本窗累计 " + errors + " 条错误级日志");
        }

        return counter.Tripped;
    }

    private static Counter? idleCounter;

    private static void ObserveIdle()
    {
        idleCounter ??= new Counter(PumpThreshold, int.MaxValue);
        bool wasTripped = idleCounter.Tripped;
        idleCounter.OnPump(RimRunnerUnownedErrors.Total);
        if (wasTripped)
        {
            if (idleCounter.DirtyPumps == 0)
            {
                idleCounter = null;
            }

            return;
        }

        if (idleCounter.Tripped)
        {
            string text = "没有用例或 live 驱动在执行时，连续 " + idleCounter.DirtyPumps + " 帧都有新的错误级日志";
            RimRunnerUnownedErrors.MarkSustained(text);
            RimRunnerEngineLog.Warn(text + "（正文与栈见本日志的「错误级日志调用栈」条目）");
        }
    }

    internal static string? TakeTrip(out string? firstErrorStack)
    {
        string? trip = pendingTrip;
        firstErrorStack = pendingTripStack;
        pendingTrip = null;
        pendingDriveTrip = null;
        pendingTripStack = null;
        return trip;
    }

    internal static string? TakeDriveTrip()
    {
        string? trip = pendingDriveTrip;
        pendingTrip = null;
        pendingDriveTrip = null;
        pendingTripStack = null;
        return trip;
    }

    private static void RecordTrip(string how)
    {
        RunTrips++;
        RimRunnerLogLine? first = RimRunnerLogMonitor.FirstErrorOrNull(armedCapture);
        string firstText = first?.Text ?? "<无>";
        if (firstText.Length > FirstErrorChars)
        {
            firstText = firstText[..FirstErrorChars] + "…";
        }

        string cause = how + "（阈 " + counter!.PumpThreshold + " 泵 / " + counter!.ErrorThreshold + " 条）";
        pendingTrip = "熔断：" + cause + "，本用例余下的推进已停止；首条 ← \"" + firstText + "\"";
        pendingDriveTrip = "持续错误级日志熔断：" + cause + "，推进已停止；首条 ← \"" + firstText + "\"";
        pendingTripStack = first?.Stack;
    }

    internal static bool IsIdleWindow(int observations, int framesElapsed, int ticksElapsed, int unobservedTicks) =>
        observations == 0 && (framesElapsed > 0 || ticksElapsed - unobservedTicks > 0);

    private static int CurrentTick() => Current.Game?.tickManager?.TicksGame ?? 0;

    internal static IDisposable SubstituteRunTotalsForSelfTest(int observations, int trips)
    {
        RunTotalsScope scope = new(RunObservations, RunTrips);
        RunObservations = observations;
        RunTrips = trips;
        return scope;
    }

    private sealed class RunTotalsScope : IDisposable
    {
        private readonly int observations;
        private readonly int trips;

        internal RunTotalsScope(int observations, int trips)
        {
            this.observations = observations;
            this.trips = trips;
        }

        public void Dispose()
        {
            RunObservations = observations;
            RunTrips = trips;
        }
    }

    private static int ReadThreshold(string arg, int fallback)
    {
        return GenCommandLine.TryGetCommandLineArg(arg, out string raw)
               && int.TryParse(raw, out int value)
               && value > 0
            ? value
            : fallback;
    }
}
