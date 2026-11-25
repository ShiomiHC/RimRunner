using RimWorld.Planet;

namespace RimRunner;

internal static class RimRunnerSceneEnvironment
{
    private const int MorningHour = 10;
    private const int MidnightHour = 0;

    private const float ArrivedWindowHours = 2f;

    private const float NightMaxGlow = 0.1f;
    private const float DuskGlow = 0.35f;
    private const int DuskScanStepTicks = 250;

    private const int WeatherTransitionTicks = 4000;

    private static int baselineDayOfYear = -1;
    private static int lockedTicksAbs;
    private static bool scanning;

    internal static bool LightLocked { get; private set; }
    internal static WeatherDef? LockedWeather { get; private set; }

    internal static string TimeLabel(RimRunnerSceneTime time)
    {
        return time switch
        {
            RimRunnerSceneTime.Morning => "上午",
            RimRunnerSceneTime.Dusk => "黄昏",
            RimRunnerSceneTime.Midnight => "深夜",
            _ => throw new InvalidOperationException("场面时段出现未处理的取值：" + time),
        };
    }

    internal static string? JumpTo(Map map, RimRunnerSceneTime time)
    {
        if (baselineDayOfYear < 0)
        {
            throw new InvalidOperationException("基线日期未记录：OnMapReady 须先于跳时");
        }

        int wrapDays = WrapDays(baselineDayOfYear, GenLocalDate.DayOfYear(map));
        if (wrapDays > 0)
        {
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + wrapDays * GenDate.TicksPerDay);
        }

        int? ticks = TicksUntil(map, time);
        if (ticks == null)
        {
            return "本图往后一天内天光不经过黄昏段，无从跳到黄昏";
        }

        Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + ticks.Value);
        if (LightLocked)
        {
            lockedTicksAbs = Find.TickManager.TicksAbs;
        }

        return null;
    }

    internal static int WrapDays(int baselineDay, int day)
    {
        int drift = ((day - baselineDay) % GenDate.DaysPerYear + GenDate.DaysPerYear) % GenDate.DaysPerYear;
        return drift >= GenDate.DaysPerQuadrum ? GenDate.DaysPerYear - drift : 0;
    }

    internal static void ToggleLightLock()
    {
        LightLocked = !LightLocked;
        lockedTicksAbs = Find.TickManager.TicksAbs;
    }

    internal static void LockWeather(Map map, WeatherDef weather)
    {
        LockedWeather = weather ?? throw new ArgumentNullException(nameof(weather));
        ApplyWeather(map);
    }

    internal static void UnlockWeather()
    {
        LockedWeather = null;
    }

    internal static void OnMapReady(Map map)
    {
        baselineDayOfYear = GenLocalDate.DayOfYear(map);
        if (LightLocked)
        {
            lockedTicksAbs = Find.TickManager.TicksAbs;
        }

        if (LockedWeather != null)
        {
            ApplyWeather(map);
        }
    }

    internal static void OnSceneMap(Map map)
    {
        if (LockedWeather != null)
        {
            ApplyWeather(map);
        }
    }

    internal static bool OverrideSunTicks(ref int ticksAbs)
    {
        if (LightLocked && !scanning)
        {
            ticksAbs = lockedTicksAbs;
        }

        return true;
    }

    internal static void OverrideDayPercent(PlanetTile tile, ref float dayPercent)
    {
        if (LightLocked)
        {
            dayPercent = GenDate.DayPercent(lockedTicksAbs, Find.WorldGrid.LongLatOf(tile).x);
        }
    }

    private static void ApplyWeather(Map map)
    {
        WeatherManager manager = map.weatherManager;
        if (manager.curWeather != LockedWeather)
        {
            manager.TransitionTo(LockedWeather);
        }

        manager.lastWeather = LockedWeather;
        manager.curWeatherAge = WeatherTransitionTicks;
    }

    private static int? TicksUntil(Map map, RimRunnerSceneTime time)
    {
        return time switch
        {
            RimRunnerSceneTime.Morning => TicksUntilHour(map, MorningHour),
            RimRunnerSceneTime.Midnight => TicksUntilHour(map, MidnightHour),
            RimRunnerSceneTime.Dusk => TicksUntilDusk(map),
            _ => throw new InvalidOperationException("场面时段出现未处理的取值：" + time),
        };
    }

    private static int TicksUntilHour(Map map, int hour)
    {
        return TicksUntilHour(GenLocalDate.HourFloat(map), hour);
    }

    internal static int TicksUntilHour(float hourNow, int hour)
    {
        float past = Mathf.Repeat(hourNow - hour, GenDate.HoursPerDay);
        if (past < ArrivedWindowHours)
        {
            return 0;
        }

        return Mathf.RoundToInt((GenDate.HoursPerDay - past) * GenDate.TicksPerHour);
    }

    private static int? TicksUntilDusk(Map map)
    {
        int now = Find.TickManager.TicksAbs;
        scanning = true;
        try
        {
            float current = GenCelestial.CelestialSunGlow(map, now);
            if (current > NightMaxGlow && current <= DuskGlow
                && GenCelestial.CelestialSunGlow(map, now + DuskScanStepTicks) < current)
            {
                return 0;
            }

            float previous = current;
            for (int offset = DuskScanStepTicks; offset <= GenDate.TicksPerDay; offset += DuskScanStepTicks)
            {
                float glow = GenCelestial.CelestialSunGlow(map, now + offset);
                if (previous > DuskGlow && glow <= DuskGlow)
                {
                    return offset;
                }

                previous = glow;
            }

            return null;
        }
        finally
        {
            scanning = false;
        }
    }
}

[HarmonyPatch(typeof(GenCelestial), nameof(GenCelestial.CelestialSunGlow), typeof(PlanetTile), typeof(int))]
internal static class Patch_GenCelestial_CelestialSunGlow_RimRunnerShowcase
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Stages;
    }

    private static bool Prefix(ref int ticksAbs)
    {
        return RimRunnerSceneEnvironment.OverrideSunTicks(ref ticksAbs);
    }
}

[HarmonyPatch(typeof(GenLocalDate), nameof(GenLocalDate.DayPercent), typeof(PlanetTile))]
internal static class Patch_GenLocalDate_DayPercent_RimRunnerShowcase
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Stages;
    }

    private static void Postfix(PlanetTile tile, ref float __result)
    {
        RimRunnerSceneEnvironment.OverrideDayPercent(tile, ref __result);
    }
}

[HarmonyPatch(typeof(WeatherDecider), nameof(WeatherDecider.WeatherDeciderTick))]
internal static class Patch_WeatherDecider_WeatherDeciderTick_RimRunnerShowcase
{
    private static bool Prepare()
    {
        return RimRunnerProcessMode.Stages;
    }

    private static bool Prefix()
    {
        return RimRunnerSceneEnvironment.LockedWeather == null;
    }
}
