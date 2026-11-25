namespace RimRunner;

/// <summary>相机视野锁：锁定视野矩形以确定性构造「离屏」或「在视野里」态。</summary>
/// <remarks>锁从上锁持续到 Restore、本 phase 体结束或场面拆场，跨帧仍有效。只在目标图是当前地图时起作用，引擎截图前已解除。本段里从未起作用的锁出 CAMERA-LOCK-INERT 警告。</remarks>
public sealed class RimRunnerCameraFixture
{
    private const int EdgeInset = 4;
    private const int LockedHalfSize = 2;

    private readonly Map map;

    private bool applied;

    private static readonly List<RimRunnerCameraFixture> createdThisPhase = [];

    private RimRunnerCameraFixture(Map map, CellRect lockedRect)
    {
        this.map = map;
        LockedRect = lockedRect;
    }

    internal static RimRunnerCameraFixture? Active { get; private set; }

    /// <summary>锁定的视野矩形。</summary>
    public CellRect LockedRect { get; }

    /// <summary>锁离屏：把视野锁到离 focus 最远的地图角落，返回已上锁的锁。</summary>
    /// <remarks>目标是独立小图时不抛，锁只在它借成当前地图时起作用；整个 phase 都没借成当前地图时出 CAMERA-LOCK-INERT。</remarks>
    /// <exception cref="InvalidOperationException">目标图不是当前地图、也不是独立小图时抛。</exception>
    public static RimRunnerCameraFixture LockAwayFrom(Map map, IntVec3 focus)
    {
        RequireLockable(map);
        return Locked(map, CellRect.CenteredOn(FarCorner(map, focus), LockedHalfSize));
    }

    /// <summary>锁入视野：把视野锁成以 focus 为中心的 5×5 矩形，返回已上锁的锁。</summary>
    /// <remarks>目标是独立小图时不抛，锁只在该图借成当前地图时起作用。整个 phase 都未借成当前地图时出 CAMERA-LOCK-INERT。按相机判档的物在视野内的档随相机视距，要钉档用 ctx.PinTickRate。</remarks>
    /// <exception cref="InvalidOperationException">目标图不是当前地图、也不是独立小图时抛。</exception>
    /// <exception cref="ArgumentOutOfRangeException">focus 不在目标图内时抛。</exception>
    public static RimRunnerCameraFixture LockOn(Map map, IntVec3 focus)
    {
        RequireLockable(map);
        if (!focus.InBounds(map))
        {
            throw new ArgumentOutOfRangeException(nameof(focus), focus,
                RimRunnerLog.Prefix + "[CameraFixture] LockOn 的 focus 不在目标图内：图尺寸 " + map.Size);
        }

        return Locked(map, CellRect.CenteredOn(focus, LockedHalfSize));
    }

    private static void RequireLockable(Map map)
    {
        if (map == null)
        {
            throw new ArgumentNullException(nameof(map));
        }

        if (RimRunnerCurrentMapProbe.EngineCurrentMap() != map && !RimRunnerIsolatedMap.IsPoolMap(map))
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + "[CameraFixture] 测试图不是当前地图，视野锁不会生效："
                                    + "CurrentMap=" + RimRunnerCurrentMapProbe.EngineCurrentMap() + " 目标图=" + map);
        }
    }

    private static RimRunnerCameraFixture Locked(Map map, CellRect rect)
    {
        RimRunnerCameraFixture fixture = new(map, rect);
        fixture.Lock();
        createdThisPhase.Add(fixture);
        return fixture;
    }

    /// <summary>重锁：Restore 之后再让视野回到 LockedRect；替换别的活动锁。</summary>
    /// <remarks>目标图当前就是当前地图时，上锁即算起效，不出 CAMERA-LOCK-INERT。目标图还不是当前地图时，上锁要到该图成为当前地图并被读到才起效。</remarks>
    public void Lock()
    {
        Active = this;
        applied = RimRunnerCurrentMapProbe.EngineCurrentMap() == map;
    }

    /// <summary>还原：解除这把锁，视野回到相机的真实位置。</summary>
    public void Restore()
    {
        if (Active == this) Active = null;
    }

    internal static void EndPhase()
    {
        if (RimRunnerContext.AmbientPhase == RimRunnerPhase.Case)
        {
            foreach (RimRunnerCameraFixture fixture in createdThisPhase)
            {
                if (!fixture.applied)
                {
                    RimRunnerWarnings.Warn(RimRunnerWarningKey.CameraLockInert,
                        "视野锁创建于 " + fixture.map + "，本段内该图从未成为当前地图，锁没有生效");
                }
            }
        }

        createdThisPhase.Clear();
        Active = null;
    }

    internal static bool TryGetLockedRect(out CellRect rect)
    {
        RimRunnerCameraFixture? active = Active;
        if (active != null && RimRunnerCurrentMapProbe.EngineCurrentMap() == active.map)
        {
            active.applied = true;
            rect = active.LockedRect;
            return true;
        }

        rect = default;
        return false;
    }

    private static IntVec3 FarCorner(Map map, IntVec3 focus)
    {
        int x = focus.x * 2 >= map.Size.x ? EdgeInset : map.Size.x - 1 - EdgeInset;
        int z = focus.z * 2 >= map.Size.z ? EdgeInset : map.Size.z - 1 - EdgeInset;
        return new IntVec3(x, 0, z);
    }
}

[HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect), MethodType.Getter)]
internal static class Patch_CameraDriver_CurrentViewRect_RimRunnerCameraFixture
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static bool Prefix(ref CellRect __result)
    {
        if (!RimRunnerCameraFixture.TryGetLockedRect(out CellRect rect)) return true;
        __result = rect;
        return false;
    }
}
