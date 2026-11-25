using System.Reflection.Emit;

namespace RimRunner;

internal static class RimRunnerCurrentMap
{
    private sealed class Borrow : IDisposable
    {
        private readonly Map? previous;
        private bool disposed;

        internal Borrow(Map? previous)
        {
            this.previous = previous;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            active = null;
            if (previous != null && Find.Maps.Contains(previous) && RimRunnerCurrentMapProbe.EngineCurrentMap() != previous)
            {
                Current.Game.CurrentMap = previous;
            }
        }
    }

    private static Borrow? active;

    internal static bool Borrowing => active != null;

    internal static IDisposable Lend(Map map)
    {
        if (map == null)
        {
            throw new ArgumentNullException(nameof(map));
        }

        if (!Find.Maps.Contains(map))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " AsCurrentMap 的图不在本局：" + map);
        }

        if (active != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " AsCurrentMap 不得嵌套：上一个作用域尚未结束");
        }

        Map? previous = RimRunnerCurrentMapProbe.EngineCurrentMap();
        active = new Borrow(previous);
        if (previous != map)
        {
            Current.Game.CurrentMap = map;
        }

        return active;
    }

    internal static void ThrowIfBorrowing(string entry)
    {
        if (active != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " AsCurrentMap 作用域内禁止推 tick（" + entry
                + "）：借来的当前地图按相机逐 tick 交付，只供一次 vanilla 调用使用");
        }
    }

    internal static string? EndCase()
    {
        if (active == null)
        {
            return null;
        }

        active.Dispose();
        return "AsCurrentMap 作用域到用例收尾仍未结束，已切回借用前的当前地图";
    }

    internal static Map? CurrentMapSeenBySelection(Map target)
    {
        Map? current = RimRunnerCurrentMapProbe.EngineCurrentMap();
        return target != null && target != current && RimRunnerIsolatedMap.HoldsCurrentMap(target) ? target : current;
    }
}

[HarmonyPatch(typeof(Selector), "SelectInternal")]
internal static class Patch_Selector_SelectInternal_RimRunnerCurrentMap
{
    private static readonly MethodInfo CurrentMapGetter = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap));

    private static readonly MethodInfo Replacement =
        AccessTools.Method(typeof(RimRunnerCurrentMap), nameof(RimRunnerCurrentMap.CurrentMapSeenBySelection));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions.ToList();
        List<int> hits = Enumerable.Range(0, codes.Count).Where(index => codes[index].Calls(CurrentMapGetter)).ToList();
        if (hits.Count != 1)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " [engine] Selector.SelectInternal 里 Find.CurrentMap 的读点有 " + hits.Count
                + " 处（预期 1），阻止选中时切图的补丁无法挂上");
        }

        int at = hits[0];
        CodeInstruction dup = new(OpCodes.Dup);
        dup.labels.AddRange(codes[at].labels);
        dup.blocks.AddRange(codes[at].blocks);
        codes[at] = new CodeInstruction(OpCodes.Call, Replacement);
        codes.Insert(at, dup);
        return codes;
    }
}
