namespace RimRunner;

internal static class RimRunnerDrawnPosition
{
    private static Func<Map, IntVec3, Vector3?>? substitute;

    internal static Vector3 CellCenter(Map map, IntVec3 cell) =>
        Locate(map, cell) ?? cell.ToVector3Shifted();

    internal static Vector3 ThingCenter(Thing thing)
    {
        Vector3 logical = thing.DrawPos;
        if (thing.Map is not Map map || Locate(map, thing.Position) is not Vector3 drawn)
        {
            return logical;
        }

        Vector3 shift = drawn - thing.Position.ToVector3Shifted();
        return new Vector3(logical.x + shift.x, logical.y, logical.z + shift.z);
    }

    internal static Rect GroundBounds(Map map, CellRect rect)
    {
        float minX = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;
        foreach (IntVec3 corner in (IntVec3[])
                 [new(rect.minX, 0, rect.minZ), new(rect.maxX, 0, rect.minZ), new(rect.minX, 0, rect.maxZ), new(rect.maxX, 0, rect.maxZ)])
        {
            Vector3 center = CellCenter(map, corner);
            minX = Mathf.Min(minX, center.x - 0.5f);
            minZ = Mathf.Min(minZ, center.z - 0.5f);
            maxX = Mathf.Max(maxX, center.x + 0.5f);
            maxZ = Mathf.Max(maxZ, center.z + 0.5f);
        }

        return Rect.MinMaxRect(minX, minZ, maxX, maxZ);
    }

    internal static IDisposable SubstituteForSelfTest(Func<Map, IntVec3, Vector3?> locate)
    {
        if (substitute != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 画出位置的自测替身已在用");
        }

        substitute = locate;
        return new Restore();
    }

    private static Vector3? Locate(Map map, IntVec3 cell) =>
        substitute != null ? substitute(map, cell) : RimRunnerProduct.TryGetCapabilities()?.LocateDrawnCell(map, cell);

    private sealed class Restore : IDisposable
    {
        public void Dispose() => substitute = null;
    }
}
