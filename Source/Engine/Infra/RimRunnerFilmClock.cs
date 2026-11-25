using Verse.Sound;

namespace RimRunner;

internal static class RimRunnerFilmClock
{
    private static int speed;
    private static bool frameDue;
    private static float? pinnedDeltaTime;
    private static bool held;
    private static Func<int>? frameWork;
    private static Func<bool>? frameReady;
    private static bool frameRuns;
    private static float frameSeconds;

    private static bool stageActive;
    private static Action<string>? stageFailure;
    private static float stageSeconds;

    internal const float PinnedTimeStart = RimRunnerEngineArgs.FilmPinnedTimeStart;
    private const float LetterSettledSeconds = 2f;

    private static Action<Waylith.UnityCompat.UnityTimeState, float>? setStateTime;

    private static bool timePinned;
    private static float pinnedFrameSeconds;
    private static int capturedFrames;

    private static bool cameraLocked;
    private static Vector3 cameraPos;
    private static float cameraSize;
    private static FloatRange? lockedSizeRange;

    internal static bool Armed { get; private set; }

    internal static bool StageActive => stageActive;

    internal static bool SkipsFrame => Armed && !frameRuns;

    internal static string? TimeUnpinned { get; private set; }

    internal static void Hold()
    {
        held = true;
        pinnedDeltaTime = 0f;
        PinClockAtStart();
    }

    internal static void Arm(int multiplier, int fps, Func<bool> ready, Func<int> work)
    {
        Armed = true;
        speed = multiplier;
        frameSeconds = fps > 0 ? 1f / fps : 0f;
        pinnedDeltaTime = frameSeconds;
        frameReady = ready;
        frameWork = work;
        frameDue = true;
        frameRuns = true;
        ArmTime(fps);
    }

    internal static void ArmStage(Action<string> fail)
    {
        stageActive = true;
        stageFailure = fail;
    }

    internal static void DisarmStage()
    {
        stageActive = false;
        stageFailure = null;
    }

    internal static void BeginFrame()
    {
        if (Armed)
        {
            frameRuns = frameDue && frameReady!();
            pinnedDeltaTime = frameRuns ? frameSeconds : 0f;
        }

        PinTime();
    }

    private static void ArmTime(int fps)
    {
        if (!held)
        {
            PinClockAtStart();
        }

        if (!timePinned)
        {
            return;
        }

        pinnedFrameSeconds = fps > 0 ? 1f / fps : 0f;
        capturedFrames = 0;
        if (Find.LetterStack != null)
        {
            float clock = PinnedClock;
            foreach (Letter letter in Find.LetterStack.LettersListForReading)
            {
                letter.arrivalTime = clock - LetterSettledSeconds;
            }
        }

        RimRunnerFilmAlerts.Settle();
        PinTime();
    }

    private static void PinClockAtStart()
    {
        float before = Time.realtimeSinceStartup;
        float coroutineBefore = RimRunnerFilmCoroutineClock.Now;
        PinClock();
        CarrySampleAges(before);
        RimRunnerFilmCoroutineClock.Shift(RimRunnerFilmCoroutineClock.Now - coroutineBefore);
    }

    private static void PinClock()
    {
        timePinned = false;
        TimeUnpinned = null;
        if (!RimRunnerProcessMode.Film)
        {
            return;
        }

        float wall = Waylith.UnityCompat.UnityRuntime.Current!.Time.RealtimeSinceStartup;
        if (wall >= PinnedTimeStart)
        {
            TimeUnpinned = "拍摄实例已运行 " + wall.ToString("0") + " 秒，超过钉位起点 " + PinnedTimeStart.ToString("0")
                           + " 秒：Time.time 与 realtimeSinceStartup 按墙钟推进";
            return;
        }

        if (setStateTime == null
            && AccessTools.PropertySetter(typeof(Waylith.UnityCompat.UnityTimeState), nameof(Waylith.UnityCompat.UnityTimeState.Time)) is { } setter)
        {
            setStateTime = AccessTools.MethodDelegate<Action<Waylith.UnityCompat.UnityTimeState, float>>(setter);
        }

        if (setStateTime == null)
        {
            TimeUnpinned = "无法解析 Waylith.UnityCompat.UnityTimeState.Time 的 setter：Time.time 与 realtimeSinceStartup 按墙钟推进";
            return;
        }

        timePinned = true;
        pinnedFrameSeconds = 0f;
        capturedFrames = 0;
        stageSeconds = 0f;
        PinTime();
    }

    internal static void PinTime()
    {
        if (timePinned)
        {
            setStateTime!(Waylith.UnityCompat.UnityRuntime.Current!.Time, PinnedClock);
        }
    }

