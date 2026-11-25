using RimWorld.Planet;

namespace RimRunner;

internal sealed class RimRunnerDriveCamera : RimRunnerDriveVerb
{
    internal override RimRunnerDriveTable.Verb Spec => RimRunnerDriveTable.Camera;

    internal override string? Refusal(RimRunnerDriveOp op)
    {
        if (op.Arg("tile") is { } tile)
        {
            if (!WorldRendererUtility.WorldSelected)
            {
                return "世界视图未打开";
            }

            int number = RimRunnerDriveTable.ParseTile(tile)!.Value;
            PlanetLayer layer = PlanetLayer.Selected;
            return number < layer.TilesCount ? null : "地块 " + number + " 不在当前层 " + layer.Def.defName + " 内（共 " + layer.TilesCount + " 块）";
        }

        Map? map = RimRunnerCurrentMapProbe.EngineCurrentMap();
        if (map == null)
        {
            return "没有当前地图";
        }

        if (WorldRendererUtility.WorldSelected)
        {
            return "世界视图已打开，相机不在地图上";
        }

        if (op.Arg("pawn") != null)
        {
            return RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out _);
        }

        CellRect target = Target(op);
        return target.InBounds(map) ? null : "目标 " + Describe(target) + " 不整片在当前地图内（图尺寸 " + map.Size.x + "×" + map.Size.z + "）";
    }

    internal override IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op)
    {
        if (op.Arg("tile") is { } id)
        {
            PlanetTile tile = new(RimRunnerDriveTable.ParseTile(id)!.Value, PlanetLayer.Selected);
            Find.WorldCameraDriver.JumpTo(tile);
            yield return RimRunnerWait.Frames(2);
            string? away = RimRunnerLivePointer.LocateTile(id, out _, out Vector2 screen, out string label);
            op.Line("世界相机对准 " + label + "，高度 " + Number(Find.WorldCameraDriver.TrueAltitude));
            op.Line(away ?? label + " 在画面内（" + RimRunnerLivePointer.DescribeScreen(screen) + "）");
            yield break;
        }

        Map map = RimRunnerCurrentMapProbe.EngineCurrentMap()!;
        Pawn? pawn = null;
        if (op.Arg("pawn") != null)
        {
            RimRunnerDrivePawns.Resolve(op.Arg("pawn"), out List<Pawn> pawns);
            pawn = pawns[0];
        }

        CellRect target = pawn != null ? CellRect.SingleCell(pawn.Position) : Target(op);
        float? zoom = op.Arg("zoom") is { } text ? RimRunnerDriveTable.ParsePositive(text) : null;
        bool cell = pawn != null || RimRunnerDriveTable.ParseInts(op.Arg("target")!, 2, 4)!.Length == 2;
        RimRunnerOp<RimRunnerCameraShot> shot = cell
            ? RimRunnerCamera.Frame(map, target.Min, zoom)
            : RimRunnerCamera.Frame(map, target, zoom);
        try
        {
            yield return shot;
        }
        finally
        {
            RimRunnerCamera.Forget();
        }

        RimRunnerCameraShot result = shot.Result;
        op.Line("相机 " + Pose(result.Before) + " → " + Pose(result.After));
        op.Line("目标 " + (pawn != null ? RimRunnerDrivePawns.Describe(pawn) + " 所在格 " : "") + Describe(result.Target) + (result.TargetVisible ? " 整片在画面内" : " 有部分在画面外"));
        op.Line("框住目标加四周 1 格需视距 " + Number(result.SizeNeeded)
                + (result.SizeNeeded > result.SizeMax ? "，超出最远视距 " : "，最远视距 ") + Number(result.SizeMax));
        op.Line("视野 " + Describe(result.ViewRect));
        op.Line("当前地图在" + RimRunnerLivePointer.DescribeTile(map.Tile));
    }

    private static CellRect Target(RimRunnerDriveOp op)
    {
        int[] v = RimRunnerDriveTable.ParseInts(op.Arg("target")!, 2, 4)!;
        return v.Length == 2
            ? CellRect.SingleCell(new IntVec3(v[0], 0, v[1]))
            : CellRect.FromLimits(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }

    internal static string Describe(CellRect rect) =>
        rect.Area == 1 ? "(" + rect.minX + "," + rect.minZ + ")" : "(" + rect.minX + "," + rect.minZ + ")–(" + rect.maxX + "," + rect.maxZ + ")";

    internal static string Number(float value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string Pose(RimRunnerCameraPose pose) =>
        "(" + Number(pose.X) + ", " + Number(pose.Z) + ") 视距 " + Number(pose.Size);
}
