using System.Runtime.CompilerServices;

namespace RimRunner;

internal static class RimRunnerSceneSeed
{
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "set_StateCompressed")]
    private static extern void SetRandState(
        [UnsafeAccessorType("Verse.Rand, Assembly-CSharp")] object? rand, ulong state);

    internal static void Pin(int seed)
    {
        SetRandState(null, (uint)seed);
    }

    internal static int StableHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash = (hash ^ c) * 16777619;
            }

            return (int)hash;
        }
    }
}