    internal static bool TryPinnedRealtime(out float realtime)
    {
        realtime = timePinned ? PinnedClock : 0f;
        return timePinned;
    }

    private static float PinnedClock => PinnedTimeStart + stageSeconds + capturedFrames * pinnedFrameSeconds;

    private static AccessTools.FieldRef<Sustainer, List<SubSustainer>>? subSustainers;
    private static AccessTools.FieldRef<SubSustainer, List<SampleSustainer>>? sustainerSamples;
    private static bool sampleFieldsResolved;

    private static void CarrySampleAges(float before)
    {
        float shift = Time.realtimeSinceStartup - before;
        if (shift >= 0f || Find.SoundRoot?.sustainerManager is not { } manager)
        {
            return;
        }

        if (!sampleFieldsResolved)
        {
            sampleFieldsResolved = true;
            if (AccessTools.Field(typeof(Sustainer), "subSustainers") is { } subs
                && subs.FieldType == typeof(List<SubSustainer>)
                && AccessTools.Field(typeof(SubSustainer), "samples") is { } samples
                && samples.FieldType == typeof(List<SampleSustainer>))
            {
                subSustainers = AccessTools.FieldRefAccess<Sustainer, List<SubSustainer>>(subs);
                sustainerSamples = AccessTools.FieldRefAccess<SubSustainer, List<SampleSustainer>>(samples);
            }
        }

        if (subSustainers == null || sustainerSamples == null)
        {
            return;
        }

        foreach (Sustainer sustainer in manager.AllSustainers)
        {
            foreach (SubSustainer sub in subSustainers(sustainer))
            {
                foreach (SampleSustainer sample in sustainerSamples(sub))
                {
                    sample.startRealTime += shift;
                }
            }
        }
    }

    internal static void Disarm()
    {
        float before = Time.realtimeSinceStartup;
        float coroutineBefore = RimRunnerFilmCoroutineClock.Now;
        DisarmClock();
        CarrySampleAges(before);
        RimRunnerFilmCoroutineClock.Shift(RimRunnerFilmCoroutineClock.Now - coroutineBefore);
    }

    private static void DisarmClock()
    {
        Armed = false;
        held = false;
        frameDue = false;
        frameRuns = false;
        pinnedDeltaTime = null;
        frameReady = null;
        frameWork = null;
        cameraLocked = false;
        if (lockedSizeRange is FloatRange sizeRange && Find.CameraDriver is CameraDriver driver)
        {
            driver.config.sizeRange = sizeRange;
        }

        lockedSizeRange = null;
        stageActive = false;
        stageFailure = null;
        stageSeconds = 0f;
        if (timePinned)
        {
            Waylith.UnityCompat.UnityTimeState state = Waylith.UnityCompat.UnityRuntime.Current!.Time;
            setStateTime!(state, state.RealtimeSinceStartup);
        }

        timePinned = false;
    }

    internal static void FrameCaptured()
    {
        frameDue = true;
        capturedFrames++;
    }

    internal static void OnTickManagerUpdate(ref int ticksThisFrame)
    {
        ticksThisFrame = 0;
        if (!frameRuns)
        {
            return;
        }

        frameRuns = false;
        frameDue = false;
        ticksThisFrame = frameWork!();
    }

    internal static void OnStageUpdate(ref int ticksThisFrame)
    {
        ticksThisFrame = 0;
        Action<string> fail = stageFailure!;
        try
        {
            while (RimRunnerSceneHost.Chain is { Active: true }
                   && ticksThisFrame < RimRunnerTimeDriver.MaxBurstTicksPerCall
                   && !(RimRunnerHiddenWindowMouse.HasTarget && ticksThisFrame >= 1)
                   && !(ticksThisFrame >= 1 && RimRunnerFilmCoroutineClock.WaitDue(PinnedClock)))
            {
                RimRunnerLongEventDrain.Drain();
                Find.TickManager.DoSingleTick();
                ticksThisFrame++;
                stageSeconds += 1f / GenTicks.TicksPerRealSecond;
                PinTime();
            }
        }
        catch (Exception exception)
        {
            DisarmStage();
            fail("推进步骤抛出异常：" + exception.GetType().Name + ": " + exception.Message);
        }
    }

    internal static bool DeltaTimePinned => pinnedDeltaTime.HasValue;

    internal static bool TryFixedDeltaTime(out float deltaTime)
    {
        deltaTime = pinnedDeltaTime ?? 0f;
        return pinnedDeltaTime.HasValue;
    }

    internal static void SetSpeed(int multiplier)
    {
        speed = multiplier;
    }

    internal static void OverrideMultiplier(ref float multiplier)
    {
        if (Armed && multiplier > 0f)
        {
            multiplier = speed;
        }
    }

