using System.Collections;
using RimWorld.Planet;

namespace RimRunner;

internal static class RimRunnerMapLifecyclePatches
{
    private static readonly Dictionary<int, int> CreatedAt = [];

    private static readonly Dictionary<int, int> AddedAt = [];

    private static readonly HashSet<int> Registered = [];

    private static int engineTeardownDepth;

    private const string Source = nameof(RimRunnerMapLifecyclePatches);

    internal static void Reset()
    {
        CreatedAt.Clear();
        Registered.Clear();
    }

    internal static void Register(Map? map)
    {
        if (map != null) Registered.Add(map.uniqueID);
    }

    internal static void RunAsEngineTeardown(Action teardown)
    {
        engineTeardownDepth++;
        try
        {
            teardown();
        }
        finally
        {
            engineTeardownDepth--;
        }
    }

    private static bool RegisteredOrChildOfRegistered(Map map)
    {
        for (Map? current = map; current != null; current = (current.Parent as PocketMapParent)?.sourceMap)
        {
            if (Registered.Contains(current.uniqueID)) return true;
            if (current.Parent is not PocketMapParent) return false;
        }

        return false;
    }

    private static bool SceneDriven() =>
        RimRunnerContext.AmbientPhase == RimRunnerPhase.Scene || RimRunnerSceneHost.Chain is { Active: true };

    internal static bool GeneratedThisTick(Map map) =>
        map != null && AddedAt.TryGetValue(map.uniqueID, out int t) && t == Find.TickManager.TicksGame;

    internal static string? ComponentHoldOf(Map map)
    {
        List<GameComponent>? components = Current.Game?.components;
        if (components == null) return null;
        Assembly engine = typeof(RimRunnerMapLifecyclePatches).Assembly;
        Queue<(object Obj, string Path, int Depth)> queue = new();
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        foreach (object component in components.Concat(SelfTestHolders))
        {
            if (component != null && component.GetType().Assembly != engine && visited.Add(component))
            {
                queue.Enqueue((component, component.GetType().Name, 0));
            }
        }

        int expanded = 0;
        while (queue.Count > 0 && expanded++ < HoldScanMaxNodes)
        {
            (object obj, string path, int depth) = queue.Dequeue();
            if (depth >= HoldScanMaxDepth) continue;
            foreach ((object? child, string label) in HoldScanChildren(obj))
            {
                if (child == null || RimRunnerHolderScan.IsLeaf(child)) continue;
                if (ReferenceEquals(child, map) || (child is Thing { Destroyed: false } thing && thing.MapHeld == map))
                {
                    return path + label;
                }

                if (HoldScanPruned(child) || !visited.Add(child)) continue;
                queue.Enqueue((child, path + label, depth + 1));
            }
        }

        return null;
    }

    internal static readonly List<object> SelfTestHolders = [];

    private const int HoldScanMaxDepth = 6;
    private const int HoldScanMaxNodes = 20000;

    private static bool HoldScanPruned(object value) =>
        value is Map or Thing or World or WorldObject or Def or Faction or Game or Delegate or UnityEngine.Object;

    private static IEnumerable<(object? Child, string Label)> HoldScanChildren(object obj)
    {
        switch (obj)
        {
            case Array array:
                foreach (object? item in array) yield return (item, "[*]");
                yield break;
            case IDictionary dictionary when IsStandardCollection(obj):
                foreach (DictionaryEntry entry in dictionary)
                {
                    yield return (entry.Key, "{key}");
                    yield return (entry.Value, "{value}");
                }

                yield break;
            case IEnumerable sequence when IsStandardCollection(obj):
                foreach (object? item in sequence) yield return (item, "[*]");
                yield break;
        }

        Type type = obj.GetType();
        foreach (FieldInfo field in RimRunnerHolderScan.ReferenceFields(type))
        {
            object? value;
            try
            {
                value = field.GetValue(obj);
            }
            catch (Exception)
            {
                continue;
            }

            if (value == null) continue;
            if (field.FieldType.IsValueType && value is not IEnumerable)
            {
                foreach (FieldInfo inner in RimRunnerHolderScan.ReferenceFields(value.GetType()))
                {
                    yield return (inner.GetValue(value), "." + field.Name + "." + inner.Name);
                }

                continue;
            }

            yield return (value, "." + field.Name);
        }
    }

    private static bool IsStandardCollection(object obj) =>
        obj.GetType().Namespace is { } ns && ns.StartsWith("System.Collections", StringComparison.Ordinal);

    [HarmonyPatch(typeof(Game), nameof(Game.AddMap), typeof(Map))]
    internal static class Patch_Game_AddMap_RimRunnerSentinel
    {
        private static void Postfix(Map map)
        {
            if (map == null)
            {
                return;
            }

            AddedAt[map.uniqueID] = Find.TickManager.TicksGame;
            if (RimRunnerObservationBuffer.ActiveCaseId == null)
            {
                return;
            }

            CreatedAt[map.uniqueID] = Find.TickManager.TicksGame;
        }
    }

    [HarmonyPatch(typeof(MapDeiniter), nameof(MapDeiniter.Deinit), typeof(Map), typeof(bool))]
    internal static class Patch_MapDeiniter_Deinit_RimRunnerSentinel
    {
        private sealed class DeinitState
        {
            internal required Map Map;
            internal RimRunnerContext? Scene;
            internal string? CaseId;
            internal required HashSet<string> WorldPawnsBefore;
        }

