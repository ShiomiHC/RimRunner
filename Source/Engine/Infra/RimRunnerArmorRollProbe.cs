namespace RimRunner;

public static class RimRunnerArmorRollProbe
{
    public readonly struct Entry(uint iteration, bool apparelLayer)
    {
        public readonly uint Iteration = iteration;
        public readonly bool ApparelLayer = apparelLayer;
    }

    private static readonly FieldInfo IterationsField =
        typeof(Rand).GetField("iterations", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(RimRunnerLog.Prefix + " Verse.Rand 没有 private static 字段 iterations，探针无法读抽数");

    private static List<Entry>? entries;

    public static uint Iterations => (uint)IterationsField.GetValue(null)!;

    public static IReadOnlyList<Entry>? Entries => entries;

    public static IDisposable Arm()
    {
        if (entries != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 护甲 roll 探针已启用，不得嵌套");
        }

        entries = [];
        return new Scope();
    }

    internal static void Record(Thing armorThing)
    {
        entries?.Add(new Entry(Iterations, armorThing != null));
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
            entries = null;
        }
    }
}

[HarmonyPatch(typeof(ArmorUtility), "ApplyArmor")]
internal static class Patch_ArmorUtility_ApplyArmor_RimRunnerRollProbe
{
    private static void Prefix(Thing armorThing)
    {
        RimRunnerArmorRollProbe.Record(armorThing);
    }
}
