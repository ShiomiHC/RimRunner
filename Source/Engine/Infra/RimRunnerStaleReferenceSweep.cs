using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Verse.AI;
using Verse.Sound;

namespace RimRunner;

/// <summary>摘掉 vanilla 缓存与产品登记的清扫里指向已销毁或已丢弃对象的键。</summary>
/// <remarks>用例体每回收 32 个 pawn 清一次，回收过 pawn 的用例收尾再清一遍并清 vanilla 的逐次查询结果表。场景装配期的回收不计。</remarks>
public static class RimRunnerStaleReferenceSweep
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "workerInt")]
    private static extern ref StatWorker StatWorkerOf(StatDef stat);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "temporaryStatCache")]
    private static extern ref Dictionary<Thing, StatCacheEntry> TemporaryStatCache(StatWorker worker);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "immutableStatCache")]
    private static extern ref ConcurrentDictionary<Thing, float> ImmutableStatCache(StatWorker worker);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "verbs")]
    private static extern ref List<Verb> BuiltVerbs(VerbTracker tracker);

    private static readonly FieldInfo SocialThoughts =
        AccessTools.Field(typeof(SituationalThoughtHandler), "cachedSocialThoughts");

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "studiableThingsCache")]
    private static extern ref Dictionary<Map, HashSet<Thing>> StudiableThings(StudyManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "pawnTextureAtlases")]
    private static extern ref List<PawnTextureAtlas> PawnTextureAtlases(
        [UnsafeAccessorType("Verse.GlobalTextureAtlasManager, Assembly-CSharp")] object? manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "cachedDangers")]
    private static extern ref Dictionary<Pawn, Danger> CachedDangers(Region region);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "cache")]
    private static extern ref ReachabilityCache ReachabilityCacheOf(Reachability reachability);

    private static readonly FieldInfo ReachabilityEntries = AccessTools.Field(typeof(ReachabilityCache), "cacheDict");

    private static readonly PropertyInfo EntryTraverseParms =
        AccessTools.Property(AccessTools.Inner(typeof(ReachabilityCache), "CachedEntry"), "TraverseParms");

    private static readonly List<FieldInfo> MapPawnsResults = ResultFields(typeof(MapPawns), BindingFlags.Instance, typeof(List<Pawn>), "List<Pawn>");

    private static readonly List<FieldInfo> PawnsFinderResults = ResultFields(typeof(PawnsFinder), BindingFlags.Static, typeof(List<Pawn>), "List<Pawn>");

    private static readonly List<FieldInfo> MapPawnsFactionResults =
        ResultFields(typeof(MapPawns), BindingFlags.Instance, AccessTools.Inner(typeof(MapPawns), "FactionDictionary"), "FactionDictionary");

    private static readonly FieldInfo PawnsInFactionResult = MapPawnsFactionResults.SingleOrDefault(field => field.Name == "pawnsInFactionResult")
        ?? throw new InvalidOperationException(RimRunnerLog.Prefix + " MapPawns 上无法解析 pawnsInFactionResult");

    private static readonly List<FieldInfo> PawnsFinderFactionResults =
        ResultFields(typeof(PawnsFinder), BindingFlags.Static, typeof(Dictionary<Faction, List<Pawn>>), "Dictionary<Faction, List<Pawn>>");

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pawnList")]
    private static extern ref Dictionary<Faction, List<Pawn>> FactionBuckets(
        [UnsafeAccessorType("Verse.MapPawns+FactionDictionary, Assembly-CSharp")] object table);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "nullFactionPawns")]
    private static extern ref List<Pawn> NullFactionBucket(
        [UnsafeAccessorType("Verse.MapPawns+FactionDictionary, Assembly-CSharp")] object table);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "concerns")]
    private static extern ref HashSet<Pawn> BattleConcerns(Battle battle);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "tmpReceivers")]
    private static extern ref List<ISignalReceiver> SignalReceivers(SignalManager? manager);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "liveMessages")]
    private static extern ref List<Message> LiveMessages(
        [UnsafeAccessorType("Verse.Messages, Assembly-CSharp")] object? messages);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "uniqueContainedThings")]
    private static extern ref List<Thing> RoomContents(Room room);

    private static readonly MethodInfo CompleteScheduledJobs = AccessTools.Method(typeof(PathFinder), "ForceCompleteScheduledJobs");

    private static readonly MethodInfo FinalizePathJobs = AccessTools.Method(typeof(PathFinder), "FinalizeRecyclePathJobData");

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "workQueue")]
    private static extern ref List<PathRequest> PathWorkQueue(PathFinder pathFinder);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "cachedProviders")]
    private static extern ref List<IPathFindCostProvider> CachedCostProviders(PathFinder pathFinder);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "cachedDoors")]
    private static extern ref List<Thing> CachedDoors(PathFinder pathFinder);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "cachedPawns")]
    private static extern ref List<Pawn> CachedPathPawns(PathFinder pathFinder);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "cachedTick")]
    private static extern ref int CachedPathTick(PathFinder pathFinder);

    private static readonly FieldInfo FreeToils = AccessTools.Field(typeof(SimplePool<Toil>), "freeItems");

    /// <summary>对象是否已销毁；pawn 另计已丢弃。</summary>
    public static bool IsGone(object? obj)
    {
        return obj is Thing thing && (thing.Destroyed || (thing is Pawn pawn && pawn.Discarded));
    }

    internal static void Sweep()
    {
        SweepStatCaches();
        SweepSocialThoughts();
        SweepArchived();
        SweepLiveMessages();
        SweepStudiableThings();
        SweepPawnTextureAtlases();
        SweepSustainers();
        SweepRegionDangers();
        SweepReachability();
        SweepRetiredRooms();
        SweepToilPool();
        SweepBattleConcerns();
        RimRunnerProduct.Capabilities.SweepStaleReferences();
    }

    internal static void ClearQueryScratch()
    {
        foreach (Map map in Find.Maps)
        {
            foreach (FieldInfo field in MapPawnsResults)
            {
                ((List<Pawn>)field.GetValue(map.mapPawns)!).Clear();
            }

            foreach (FieldInfo field in MapPawnsFactionResults)
            {
                bool prune = field == PawnsInFactionResult;
                foreach (List<Pawn> bucket in FactionBucketsOf(field.GetValue(map.mapPawns)!))
                {
                    if (prune)
                    {
                        bucket.RemoveAll(IsGone);
                    }
                    else
                    {
                        bucket.Clear();
                    }
                }
            }
        }

        foreach (FieldInfo field in PawnsFinderResults)
        {
            ((List<Pawn>)field.GetValue(null)!).Clear();
        }

        foreach (FieldInfo field in PawnsFinderFactionResults)
        {
            foreach (List<Pawn> bucket in ((Dictionary<Faction, List<Pawn>>)field.GetValue(null)!).Values)
            {
                bucket.Clear();
            }
        }

        SignalReceivers(null).Clear();
        PruneRoomContents();
    }

    internal static List<object> QueryScratchTables()
    {
        List<object> tables = [];
        foreach (Map map in Find.Maps)
        {
            foreach (FieldInfo field in MapPawnsResults)
            {
                tables.Add(field.GetValue(map.mapPawns)!);
            }

            foreach (FieldInfo field in MapPawnsFactionResults)
            {
                tables.AddRange(FactionBucketsOf(field.GetValue(map.mapPawns)!));
            }

            foreach (Room room in map.regionGrid.AllRooms)
            {
                tables.Add(RoomContents(room));
            }
        }

        foreach (FieldInfo field in PawnsFinderResults)
        {
            tables.Add(field.GetValue(null)!);
        }

        foreach (FieldInfo field in PawnsFinderFactionResults)
        {
            tables.AddRange(((Dictionary<Faction, List<Pawn>>)field.GetValue(null)!).Values);
        }

        tables.Add(SignalReceivers(null));
        return tables;
    }

    private static IEnumerable<List<Pawn>> FactionBucketsOf(object table)
    {
        foreach (List<Pawn> bucket in FactionBuckets(table).Values)
        {
            yield return bucket;
        }

        yield return NullFactionBucket(table);
    }

    internal static void FlushFrozenMap(Map map)
    {
        PathFinder pathFinder = map.pathFinder;
        CompleteScheduledJobs.Invoke(pathFinder, null);
        FinalizePathJobs.Invoke(pathFinder, null);
        PathWorkQueue(pathFinder).RemoveAll(request => request.Cancelled);
        CachedCostProviders(pathFinder).Clear();
        CachedDoors(pathFinder).Clear();
        CachedPathPawns(pathFinder).Clear();
        CachedPathTick(pathFinder) = int.MinValue;
        PruneRoomContents(map);
    }

    private static void PruneRoomContents()
    {
        foreach (Map map in Find.Maps)
        {
            PruneRoomContents(map);
        }
    }

    private static void PruneRoomContents(Map map)
    {
        foreach (Room room in map.regionGrid.AllRooms)
        {
            RoomContents(room).RemoveAll(IsGone);
        }
    }

    private static List<FieldInfo> ResultFields(Type owner, BindingFlags binding, Type? fieldType, string shape)
    {
        List<FieldInfo> fields = owner.GetFields(binding | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.FieldType == fieldType && field.Name.EndsWith("Result", StringComparison.Ordinal))
            .ToList();
        if (fields.Count == 0)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + owner.Name + " 上无法解析名以 Result 结尾的 " + shape + " 字段");
        }

        return fields;
    }

    private static void SweepStatCaches()
    {
        List<Thing> gone = [];
        foreach (StatDef stat in DefDatabase<StatDef>.AllDefsListForReading)
        {
            StatWorker? worker = StatWorkerOf(stat);
            if (worker == null)
            {
                continue;
            }

            gone.Clear();
            CollectGoneStatKeys(TemporaryStatCache(worker)?.Keys, gone);
            CollectGoneStatKeys(ImmutableStatCache(worker)?.Keys, gone);
            foreach (Thing thing in gone)
            {
                worker.ClearCacheForThing(thing);
            }
        }
    }

    private static void CollectGoneStatKeys(IEnumerable<Thing>? keys, List<Thing> gone)
    {
        if (keys == null)
        {
            return;
        }

        foreach (Thing key in keys)
        {
            if (IsGone(key) || CasterIsGone(key))
            {
                gone.Add(key);
            }
        }
    }

    private static bool CasterIsGone(Thing key)
    {
        VerbTracker? tracker = (key as ThingWithComps)?.GetComp<CompEquippable>()?.verbTracker;
        List<Verb>? verbs = tracker == null ? null : BuiltVerbs(tracker);
        return verbs != null && verbs.Any(verb => IsGone(verb.caster));
    }

    private static void SweepSocialThoughts()
    {
        List<Pawn> gone = [];
        foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
        {
            SituationalThoughtHandler? situational = pawn.needs?.mood?.thoughts?.situational;
            if (situational == null)
            {
                continue;
            }

            IDictionary cache = (IDictionary)SocialThoughts.GetValue(situational)!;
            gone.Clear();
            CollectGone(cache.Keys.Cast<Pawn>(), gone);
            foreach (Pawn other in gone)
            {
                cache.Remove(other);
            }
        }
    }

    private static void SweepArchived()
    {
        List<IArchivable>? gone = null;
        foreach (IArchivable archivable in Find.Archive.ArchivablesListForReading)
        {
            bool stale = archivable switch
            {
                Message { lookTargets.targets: { } targets } => targets.Any(target => IsGone(target.Thing)),
                Letter letter => RimRunnerLetterProbe.Holds(letter, IsGone),
                _ => false,
            };
            if (stale)
            {
                (gone ??= []).Add(archivable);
            }
        }

        if (gone == null)
        {
            return;
        }

        foreach (IArchivable archivable in gone)
        {
            Find.Archive.Remove(archivable);
        }
    }

    private static void SweepLiveMessages()
    {
        LiveMessages(null).RemoveAll(message =>
            message.lookTargets?.targets != null && message.lookTargets.targets.Any(target => IsGone(target.Thing)));
    }

    private static void SweepPawnTextureAtlases()
    {
        foreach (PawnTextureAtlas atlas in PawnTextureAtlases(null))
        {
            atlas.GC();
        }
    }

    private static void SweepSustainers()
    {
        List<Sustainer> sustainers = Find.SoundRoot.sustainerManager.AllSustainers;
        for (int i = sustainers.Count - 1; i >= 0; i--)
        {
            if (IsGone(sustainers[i].info.Maker.Thing))
            {
                sustainers[i].SustainerUpdate();
            }
        }
    }

    private static void SweepRegionDangers()
    {
        List<Pawn> gone = [];
        foreach (Map map in Find.Maps)
        {
            foreach (Region region in map.regionGrid.AllRegions)
            {
                Dictionary<Pawn, Danger> dangers = CachedDangers(region);
                gone.Clear();
                CollectGone(dangers.Keys, gone);
                foreach (Pawn pawn in gone)
                {
                    dangers.Remove(pawn);
                }
            }
        }
    }

    private static void SweepReachability()
    {
        HashSet<Pawn> gone = [];
        foreach (Map map in Find.Maps)
        {
            gone.Clear();
            foreach (object entry in ((IDictionary)ReachabilityEntries.GetValue(ReachabilityCacheOf(map.reachability))!).Keys)
            {
                Pawn pawn = ((TraverseParms)EntryTraverseParms.GetValue(entry)!).pawn;
                if (IsGone(pawn))
                {
                    gone.Add(pawn);
                }
            }

            foreach (Pawn pawn in gone)
            {
                map.reachability.ClearCacheFor(pawn);
            }
        }
    }

    private static void SweepToilPool()
    {
        foreach (Toil toil in (Queue<Toil>)FreeToils.GetValue(null)!)
        {
            if (toil.actor != null && IsGone(toil.actor))
            {
                toil.actor = null;
            }
        }
    }

    private static void SweepRetiredRooms()
    {
        List<int> retired = [];
        foreach (Map map in Find.Maps)
        {
            Dictionary<int, Room> registered = map.regionAndRoomUpdater.roomLookup;
            IReadOnlyDictionary<int, Room> live = map.regionGrid.RoomLookup;
            retired.Clear();
            foreach (int id in registered.Keys)
            {
                if (!live.ContainsKey(id))
                {
                    retired.Add(id);
                }
            }

            foreach (int id in retired)
            {
                registered.Remove(id);
            }
        }
    }

    private static void SweepBattleConcerns()
    {
        HashSet<Pawn>? gone = null;
        foreach (Battle battle in Find.BattleLog.Battles)
        {
            foreach (Pawn pawn in BattleConcerns(battle))
            {
                if (pawn.Discarded)
                {
                    (gone ??= []).Add(pawn);
                }
            }
        }

        if (gone == null)
        {
            return;
        }

        foreach (Pawn pawn in gone)
        {
            Find.BattleLog.Notify_PawnDiscarded(pawn, silentlyRemoveReferences: true);
        }
    }

    private static void SweepStudiableThings()
    {
        foreach (HashSet<Thing> things in StudiableThings(Find.StudyManager).Values)
        {
            things.RemoveWhere(IsGone);
        }
    }

    private static void CollectGone<T>(IEnumerable<T>? keys, List<T> gone)
    {
        if (keys == null)
        {
            return;
        }

        foreach (T key in keys)
        {
            if (IsGone(key))
            {
                gone.Add(key);
            }
        }
    }
}
