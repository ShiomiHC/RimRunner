using System.Runtime.CompilerServices;

namespace RimRunner;

internal static class RimRunnerZoneColorPin
{
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "nextStorageZoneColorIndex")]
    private static extern ref int StorageIndex(
        [UnsafeAccessorType("Verse.ZoneColorUtility, Assembly-CSharp")] object? utility);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "nextGrowingZoneColorIndex")]
    private static extern ref int GrowingIndex(
        [UnsafeAccessorType("Verse.ZoneColorUtility, Assembly-CSharp")] object? utility);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "nextFishingZoneColorIndex")]
    private static extern ref int FishingIndex(
        [UnsafeAccessorType("Verse.ZoneColorUtility, Assembly-CSharp")] object? utility);

    internal static void Reset()
    {
        StorageIndex(null) = 0;
        GrowingIndex(null) = 0;
        FishingIndex(null) = 0;
    }
}