    internal static void LockCamera(Vector3 position, float size)
    {
        cameraLocked = true;
        cameraPos = position;
        cameraSize = size;
        ApplyCamera();
    }

    internal static void ApplyCamera()
    {
        if (!cameraLocked || RimRunnerCurrentMapProbe.EngineCurrentMap() == null)
        {
            return;
        }

        CameraDriver driver = Find.CameraDriver;
        lockedSizeRange ??= driver.config.sizeRange;
        FloatRange original = lockedSizeRange.Value;
        driver.config.sizeRange = new FloatRange(Mathf.Min(original.min, cameraSize), Mathf.Max(original.max, cameraSize));
        driver.SetRootPosAndSize(cameraPos, cameraSize);
        Camera camera = Find.Camera;
        Vector3 position = camera.transform.position;
        if (position.y < MinCameraHeight)
        {
            position.y = MinCameraHeight;
            camera.transform.position = position;
        }

        Current.SubcameraDriver?.UpdatePositions(camera);
    }

    private const float MinCameraHeight = 15f;
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
internal static class Patch_TickManager_TickManagerUpdate_RimRunnerFilm
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Film;
    }

    private static bool Prefix(ref int ___ticksThisFrame)
    {
        if (RimRunnerFilmClock.Armed)
        {
            RimRunnerFilmClock.OnTickManagerUpdate(ref ___ticksThisFrame);
            return false;
        }

        if (RimRunnerFilmClock.StageActive)
        {
            RimRunnerFilmClock.OnStageUpdate(ref ___ticksThisFrame);
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
internal static class Patch_RootPlay_Update_RimRunnerFilm
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Film;
    }

    private static void Prefix()
    {
        RimRunnerFilmClock.BeginFrame();
    }
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.TickRateMultiplier), MethodType.Getter)]
internal static class Patch_TickManager_TickRateMultiplier_RimRunnerFilm
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Film;
    }

    private static void Postfix(ref float __result)
    {
        RimRunnerFilmClock.OverrideMultiplier(ref __result);
    }
}

internal static class RimRunnerFilmDeltaTime
{
    internal static void Install(Harmony harmony)
    {
        if (!RimRunnerProcessMode.Film && RimRunnerDefScope.SelfTestAssembly == null)
        {
            return;
        }

        harmony.Patch(AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(RimRunnerFilmDeltaTime), nameof(Prefix))));
    }

    private static bool Prefix(ref float __result)
    {
        if (!RimRunnerFilmClock.TryFixedDeltaTime(out float deltaTime))
        {
            return true;
        }

        __result = deltaTime;
        return false;
    }
}

internal static class RimRunnerFilmRealtime
{
    internal static void Install(Harmony harmony)
    {
        if (!RimRunnerProcessMode.Film)
        {
            return;
        }

        harmony.Patch(AccessTools.PropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartup)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(RimRunnerFilmRealtime), nameof(Prefix))));
    }

    private static bool Prefix(ref float __result)
    {
        if (!RimRunnerFilmClock.TryPinnedRealtime(out float realtime))
        {
            return true;
        }

        __result = realtime;
        return false;
    }
}

[HarmonyPatch(typeof(RealTime), nameof(RealTime.Update))]
internal static class Patch_RealTime_Update_RimRunnerFilm
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Film;
    }

    private static void Postfix()
    {
        if (RimRunnerFilmClock.DeltaTimePinned)
        {
            RealTime.realDeltaTime = RealTime.deltaTime;
        }
    }
}

[HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.Update))]
internal static class Patch_CameraDriver_Update_RimRunnerFilm
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Film;
    }

    [HarmonyPriority(Priority.First)]
    private static void Postfix()
    {
        RimRunnerFilmClock.ApplyCamera();
    }
}

[HarmonyPatch(typeof(SectionLayer_TerrainScatter), "GenerateScats")]
internal static class Patch_SectionLayerTerrainScatter_GenerateScats_RimRunnerFilm
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Film;
    }

    private static void Prefix(Section ___section)
    {
        Rand.PushState(Gen.HashCombineInt(___section.map.ConstantRandSeed, ___section.botLeft.GetHashCode()));
    }

    private static void Finalizer()
    {
        Rand.PopState();
    }
}

[HarmonyPatch(typeof(SectionLayer_TerrainScatter), "GetScatPoints")]
internal static class Patch_SectionLayerTerrainScatter_GetScatPoints_RimRunnerFilm
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Film;
    }

    private static void Prefix(Map map)
    {
        Rand.PushState(map.ConstantRandSeed);
    }

    private static void Finalizer()
    {
        Rand.PopState();
    }
}
