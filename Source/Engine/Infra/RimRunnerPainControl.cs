namespace RimRunner;

/// <summary>痛觉压制：为 host 保留真实痛觉，同图其余 pawn 的 PainTotal 压成 0。</summary>
public static class RimRunnerPainControl
{
    private static readonly Dictionary<Map, Suppression> HostsByMap = [];

    /// <summary>痛觉压制作用域：作用域内压制 host 以外的痛觉。</summary>
    /// <remarks>按 pawn 此刻所在图判，含后到这张图的；每张图一个 host，后开的顶替先开的、退出时恢复。Remove 之后退出不写回；用例末（多阶段为 phase 末）仍没退出的由引擎撤下并记 FIXTURE-RESIDUE。</remarks>
    public static IDisposable SuppressOthers(Pawn host)
    {
        if (host?.Map == null) throw new ArgumentException("测试痛觉 host 须在图上", nameof(host));
        Map map = host.Map;
        HostsByMap.TryGetValue(map, out Suppression? previous);
        Suppression layer = new(host, previous);
        HostsByMap[map] = layer;
        return new SuppressionScope(map, layer);
    }

    /// <summary>痛觉压制撤销：撤下 host 所在的压制；host 不是任何图当前的 host 时无操作。</summary>
    public static void Remove(Pawn? host)
    {
        if (host == null) return;
        foreach (Map map in HostsByMap.Where(pair => pair.Value.Host == host).Select(pair => pair.Key).ToList())
        {
            HostsByMap.Remove(map);
        }
    }

    /// <summary>该 pawn 的 PainTotal 此刻是否被压成 0：在已设 host 的图上且不是 host。</summary>
    public static bool ShouldSuppress(Pawn? pawn)
    {
        return pawn?.Map != null && HostsByMap.TryGetValue(pawn.Map, out Suppression? layer)
                                 && pawn != layer.Host;
    }

    internal static object Snapshot() => new Dictionary<Map, Suppression>(HostsByMap);

    internal static string? RestoreSnapshot(object snapshot)
    {
        var before = (Dictionary<Map, Suppression>)snapshot;
        List<string> leaked = HostsByMap
            .Where(pair => !before.TryGetValue(pair.Key, out Suppression? was) || was != pair.Value)
            .Select(pair => pair.Key + " 上 host " + RimRunnerFixtureState.Describe(pair.Value.Host))
            .ToList();
        if (leaked.Count == 0)
        {
            return null;
        }

        HostsByMap.Clear();
        foreach (KeyValuePair<Map, Suppression> pair in before)
        {
            HostsByMap[pair.Key] = pair.Value;
        }

        return string.Join("、", leaked);
    }

    private sealed class Suppression(Pawn host, Suppression? previous)
    {
        internal Pawn Host { get; } = host;
        internal Suppression? Previous { get; } = previous;
        internal bool Closed;
    }

    private sealed class SuppressionScope(Map map, Suppression layer) : IDisposable
    {
        public void Dispose()
        {
            if (layer.Closed) return;
            layer.Closed = true;
            if (!HostsByMap.TryGetValue(map, out Suppression? current) || current != layer) return;
            Suppression? restore = layer.Previous;
            while (restore is { Closed: true })
            {
                restore = restore.Previous;
            }

            if (restore != null)
            {
                HostsByMap[map] = restore;
            }
            else
            {
                HostsByMap.Remove(map);
            }
        }
    }
}

[HarmonyPatch(typeof(HediffSet), nameof(HediffSet.PainTotal), MethodType.Getter)]
internal static class Patch_HediffSet_PainTotal_RimRunnerSuppressOthers
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(HediffSet __instance, ref float __result)
    {
        if (RimRunnerPainControl.ShouldSuppress(__instance?.pawn)) __result = 0f;
    }
}
