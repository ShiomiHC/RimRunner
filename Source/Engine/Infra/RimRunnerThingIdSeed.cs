using System.Reflection.Emit;

namespace RimRunner;

internal static class RimRunnerThingIdSeed
{
    [ThreadStatic] private static Frame? top;

    internal static IDisposable Enter(int seed)
    {
        top = new Frame(seed, top);
        return top;
    }

    internal static int SeedOf(Thing thing)
    {
        Frame? frame = top;
        if (frame == null)
        {
            return thing.thingIDNumber;
        }

        if (!frame.Assigned.TryGetValue(thing.thingIDNumber, out int seed))
        {
            seed = Gen.HashCombineInt(frame.Seed, frame.Assigned.Count);
            frame.Assigned.Add(thing.thingIDNumber, seed);
        }

        return seed;
    }

    internal static IEnumerable<CodeInstruction> RewriteSingleRead(IEnumerable<CodeInstruction> instructions, string site)
    {
        FieldInfo idField = AccessTools.Field(typeof(Thing), nameof(Thing.thingIDNumber));
        MethodInfo replacement = AccessTools.Method(typeof(RimRunnerThingIdSeed), nameof(SeedOf));
        List<CodeInstruction> codes = instructions.ToList();
        List<int> hits = Enumerable.Range(0, codes.Count).Where(index => codes[index].LoadsField(idField)).ToList();
        if (hits.Count != 1)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " [engine] " + site + " 里 thingIDNumber 的读点有 "
                                                + hits.Count + " 处（预期 1），号种子补丁无法挂上");
        }

        codes[hits[0]].opcode = OpCodes.Call;
        codes[hits[0]].operand = replacement;
        return codes;
    }

    private sealed class Frame : IDisposable
    {
        internal readonly int Seed;
        internal readonly Dictionary<int, int> Assigned = [];
        private readonly Frame? outer;
        private bool disposed;

        internal Frame(int seed, Frame? outer)
        {
            Seed = seed;
            this.outer = outer;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            top = outer;
        }
    }
}

[HarmonyPatch(typeof(PawnWeaponGenerator), nameof(PawnWeaponGenerator.TryGenerateWeaponFor))]
internal static class Patch_PawnWeaponGenerator_TryGenerateWeaponFor_RimRunnerThingIdSeed
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        RimRunnerThingIdSeed.RewriteSingleRead(instructions, "PawnWeaponGenerator.TryGenerateWeaponFor");
}

[HarmonyPatch(typeof(Pawn_StoryTracker), nameof(Pawn_StoryTracker.TryGetRandomHeadFromSet))]
internal static class Patch_Pawn_StoryTracker_TryGetRandomHeadFromSet_RimRunnerThingIdSeed
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        RimRunnerThingIdSeed.RewriteSingleRead(instructions, "Pawn_StoryTracker.TryGetRandomHeadFromSet");
}
