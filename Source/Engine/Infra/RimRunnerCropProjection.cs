namespace RimRunner;

internal static class RimRunnerCropProjection
{
    internal readonly record struct ScreenBox(float XMin, float XMax, float YMin, float YMax)
    {
        internal float Width => XMax - XMin;
        internal float Height => YMax - YMin;
        internal float CenterX => (XMin + XMax) / 2f;
        internal float CenterY => (YMin + YMax) / 2f;
    }

    internal readonly record struct Framing(Vector3 RootPos, float RootSize);

    internal static ScreenBox Project(
        Func<Vector3, Vector3> toScreen, float minX, float minZ, float maxX, float maxZ, float headroomCells)
    {
        float x0 = float.PositiveInfinity;
        float x1 = float.NegativeInfinity;
        float y0 = float.PositiveInfinity;
        float y1 = float.NegativeInfinity;
        Vector3[] corners = [new(minX, 0f, minZ), new(maxX, 0f, minZ), new(minX, 0f, maxZ), new(maxX, 0f, maxZ)];
        foreach (Vector3 corner in corners)
        {
            Vector3 screen = toScreen(corner);
            x0 = Mathf.Min(x0, screen.x);
            x1 = Mathf.Max(x1, screen.x);
            y0 = Mathf.Min(y0, screen.y);
            y1 = Mathf.Max(y1, screen.y);
        }

        if (headroomCells > 0f)
        {
            Vector3 center = new((minX + maxX) / 2f, 0f, (minZ + maxZ) / 2f);
            y1 += headroomCells * PixelsPerCell(toScreen, center);
        }

        return new ScreenBox(x0, x1, y0, y1);
    }

    internal static float PixelsPerCell(Func<Vector3, Vector3> toScreen, Vector3 at)
    {
        (Vector2 dx, Vector2 dz) = GroundAxes(toScreen, at);
        float sum = dx.sqrMagnitude + dz.sqrMagnitude;
        float det = dx.x * dz.y - dz.x * dx.y;
        return Mathf.Sqrt((sum + Mathf.Sqrt(Mathf.Max(sum * sum - 4f * det * det, 0f))) / 2f);
    }

    internal static Framing Refine(
        Func<Vector3, Vector3> toScreen, ScreenBox box, Vector3 rootPos, float rootSize, int screenWidth, int screenHeight)
    {
        float scale = Mathf.Max(box.Width / screenWidth, box.Height / screenHeight);
        float offsetX = box.CenterX - screenWidth / 2f;
        float offsetY = box.CenterY - screenHeight / 2f;
        (Vector2 dx, Vector2 dz) = GroundAxes(toScreen, new Vector3(rootPos.x, 0f, rootPos.z));
        float det = dx.x * dz.y - dz.x * dx.y;
        Vector3 moved = rootPos;
        if (Mathf.Abs(det) > 1e-3f)
        {
            moved.x += (offsetX * dz.y - dz.x * offsetY) / det;
            moved.z += (dx.x * offsetY - offsetX * dx.y) / det;
        }

        return new Framing(moved, rootSize * scale);
    }

    private static (Vector2 Dx, Vector2 Dz) GroundAxes(Func<Vector3, Vector3> toScreen, Vector3 at)
    {
        Vector3 origin = toScreen(at);
        Vector3 alongX = toScreen(at + new Vector3(1f, 0f, 0f));
        Vector3 alongZ = toScreen(at + new Vector3(0f, 0f, 1f));
        return (new Vector2(alongX.x - origin.x, alongX.y - origin.y),
            new Vector2(alongZ.x - origin.x, alongZ.y - origin.y));
    }
}
