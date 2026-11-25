using System.Diagnostics;

namespace RimRunner;

/// <summary>tick 推进原语：burst 推进、条件推进与纯 tick 计时。</summary>
public static class RimRunnerTimeDriver
{
    internal const int MaxBurstTicksPerCall = 5000;
    private static readonly FieldInfo? UltraSpeedBoostField = typeof(TickManager).GetField(
        "UltraSpeedBoost", BindingFlags.Static | BindingFlags.NonPublic);
    private static int vanillaTickDepth;
    private static bool advancingBurst;
    private static bool inBurstCallback;
    private static string? heldBy;

    internal static bool InBurstCallback => inBurstCallback;

    internal static void HoldTicks(string holder, Action body)
    {
        heldBy = holder;
        try
        {
            body();
        }
        finally
        {
            heldBy = null;
        }
    }

    private static void ThrowIfHeld(string entry)
    {
        if (heldBy != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + heldBy + " 里禁止推 tick（" + entry
                + "）");
        }
    }

    private static void SyncAllDrawPos()
    {
        List<Map> maps = Find.Maps;
        for (int mapIndex = 0; mapIndex < maps.Count; mapIndex++)
        {
            IReadOnlyList<Pawn> pawns = maps[mapIndex].mapPawns.AllPawnsSpawned;
            for (int pawnIndex = 0; pawnIndex < pawns.Count; pawnIndex++)
            {
                RimRunnerPawns.SyncDrawPos(pawns[pawnIndex]);
            }
        }
    }

    /// <summary>同一 update 内推进 ticks 个真实 tick。</summary>
    /// <remarks>同一帧内推完、帧时间不走；每 tick 前把绘制位置重置到插值根，同 vanilla 6 倍速以上；推 tick 期间与 AsCurrentMap 作用域内调即抛。无 1 / 3 倍速的弹簧平滑与帧时间，不能据此断渲染平滑度。</remarks>
    /// <param name="ticks">推进的 tick 数，只收 0–5000。</param>
    public static void AdvanceBurst(int ticks)
    {
        AdvanceBurst(ticks, shouldContinue: null);
    }

    /// <summary>推进到 done 成立或到 cap。</summary>
    /// <remarks>每轮先判 done、再调 onStep、后推进 pollInterval 个 tick。最后一次推进之后仍判一次 done，latch 型判定靠这次收尾；持续错误级日志熔断即停止推进，不再判。pollInterval 取被观测量的节流周期。</remarks>
    /// <param name="cap">推进上限，单位 tick，不得为负。</param>
    /// <param name="done">停止条件，成立即停止推进。</param>
    /// <param name="pollInterval">判定粒度，单位 tick，只收 1–5000。</param>
    /// <param name="onStep">每轮推进之前运行的动作，可省。</param>
    /// <returns>实际推进的 tick。</returns>
    public static int AdvanceUntil(int cap, Func<bool> done, int pollInterval,
        Action? onStep = null)
    {
        if (done == null)
        {
            throw new ArgumentNullException(nameof(done));
        }

        if (cap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cap), cap, "推进上限不能为负");
        }

        if (pollInterval is < 1 or > MaxBurstTicksPerCall)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval), pollInterval, "pollInterval 只接受 1..5000t");
        }

        int elapsed = 0;
        while (!done() && elapsed < cap)
        {
            onStep?.Invoke();
            int step = Math.Min(pollInterval, cap - elapsed);
            int advanced = AdvanceBurstCounted(step, shouldContinue: null);
            elapsed += advanced;
            if (advanced < step)
            {
                break;
            }
        }

        return elapsed;
    }

    /// <summary>暂停状态下跨帧连推到 done 成立或推满 cap，每帧至多 5000 tick。</summary>
    /// <remarks>yield 它，每 tick 推之前判一次 done，帧与帧之间可取消，原来的速度推完还原。调用时 done 已成立即零推进。持续错误级日志熔断即停止推进，结果按未成立给。done 抛出即中止调用方。</remarks>
    /// <param name="cap">推进上限，单位 tick，不得为负。</param>
    /// <param name="done">停止条件，成立即停止推进。</param>
    /// <returns>跨帧原语，结果是成立与否与推进的 tick 数。</returns>
    public static RimRunnerOp<RimRunnerWaitUntilResult> WaitUntil(int cap, Func<bool> done)
    {
        if (done == null)
        {
            throw new ArgumentNullException(nameof(done));
        }

        if (cap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cap), cap, "推进上限不能为负");
        }

        RimRunnerContext? owner = RimRunnerContext.Active;
        return new RimRunnerOp<RimRunnerWaitUntilResult>("WaitUntil", op => WaitUntilSteps(op, cap, done, owner));
    }

    private static IEnumerable<RimRunnerWait> WaitUntilSteps(RimRunnerOp<RimRunnerWaitUntilResult> op, int cap,
        Func<bool> done, RimRunnerContext? owner)
    {
        bool Done()
        {
            using IDisposable? scope = owner is { IsScene: true } && RimRunnerContext.Active != owner ? owner.EnterScene() : null;
            try
            {
                return done();
            }
            catch (Exception exception)
            {
                throw new RimRunnerConditionException(exception);
            }
        }

        bool met = Done();
        int advanced = 0;
        TimeSpeed previous = Find.TickManager.CurTimeSpeed;
        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        try
        {
            while (!met && advanced < cap)
            {
                int chunk = Math.Min(MaxBurstTicksPerCall, cap - advanced);
                int moved = AdvanceBurstCounted(chunk, () => !(met = Done()));
                advanced += moved;
                if (met || moved < chunk)
                {
                    break;
                }

                met = Done();
                if (!met && advanced < cap)
                {
                    yield return RimRunnerWait.Frames(1);
                }
            }
        }
        finally
        {
            Find.TickManager.CurTimeSpeed = previous;
        }

        op.SetResult(new RimRunnerWaitUntilResult(met, advanced));
    }

    /// <summary>burst 推进，每 tick 先调用 shouldContinue。</summary>
    /// <remarks>同一帧内推完、帧时间不走；每 tick 前把绘制位置重置到插值根，同 vanilla 6 倍速以上；推 tick 期间与 AsCurrentMap 作用域内调即抛。无 1 / 3 倍速的弹簧平滑与帧时间，不能据此断渲染平滑度。</remarks>
    /// <param name="ticks">推进 tick 数上限，只收 0–5000。</param>
    /// <param name="shouldContinue">每 tick 推进前判，为假即停止推进；可为 null。</param>
    public static void AdvanceBurst(int ticks, Func<bool>? shouldContinue)
    {
        AdvanceBurstCounted(ticks, shouldContinue);
    }

    internal static int AdvanceBurstCounted(int ticks, Func<bool>? shouldContinue)
    {
        if (ticks is < 0 or > MaxBurstTicksPerCall)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ticks), ticks, "单次 burst 只接受 0..5000t");
        }

        if (vanillaTickDepth > 0 || advancingBurst)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 禁止在推 tick 期间（burst 或按原版速度推进的 tick 内）再启动 burst");
        }

        RimRunnerCurrentMap.ThrowIfBorrowing("Burst");
        RimRunnerScreenshotDriver.ThrowIfPreparing("Burst");
        ThrowIfHeld("Burst");

        bool timeSpeedWasPaused = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
        if (timeSpeedWasPaused)
        {
            Find.TickManager.TogglePaused();
        }

        advancingBurst = true;
        int advanced = 0;
        try
        {
            for (int i = 0; i < ticks; i++)
            {
                if (RimRunnerSustainedErrorBreaker.ShouldStop())
                {
                    break;
                }

                if (shouldContinue != null)
                {
                    bool keepGoing;
                    inBurstCallback = true;
                    try
                    {
                        keepGoing = shouldContinue();
                    }
                    finally
                    {
                        inBurstCallback = false;
                    }

                    if (!keepGoing)
                    {
                        break;
                    }
                }

                RimRunnerLongEventDrain.Drain();
                SyncAllDrawPos();
                Find.TickManager.DoSingleTick();
                advanced++;
                RimRunnerCaseWatch.SampleTick();
                RimRunnerFootprintProbe.SampleTick();
                if (i % RimRunnerLiveServer.BurstHeartbeatTicks == 0)
                {
                    RimRunnerLiveServer.PublishBurstHeartbeat(Find.TickManager.TicksGame);
                }
            }

            RimRunnerLongEventDrain.Drain();
        }
        finally
        {
            advancingBurst = false;
            if (timeSpeedWasPaused
                && Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
            {
                Find.TickManager.TogglePaused();
            }
        }

        return advanced;
    }

    /// <summary>纯 tick 计时：推进 ticks 个 tick，返回墙钟毫秒。</summary>
    /// <remarks>每 tick 只运行 DoSingleTick，pawn 绘制位置保持推进前的值，引擎的逐 tick 采样、持续错误级日志熔断与长事件补执行都不发生。ticks 只收 0–5000，非 Readout 用例调用出警告。</remarks>
    public static double MeasureRawTicks(int ticks)
    {
        if (ticks is < 0 or > MaxBurstTicksPerCall)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ticks), ticks, "单次 burst 只接受 0..5000t");
        }

        if (vanillaTickDepth > 0 || advancingBurst)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 禁止在推 tick 期间（burst 或按原版速度推进的 tick 内）再启动 burst");
        }

        RimRunnerCurrentMap.ThrowIfBorrowing("Burst");
        RimRunnerScreenshotDriver.ThrowIfPreparing("Burst");
        ThrowIfHeld("Burst");

        bool timeSpeedWasPaused = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
        if (timeSpeedWasPaused)
        {
            Find.TickManager.TogglePaused();
        }

        advancingBurst = true;
        int startTick = Find.TickManager.TicksGame;
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < ticks; i++)
            {
                Find.TickManager.DoSingleTick();
            }
        }
        finally
        {
            watch.Stop();
            RimRunnerSustainedErrorBreaker.NoteUnobservedTicks(Find.TickManager.TicksGame - startTick);
            RimRunnerLiveServer.PublishBurstHeartbeat(Find.TickManager.TicksGame);
            advancingBurst = false;
            if (timeSpeedWasPaused
                && Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
            {
                Find.TickManager.TogglePaused();
            }
        }

        return watch.Elapsed.TotalMilliseconds;
    }

    internal static bool Advancing => advancingBurst || vanillaTickDepth > 0;

    internal static void ThrowIfAdvancing(string entry)
    {
        if (advancingBurst || vanillaTickDepth > 0)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 推进 tick 期间禁止" + entry
                                                + "：同一段推进的前后 tick 会混入两种状态");
        }
    }

    internal static void EnterVanillaTick()
    {
        vanillaTickDepth++;
        RimRunnerCaseWatch.SampleTick();
    }

    internal static void ExitVanillaTick()
    {
        if (vanillaTickDepth <= 0)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 按原版速度推进的 tick 退出次数超过进入次数");
        }

        vanillaTickDepth--;
    }

    internal static TimeSpeed BeginVanillaSpeed(
        TimeSpeed target, out bool previousUltraSpeedBoost, out bool previousNeverForceNormalSpeed)
    {
        if (target == TimeSpeed.Paused)
        {
            throw new ArgumentOutOfRangeException(nameof(target), target,
                "按原版速度推 tick 不能以 Paused 启动");
        }

        RimRunnerCurrentMap.ThrowIfBorrowing("VanillaSpeed");
        RimRunnerScreenshotDriver.ThrowIfPreparing("VanillaSpeed");
        ThrowIfHeld("VanillaSpeed");

        TimeSpeed previous = Find.TickManager.CurTimeSpeed;
        previousUltraSpeedBoost = ReadUltraSpeedBoost();
        previousNeverForceNormalSpeed = DebugViewSettings.neverForceNormalSpeed;
        try
        {
            if (target == TimeSpeed.Ultrafast)
            {
                WriteUltraSpeedBoost(true);
            }

            DebugViewSettings.neverForceNormalSpeed = true;
            EnsureVanillaSpeed(target);
        }
        catch
        {
            WriteUltraSpeedBoost(previousUltraSpeedBoost);
            DebugViewSettings.neverForceNormalSpeed = previousNeverForceNormalSpeed;
            throw;
        }

        return previous;
    }

    internal static void EnsureVanillaSpeed(TimeSpeed target)
    {
        Find.TickManager.CurTimeSpeed = target;
        if (Find.TickManager.CurTimeSpeed != target)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 无法将游戏速度切换到 " + target);
        }
    }

    internal static void RestoreTimeSpeed(TimeSpeed previous)
    {
        Find.TickManager.CurTimeSpeed = previous;
        if (Find.TickManager.CurTimeSpeed != previous)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 无法把游戏速度还原为 " + previous);
        }
    }

    internal static void RestoreVanillaSpeed(
        TimeSpeed previous, bool previousUltraSpeedBoost, bool previousNeverForceNormalSpeed)
    {
        try
        {
            RestoreTimeSpeed(previous);
        }
        finally
        {
            DebugViewSettings.neverForceNormalSpeed = previousNeverForceNormalSpeed;
            WriteUltraSpeedBoost(previousUltraSpeedBoost);
        }
    }

    internal static bool ReadUltraSpeedBoost()
    {
        if (UltraSpeedBoostField == null)
        {
            throw new MissingFieldException(typeof(TickManager).FullName, "UltraSpeedBoost");
        }

        return (bool)UltraSpeedBoostField.GetValue(null)!;
    }

    internal static void WriteUltraSpeedBoost(bool value)
    {
        if (UltraSpeedBoostField == null)
        {
            throw new MissingFieldException(typeof(TickManager).FullName, "UltraSpeedBoost");
        }

        UltraSpeedBoostField.SetValue(null, value);
    }
}

/// <summary>一次跨帧条件推进的结果：成立与否与推进的 tick 数。</summary>
/// <param name="Met">停下时条件成立。</param>
/// <param name="Ticks">实际推进的 tick 数。</param>
public sealed record RimRunnerWaitUntilResult(bool Met, int Ticks);

internal sealed class RimRunnerConditionException(Exception inner)
    : Exception("WaitUntil 的停止条件抛出 " + inner.GetType().Name + "：" + inner.Message, inner);
