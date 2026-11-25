using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace RimRunner;

internal static class RimRunnerCurrentMapReroute
{
    private static readonly List<(string Origin, string Reader)> sites = [];
    private static readonly Dictionary<MethodBase, int> siteOf = [];
    private static readonly HashSet<MethodBase> tried = [];
    private static Harmony? harmony;

    private static readonly Dictionary<MethodInfo, MethodInfo> shimFor = new()
    {
        [AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap))] = AccessTools.Method(typeof(RimRunnerCurrentMapReroute), nameof(FindCurrentMap)),
        [AccessTools.PropertyGetter(typeof(Game), nameof(Game.CurrentMap))] = AccessTools.Method(typeof(RimRunnerCurrentMapReroute), nameof(GameCurrentMap)),
        [AccessTools.PropertyGetter(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect))] = AccessTools.Method(typeof(RimRunnerCurrentMapReroute), nameof(CameraViewRect)),
    };

    private static readonly HashSet<MethodBase> shims = [.. shimFor.Values];

    internal static bool IsShim(MethodBase method) => method.DeclaringType == typeof(RimRunnerCurrentMapReroute) && shims.Contains(method);

    internal static bool Tried(MethodBase reader) => tried.Contains(reader);

    internal static (string Origin, string Reader) Site(int site) => sites[site - 1];

    internal static void Consider(MethodBase reader, string origin, HashSet<string> acked, int armedThread)
    {
        if (!acked.Contains(origin) || origin == RimRunnerOrigin.Vanilla || origin == RimRunnerOrigin.Case
            || Environment.CurrentManagedThreadId != armedThread || !tried.Add(reader) || reader.ContainsGenericParameters)
        {
            return;
        }

        sites.Add((origin, reader.DeclaringType!.Name + "." + reader.Name));
        siteOf[reader] = sites.Count;
        try
        {
            harmony ??= new Harmony("rimrunner.currentmap.reroute");
            harmony.Patch(reader, transpiler: new HarmonyMethod(typeof(RimRunnerCurrentMapReroute), nameof(Transpiler)));
        }
        catch (Exception exception)
        {
            RimRunnerEngineLog.Warn("当前地图探针改道失败，" + reader.DeclaringType!.FullName + "." + reader.Name
                                    + " 的读取仍经截栈：" + exception.GetType().Name + " " + exception.Message);
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        int site = siteOf[original];
        foreach (CodeInstruction instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && instruction.operand is MethodInfo target && shimFor.TryGetValue(target, out MethodInfo? shim))
            {
                CodeInstruction load = new(OpCodes.Ldc_I4, site);
                instruction.MoveLabelsTo(load);
                instruction.MoveBlocksTo(load);
                yield return load;
                yield return new CodeInstruction(OpCodes.Call, shim);
                continue;
            }

            yield return instruction;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Map? FindCurrentMap(int site)
    {
        int previous = RimRunnerCurrentMapProbe.rerouteSite;
        RimRunnerCurrentMapProbe.rerouteSite = site;
        try
        {
            return Find.CurrentMap;
        }
        finally
        {
            RimRunnerCurrentMapProbe.rerouteSite = previous;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Map? GameCurrentMap(Game game, int site)
    {
        int previous = RimRunnerCurrentMapProbe.rerouteSite;
        RimRunnerCurrentMapProbe.rerouteSite = site;
        try
        {
            return game.CurrentMap;
        }
        finally
        {
            RimRunnerCurrentMapProbe.rerouteSite = previous;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static CellRect CameraViewRect(CameraDriver driver, int site)
    {
        int previous = RimRunnerCurrentMapProbe.rerouteSite;
        RimRunnerCurrentMapProbe.rerouteSite = site;
        try
        {
            return driver.CurrentViewRect;
        }
        finally
        {
            RimRunnerCurrentMapProbe.rerouteSite = previous;
        }
    }
}
