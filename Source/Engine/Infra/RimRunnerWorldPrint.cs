using RimWorld.Planet;

namespace RimRunner;

internal static class RimRunnerWorldPrint
{
    internal static Dictionary<string, string> CaptureForScene()
    {
        var print = new Dictionary<string, string>(StringComparer.Ordinal);
        CaptureResearchAndDiscovery(print);
        CaptureConditions(print, null);
        CaptureFactions(print);
        RimRunnerProduct.Capabilities.AppendWorldPrint(print);
        return print;
    }

    internal static Dictionary<string, string> CaptureForRun(IReadOnlyCollection<int>? mapIds = null)
    {
        mapIds ??= RunMapIds();
        var print = new Dictionary<string, string>(StringComparer.Ordinal);
        CaptureResearchAndDiscovery(print);
        CaptureConditions(print, mapIds);
        CaptureFactions(print);
        RimRunnerProduct.Capabilities.AppendWorldPrint(print);

        foreach (WorldObject worldObject in Find.WorldObjects.AllWorldObjects)
        {
            if (worldObject.def.defName == RimRunnerIsolatedMap.SettlementDefName)
            {
                continue;
            }

            print["世界物件 " + worldObject.GetUniqueLoadID()] = worldObject.def.defName + " "
                + (worldObject.Faction == null ? "无派系" : worldObject.Faction.def.defName);
        }

        return print;
    }

    internal static IReadOnlyCollection<int> RunMapIds()
    {
        return Find.Maps.Where(map => !RimRunnerIsolatedMap.IsPoolMap(map)).Select(map => map.uniqueID).ToList();
    }

    internal static List<string> ChangedKeys(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        return before.Keys.Union(after.Keys)
            .Where(key => !before.TryGetValue(key, out string? was) || !after.TryGetValue(key, out string? now) || was != now)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
    }

    internal static string Describe(IEnumerable<string> keys, Dictionary<string, string> before, Dictionary<string, string> after)
    {
        return string.Join("；", keys.Select(key => key + "：" + ValueOrNone(before, key) + " → " + ValueOrNone(after, key)));
    }

    private static void CaptureResearchAndDiscovery(Dictionary<string, string> print)
    {
        foreach (ResearchProjectDef project in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
        {
            float progress = Find.ResearchManager.GetProgress(project);
            if (progress > 0f)
            {
                print["研究 " + project.defName] = "进度 " + progress.ToString("0.###");
            }
        }

        foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
        {
            if (def.hiddenWhileUndiscovered && !Find.HiddenItemsManager.Hidden(def))
            {
                print["发现 " + def.defName] = "已发现";
            }
        }
    }

    private static void CaptureConditions(Dictionary<string, string> print, IReadOnlyCollection<int>? mapIds)
    {
        CountConditions(print, "世界条件 ", Find.World.gameConditionManager);
        foreach (Map map in Find.Maps)
        {
            if (mapIds == null || mapIds.Contains(map.uniqueID))
            {
                CountConditions(print, "地图 " + map.uniqueID + " 条件 ", map.gameConditionManager);
            }
        }
    }

    private static void CaptureFactions(Dictionary<string, string> print)
    {
        foreach (Faction faction in Find.FactionManager.AllFactionsListForReading
                     .Where(faction => !faction.IsPlayer && !faction.Hidden))
        {
            print["派系 " + faction.GetUniqueLoadID()] = faction.PlayerRelationKind + " " + faction.PlayerGoodwill;
        }
    }

    private static string ValueOrNone(Dictionary<string, string> print, string key)
    {
        return print.TryGetValue(key, out string? value) ? value : "无";
    }

    private static void CountConditions(Dictionary<string, string> print, string prefix, GameConditionManager manager)
    {
        foreach (IGrouping<GameConditionDef, GameCondition> group in manager.ActiveConditions.GroupBy(condition => condition.def))
        {
            print[prefix + group.Key.defName] = group.Count() + " 个";
        }
    }
}
