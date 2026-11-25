using RimWorld.Planet;

namespace RimRunner;

internal sealed class RimRunnerDriveSweep : RimRunnerDriveVerb
{
    private const int DefaultDwellFrames = 1;

    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Sweep;

    internal override string? Refusal(RimRunnerDriveOp op)
    {
        Map? map = RimRunnerCurrentMapProbe.EngineCurrentMap();
        if (map == null)
        {
            return "没有当前地图";
        }

        if (WorldRendererUtility.WorldSelected)
        {
            return "世界视图已打开，相机不在地图上";
        }

        CellRect area = Area(op, map);
        return area.InBounds(map)
            ? null
            : "矩形 (" + area.minX + "," + area.minZ + ")–(" + area.maxX + "," + area.maxZ + ") 不整片在当前地图内（图尺寸 "
              + map.Size.x + "×" + map.Size.z + "）";
    }

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        Map map = RimRunnerCurrentMapProbe.EngineCurrentMap()!;
        float? zoom = op.Arg("zoom") is { } text ? RimRunnerDriveTable.ParsePositive(text) : null;
        int dwell = op.Arg("dwell-frames") is { } frames ? RimRunnerDriveTable.ParsePositiveInt(frames)!.Value : DefaultDwellFrames;
        RimRunnerOp<RimRunnerSweepResult> sweep = RimRunnerCamera.Sweep(map, Area(op, map), zoom, dwell);
        try
        {
            yield return sweep;
        }
        finally
        {
            RimRunnerCamera.Forget();
        }

        RimRunnerSweepResult result = sweep.Result;
        op.Line("扫描 " + RimRunnerDriveCamera.Describe(result.Area) + "：" + result.Tiles + " 块 × 每块 " + dwell + " 帧，视距 "
                + RimRunnerDriveCamera.Number(result.Size) + "，共 " + result.Frames + " 帧");
    }

    private static CellRect Area(RimRunnerDriveOp op, Map map)
    {
        if (op.Arg("area") is not { } text)
        {
            return CellRect.WholeMap(map);
        }

        int[] v = RimRunnerDriveTable.ParseInts(text, 4)!;
        return CellRect.FromLimits(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }
}
