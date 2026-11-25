using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Verse.Sound;

namespace RimRunner;

internal static class RimRunnerSoundStatePin
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "recentlyPlayedResolvedGrains")]
    private static extern ref Queue<ResolvedGrain> RecentGrains(SubSoundDef def);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "lastPlayedResolvedGrain")]
    private static extern ref ResolvedGrain? LastGrain(SubSoundDef def);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "allowedPlayTimes")]
    private static extern ref Dictionary<string, float> AllowedPlayTimes(
        [UnsafeAccessorType("Verse.Sound.SoundSlotManager, Assembly-CSharp")] object? manager);

    internal static void Reset()
    {
        foreach (SoundDef def in DefDatabase<SoundDef>.AllDefsListForReading)
        {
            foreach (SubSoundDef sub in def.subSounds)
            {
                RecentGrains(sub).Clear();
                LastGrain(sub) = null;
            }
        }

        AllowedPlayTimes(null).Clear();
        RuntimeHelpers.RunClassConstructor(typeof(SoundParamSource_Perlin).TypeHandle);
        RimRunnerIdentityHashSeed.Reset();
    }
}

[HarmonyPatch]
internal static class RimRunnerIdentityHashSeed
{
    private static ConditionalWeakTable<object, object> ids = [];
    private static int next;

    private static bool Prepare() => RimRunnerProcessMode.Film;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SubEffecter_Sprayer), "MakeMote");
        yield return AccessTools.Method(typeof(CurvedScalerExt), nameof(CurvedScalerExt.ScaleAtTime));
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        MethodInfo hash = AccessTools.Method(typeof(object), nameof(GetHashCode));
        List<CodeInstruction> codes = instructions.ToList();
        List<int> hits = Enumerable.Range(0, codes.Count).Where(index => codes[index].Calls(hash)).ToList();
        if (hits.Count != 1)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " [engine] " + original.DeclaringType?.Name + "." + original.Name
                                                + " 里 GetHashCode 的调用有 " + hits.Count + " 处（预期 1），身份哈希种子补丁无法挂上");
        }

        codes[hits[0]].opcode = OpCodes.Call;
        codes[hits[0]].operand = AccessTools.Method(typeof(RimRunnerIdentityHashSeed), nameof(StableHash));
        return codes;
    }

    internal static int StableHash(object target)
    {
        if (!RimRunnerFilmClock.TryPinnedRealtime(out _))
        {
            return target.GetHashCode();
        }

        if (!ids.TryGetValue(target, out object? id))
        {
            id = 0x5bd1e995 ^ (++next * 16777619);
            ids.Add(target, id);
        }

        return (int)id;
    }

    internal static void Reset()
    {
        ids = [];
        next = 0;
    }
}
