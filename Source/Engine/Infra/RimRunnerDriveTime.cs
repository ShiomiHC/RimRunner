using System.Globalization;

namespace RimRunner;

internal sealed class RimRunnerDriveJump : RimRunnerDriveVerb
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Jump;

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        int ticks = RimRunnerDriveTable.ParseGameTicks(op.Arg("duration")!)!.Value;
        Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + ticks);
        op.Line("仅将时钟调快 " + ticks + " tick（" + (ticks / (double)RimRunnerDriveTable.TicksPerDay).ToString("0.##", Invariant)
                + " 天），中间的 tick 未推进");
        yield break;
    }
}

internal sealed class RimRunnerDriveStep : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Step;

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        int requested = RimRunnerDriveTable.ParsePositiveInt(op.Arg("ticks")!)!.Value;
        bool perFrame = op.Arg("per-frame") == "true";
        TimeSpeed previous = Find.TickManager.CurTimeSpeed;
        int advanced = 0;
        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        RimRunnerSustainedErrorBreaker.Arm(op.Capture!);
        try
        {
            while (advanced < requested)
            {
                int chunk = perFrame ? 1 : Math.Min(RimRunnerDriveTable.MaxStepTicksPerFrame, requested - advanced);
                advanced += RimRunnerTimeDriver.AdvanceBurstCounted(chunk, shouldContinue: null);
                if (RimRunnerSustainedErrorBreaker.Tripped)
                {
                    op.Abort(RimRunnerSustainedErrorBreaker.TakeDriveTrip() ?? "持续错误级日志熔断");
                    break;
                }

                if (advanced < requested)
                {
                    yield return RimRunnerWait.Frames(1);
                }
            }
        }
        finally
        {
            RimRunnerSustainedErrorBreaker.Disarm();
            Find.TickManager.CurTimeSpeed = previous;
        }

        op.Line("连推 " + advanced + " / " + requested + " tick");
    }
}

internal sealed class RimRunnerDriveSpeed : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Speed;

    internal override string? Refusal(RimRunnerDriveOp op) =>
        op.Arg("speed") != RimRunnerDriveTable.SpeedPaused ? RimRunnerDriveTime.ForcePauseRefusal() : null;

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        string name = op.Arg("speed")!;
        TimeSpeed target = RimRunnerDriveTime.ToTimeSpeed(name);
        bool max = name == RimRunnerDriveTable.SpeedMax;
        TimeSpeed before = Find.TickManager.CurTimeSpeed;
        RimRunnerTimeDriver.WriteUltraSpeedBoost(max);
        DebugViewSettings.neverForceNormalSpeed = max;
        RimRunnerTimeDriver.EnsureVanillaSpeed(target);
        op.Line("游戏速度 " + RimRunnerDriveTime.SpeedName(before) + " → " + RimRunnerDriveTime.SpeedName(Find.TickManager.CurTimeSpeed) + "；最高档加速："
                + (RimRunnerTimeDriver.ReadUltraSpeedBoost() ? "开" : "关") + "；原版自动降回常速："
                + (DebugViewSettings.neverForceNormalSpeed ? "关" : "开"));
        yield break;
    }
}

internal sealed class RimRunnerDrivePlay : RimRunnerDriveVerb
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Play;

    internal override string? Refusal(RimRunnerDriveOp op) => RimRunnerDriveTime.ForcePauseRefusal();

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        float requested = RimRunnerDriveTable.ParseWallSeconds(op.Arg("duration")!)!.Value;
        string speedName = op.Arg("speed") ?? RimRunnerDriveTable.SpeedNormal;
        TimeSpeed target = RimRunnerDriveTime.ToTimeSpeed(speedName);
        RimRunnerTimeDriver.BeginVanillaSpeed(target, out bool boost, out bool neverForceNormal);
        float start = RimRunnerRealtime.Seconds;
        float elapsed = 0f;
        RimRunnerSustainedErrorBreaker.Arm(op.Capture!);
        try
        {
            while (true)
            {
                yield return RimRunnerWait.Frames(1);
                elapsed = RimRunnerRealtime.Seconds - start;
                if (RimRunnerSustainedErrorBreaker.Tripped)
                {
                    op.Abort(RimRunnerSustainedErrorBreaker.TakeDriveTrip() ?? "持续错误级日志熔断");
                    break;
                }

                if (elapsed >= requested)
                {
                    break;
                }

                if (RimRunnerDriveTime.ForcePauseRefusal() is { } forced)
                {
                    op.Abort(forced);
                    break;
                }

                if (Find.TickManager.CurTimeSpeed != target)
                {
                    RimRunnerTimeDriver.EnsureVanillaSpeed(target);
                }
            }
        }
        finally
        {
            RimRunnerSustainedErrorBreaker.Disarm();
            RimRunnerTimeDriver.RestoreVanillaSpeed(TimeSpeed.Paused, boost, neverForceNormal);
        }

        op.Line("按 " + speedName + " 运行 " + elapsed.ToString("0.#", Invariant) + " / " + requested.ToString("0.#", Invariant) + " 秒"
                + "，已暂停");
    }
}

internal static class RimRunnerDriveTime
{
    internal static TimeSpeed ToTimeSpeed(string name) => name switch
    {
        RimRunnerDriveTable.SpeedPaused => TimeSpeed.Paused,
        RimRunnerDriveTable.SpeedNormal => TimeSpeed.Normal,
        RimRunnerDriveTable.SpeedFast => TimeSpeed.Fast,
        RimRunnerDriveTable.SpeedSuperfast => TimeSpeed.Superfast,
        RimRunnerDriveTable.SpeedMax => TimeSpeed.Ultrafast,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, RimRunnerLog.Prefix + " 未知的速度名"),
    };

    internal static string SpeedName(TimeSpeed speed) => speed switch
    {
        TimeSpeed.Paused => RimRunnerDriveTable.SpeedPaused,
        TimeSpeed.Normal => RimRunnerDriveTable.SpeedNormal,
        TimeSpeed.Fast => RimRunnerDriveTable.SpeedFast,
        TimeSpeed.Superfast => RimRunnerDriveTable.SpeedSuperfast,
        TimeSpeed.Ultrafast => RimRunnerDriveTable.SpeedMax,
        _ => speed.ToString(),
    };

    internal static bool ForcePauseSelfResolving()
    {
        return LongEventHandler.ForcePause || WorldComponent_GravshipController.CutsceneInProgress;
    }

    internal static string? ForcePauseRefusal()
    {
        if (!Find.TickManager.ForcePaused)
        {
            return null;
        }

        List<string> causes = [];
        string windows = Find.WindowStack == null
            ? ""
            : string.Join("、", Find.WindowStack.Windows.Where(window => window.forcePause)
                .Select(window => window.GetType().Name).Distinct());
        if (windows.Length > 0)
        {
            causes.Add("窗口 " + windows);
        }

        if (LongEventHandler.ForcePause)
        {
            causes.Add("长事件排队中");
        }

        if (Find.TilePicker.Active)
        {
            causes.Add("地块选择器已打开");
        }

        if (WorldComponent_GravshipController.CutsceneInProgress)
        {
            causes.Add("逆重飞船起降过场进行中");
        }

        if (Find.GravshipController?.LandingAreaConfirmationInProgress == true)
        {
            causes.Add("等待确认逆重飞船着陆区");
        }

        if (MapGenerator.debugMode)
        {
            causes.Add("地图生成调试模式");
        }

        return "游戏被强制暂停，tick 无法推进" + (causes.Count > 0 ? "（" + string.Join("；", causes) + "）" : "");
    }
}