        [HarmonyPriority(Priority.First)]
        private static void Prefix(Map map, out DeinitState? __state)
        {
            __state = null;
            if (map == null)
            {
                return;
            }

            string? caseId = RimRunnerObservationBuffer.ActiveCaseId;
            RimRunnerContext? scene = RimRunnerSceneHost.WarnTarget();
            bool unregistered = engineTeardownDepth == 0 && !RegisteredOrChildOfRegistered(map);

            if (scene != null && SceneDriven())
            {
                string sceneId = scene.Current!.id;
                if (GeneratedThisTick(map) && ComponentHoldOf(map) is { } hold)
                {
                    RimRunnerSceneHost.WarnScene(scene, RimRunnerWarningKey.ZeroTickMap,
                        RimRunnerSentinel.Compose("场面 " + sceneId + "：拆图时该图生成后一格 tick 都没推过，GameComponent 仍持有该图或图上的物——"
                        + "推 tick 才消费的队列项随图失效，错误级日志出在之后推 tick 时",
                            map + "，引用路径 " + hold),
                        Source);
                }

                if (unregistered)
                {
                    __state = CaptureDeinitState(map, scene, null);
                }
            }
            else if (scene == null && caseId != null)
            {
                if (CreatedAt.TryGetValue(map.uniqueID, out int t) && t == Find.TickManager.TicksGame
                    && ComponentHoldOf(map) is { } hold)
                {
                    RimRunnerSentinel.Warn(RimRunnerWarningKey.ZeroTickMap, caseId + "：拆图时该图生成后一格 tick 都没推过，GameComponent 仍持有该图或图上的物——"
                        + "拆图后再推 tick 的错误级日志记在当时正在执行的用例名下",
                        map + "，引用路径 " + hold);
                }

                if (unregistered && RimRunnerContext.AmbientPhase == RimRunnerPhase.Case)
                {
                    __state = CaptureDeinitState(map, null, caseId);
                }
            }

            CreatedAt.Remove(map.uniqueID);
            AddedAt.Remove(map.uniqueID);
        }

        private static DeinitState CaptureDeinitState(Map map, RimRunnerContext? scene, string? caseId)
        {
            HashSet<string> before = new(StringComparer.Ordinal);
            List<Pawn>? all = Find.WorldPawns?.AllPawnsAliveOrDead;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    string? thingId = all[i]?.ThingID;
                    if (thingId != null)
                    {
                        before.Add(thingId);
                    }
                }
            }

            return new DeinitState
            {
                Map = map,
                Scene = scene,
                CaseId = caseId,
                WorldPawnsBefore = before
            };
        }

        private static Exception? Finalizer(Exception? __exception, DeinitState? __state)
        {
            if (__state == null || Find.WorldPawns == null)
            {
                return __exception;
            }

            List<string> added = [];
            List<Pawn> all = Find.WorldPawns.AllPawnsAliveOrDead;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn pawn = all[i];
                if (pawn?.ThingID != null && !__state.WorldPawnsBefore.Contains(pawn.ThingID))
                {
                    added.Add(pawn.LabelShort + "[" + pawn.ThingID + "]");
                }
            }

            if (added.Count == 0)
            {
                return __exception;
            }

            string detail = __state.Map + "，新增世界池 pawn（" + added.Count + "）："
                            + string.Join("、", added.Take(5)) + (added.Count > 5 ? " 等" : "");
            if (__state.Scene != null)
            {
                string sceneId = __state.Scene.Current!.id;
                RimRunnerSceneHost.WarnScene(__state.Scene, RimRunnerWarningKey.UnregisteredMapDeinit,
                    RimRunnerSentinel.Compose("场面 " + sceneId + "：拆未经 ctx.GenerateMap 登记的图，拆后世界池新增 pawn 无人丢弃，留在世界池里直到回载基线档",
                        detail),
                    Source);
            }
            else if (__state.CaseId != null)
            {
                RimRunnerSentinel.Warn(RimRunnerWarningKey.UnregisteredMapDeinit,
                    __state.CaseId + "：拆未经 ctx.GenerateMap 登记的图，拆后世界池新增 pawn 无人丢弃，进此后每一次存档",
                    detail);
            }

            return __exception;
        }
    }

    [HarmonyPatch(typeof(MapDeiniter), nameof(MapDeiniter.Deinit), typeof(Map), typeof(bool))]
    internal static class Patch_MapDeiniter_Deinit_RimRunnerUnregisteredPawns
    {
        private static void Prefix(Map map)
        {
            if (map == null)
            {
                return;
            }

            var registered = new HashSet<Pawn>(map.mapPawns.AllPawns);
            List<Pawn> stray = map.listerThings.ThingsInGroup(ThingRequestGroup.Pawn)
                .OfType<Pawn>()
                .Where(p => p.Spawned && !registered.Contains(p))
                .ToList();
            foreach (Pawn p in stray)
            {
                try
                {
                    p.DeSpawnOrDeselect();
                    Find.WorldPawns.PassToWorld(p);
                }
                catch (Exception ex)
                {
                    RimRunnerEngineFault.Raise("拆图前交出未登记 pawn 失败 " + p + "：" + ex);
                }
            }
        }
    }
}
