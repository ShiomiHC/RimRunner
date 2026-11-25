using System.Runtime.CompilerServices;
using RimWorld.Planet;

namespace RimRunner;

internal static class RimRunnerIsolatedMap
{
    internal const string GeneratorDefName = "RimRunner_IsolatedMap";
    internal const string SettlementDefName = "RimRunner_IsolatedSettlement";

    internal const string PocketGeneratorDefName = "RimRunner_PocketMap";

    private const int TileSearchLimit = 4000;

    private sealed class PooledMap
    {
        internal required Map Map;
        internal required Settlement? Parent;
        internal IntVec2 Size;
        internal RimRunnerMapGround Ground;
        internal required RimRunnerMapImprint Pristine;
        internal RimRunnerMapRestore? Restore;
        internal string? Owner;
    }

    private sealed class SavedMap
    {
        internal int MapId;
        internal IntVec2 Size;
        internal RimRunnerMapGround Ground;
        internal required RimRunnerMapImprint Pristine;
        internal RimRunnerMapRestore? Restore;
    }

    private sealed class CheckpointBinding
    {
        internal int MainId;
        internal readonly Dictionary<string, List<SavedMap>> Parked = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, List<int>> HeldGenerated = new(StringComparer.Ordinal);
    }

    private static Game? game;
    private static readonly List<PooledMap> pool = [];
    private static readonly List<PooledMap> leased = [];
    private static Map? frozen;
    private static readonly Dictionary<string, List<PooledMap>> parked = new(StringComparer.Ordinal);
    private static readonly HashSet<Map> parkedMaps = [];
    private static readonly Dictionary<string, List<Map>> heldGenerated = new(StringComparer.Ordinal);
    private static readonly HashSet<Map> heldGeneratedMaps = [];
    private static CheckpointBinding? atCheckpointSave;
    private static Map? shownFrom;
    private static bool tearDownAtCaseEnd;
    private static readonly List<string> switchedTo = [];
    private static int switchCount;
    private const int SwitchLinesPerCase = 3;
    private static PooledMap? sceneLease;
    private static Map? sceneShownFrom;

    internal static IntVec2 SizeOf(RimRunnerMapTier tier)
    {
        int side = tier switch
        {
            RimRunnerMapTier.Default => 40,
            RimRunnerMapTier.Large => 60,
            RimRunnerMapTier.Huge => 100,
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "未知的小图档位"),
        };
        return new IntVec2(side, side);
    }

    internal static string Describe(IntVec2 size)
    {
        foreach (RimRunnerMapTier tier in Enum.GetValues(typeof(RimRunnerMapTier)))
        {
            if (SizeOf(tier) == size)
            {
                return tier.ToString();
            }
        }

        return size.x + "x" + size.z;
    }

    internal static string? SizeFault(IntVec2 size) =>
        size.x > 0 && size.z > 0 ? null : "独立小图的宽高须为正（实际 " + size.x + "×" + size.z + "）";

    internal static Map? Active
    {
        get
        {
            DropIfStale();
            return leased.Count > 0 ? leased[0].Map : null;
        }
    }

    internal static List<Map> LeasedMaps
    {
        get
        {
            DropIfStale();
            return leased.Select(entry => entry.Map).ToList();
        }
    }

    internal static bool AnyLeasedOnParallelSite
    {
        get
        {
            DropIfStale();
            return leased.Any(entry => IsLive(entry) && RimRunnerParallelSite.IsOn(entry.Map.Tile));
        }
    }

    internal static int PooledCount
    {
        get
        {
            DropIfStale();
            return pool.Count;
        }
    }

    internal static Map LiveMap => Active ?? RimRunnerCurrentMapProbe.EngineCurrentMap()!;

    internal static Map? Frozen
    {
        get
        {
            DropIfStale();
            return frozen;
        }
    }

    internal static bool IsFrozenMap(Map map) =>
        frozen != null && (map == frozen || (parkedMaps.Count > 0 && parkedMaps.Contains(map))
                                         || (heldGeneratedMaps.Count > 0 && heldGeneratedMaps.Contains(map)));

    internal static void HoldGenerated(string owner, Map? map)
    {
        DropIfStale();
        if (map == null || frozen == null)
        {
            return;
        }

        if (!heldGenerated.TryGetValue(owner, out List<Map>? maps))
        {
            maps = [];
            heldGenerated[owner] = maps;
        }

        maps.Add(map);
        heldGeneratedMaps.Add(map);
    }

    internal static void ReleaseGenerated(Map? map)
    {
        if (map == null || !heldGeneratedMaps.Remove(map))
        {
            return;
        }

        foreach (List<Map> maps in heldGenerated.Values)
        {
            maps.Remove(map);
        }
    }

    internal static bool IsFrozen(Thing thing) => frozen != null && IsFrozenMap(thing.MapHeld);

    internal static bool IsForeignMap(Map map) =>
        frozen != null && map != null && (map == frozen || pool.Any(entry => entry.Map == map && !leased.Contains(entry)));

    internal static string ParkedOwner(Map map) =>
        parked.FirstOrDefault(pair => pair.Value.Any(entry => entry.Map == map)).Key;

    internal static bool HoldsCurrentMap(Map target)
    {
        DropIfStale();
        return frozen != null && leased.Count > 0 && shownFrom == null && (realFrameShown || target != frozen);
    }

    private static bool realFrameShown;

    internal static void ShowForRealFrame(Map map)
    {
        realFrameShown = true;
        Current.Game.CurrentMap = map;
        CameraDriver driver = Find.CameraDriver;
        driver.SetRootPosAndSize(map.Center.ToVector3Shifted(), driver.RootSize);
        InvalidateViewRect();
    }

    private static AccessTools.FieldRef<int>? viewRectFrame;
    private static bool viewRectFrameResolved;

    private static void InvalidateViewRect()
    {
        if (!viewRectFrameResolved)
        {
            viewRectFrameResolved = true;
            FieldInfo? field = AccessTools.Field(typeof(CameraDriver), "lastViewRectGetFrame");
            if (field == null)
                RimRunnerEngineFault.Raise("无法解析 CameraDriver.lastViewRectGetFrame：真实帧用例切到小图的那一帧读视野仍是主图相机的矩形");
            else
                viewRectFrame = AccessTools.StaticFieldRefAccess<int>(field);
        }

        if (viewRectFrame != null) viewRectFrame() = -1;
    }

    internal static bool Capturing => shownFrom != null;

    internal static bool Generating => generating > 0;

    private static int generating;

    internal static void RunAsGeneratingForSelfTest(Action body)
    {
        generating++;
        try
        {
            body();
        }
        finally
        {
            generating--;
        }
    }

    internal static bool IsPoolMap(Map? map)
    {
        DropIfStale();
        return map != null && pool.Any(entry => entry.Map == map);
    }

    internal static void SiteFindings(string id, RimRunnerLevel level,
        RimRunnerMapSite site, out List<string> faults, out List<KeyValuePair<string, string>> warnings,
        out string? note)
    {
        faults = [];
        warnings = [];
        note = null;
        string head = "用例 " + id + " 声明 MapSite = ";
        string? mainByDefault = MainByDefault(level);
        switch (site.Kind)
        {
            case RimRunnerMapSite.Choice.Isolated:
                faults.AddRange(DeclarationFaults(id, level));
                if (SizeFault(site.Size) is { } sizeFault)
                {
                    faults.Add("用例 " + id + " 声明 MapSite = Isolated：" + sizeFault);
                }

                if (faults.Count == 0 && mainByDefault == null
                    && site.Size == SizeOf(RimRunnerMapTier.Default) && site.Ground == RimRunnerMapGround.Concrete)
                {
                    note = head + "Isolated()（缺省档、混凝土）：World 级的回归与读数用例缺省即如此";
                }

                break;
            case RimRunnerMapSite.Choice.Main:
                if (mainByDefault != null)
                {
                    note = head + "Main(…)：" + mainByDefault + " 缺省就留主图";
                }
                else if (string.IsNullOrWhiteSpace(site.Reason))
                {
                    warnings.Add(new KeyValuePair<string, string>(RimRunnerWarningKey.EmptyReason,
                        head + "Main，理由为空：报告 mainMap 里本条未保留主图的原因"));
                }

                break;
        }
    }

    internal static string? MainByDefault(RimRunnerLevel level)
    {
        return level != RimRunnerLevel.World ? "Level=" + level : null;
    }

    internal static List<string> DeclarationFaults(string id, RimRunnerLevel level)
    {
        List<string> faults = [];
        if (level != RimRunnerLevel.World)
        {
            faults.Add("用例 " + id + " 声明 MapSite = Isolated，须 Level=World（实际 " + level + "）：非 World 档没有地图可给");
        }

        return faults;
    }

    internal static void Enter(RimRunnerContext ctx, IntVec2 size, RimRunnerMapGround ground, bool lastInSlot,
        string? owner = null, bool compareWorld = true)
    {
        DropIfStale();
        if (leased.Count > 0)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 上一条用例领取的独立小图未收："
                                                + string.Join("、", leased.Select(entry => entry.Map.ToString())));
        }

        Map main = ctx.Map ?? throw new InvalidOperationException(
            RimRunnerLog.Prefix + " 独立小图要从主图起：ctx.Map 为 null");
        if (frozen != null && frozen != main)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 冻结的主图（" + frozen
                                                + "）不是本条的 ctx.Map（" + main + "）");
        }

        frozen = main;
        RimRunnerStaleReferenceSweep.FlushFrozenMap(main);
        tearDownAtCaseEnd = lastInSlot;
        switchedTo.Clear();
        switchCount = 0;
        ctx.Map = Lease(size, ground, owner, compareWorld);
    }

    internal static void Resume(RimRunnerContext ctx, string owner, bool lastInSlot, Action beforeLoadTick)
    {
        DropIfStale();
        if (leased.Count > 0)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 上一条用例领取的独立小图未收："
                                                + string.Join("、", leased.Select(entry => entry.Map.ToString())));
        }

        if (!parked.TryGetValue(owner, out List<PooledMap>? mine) || mine.Count == 0 || !mine.All(IsLive))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 多阶段用例 " + owner
                                                + " 停放的独立小图已不存在，这一 phase 没有图可执行");
        }

        if (frozen == null || ctx.Map != frozen)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 多阶段用例 " + owner + " 继续领取小图时主图未冻结（冻结="
                                                + frozen + "，ctx.Map=" + ctx.Map + "）");
        }

        parked.Remove(owner);
        foreach (PooledMap entry in mine)
        {
            parkedMaps.Remove(entry.Map);
        }

        if (heldGenerated.TryGetValue(owner, out List<Map>? generated))
        {
            heldGenerated.Remove(owner);
            heldGeneratedMaps.ExceptWith(generated);
        }

        leased.AddRange(mine);
        tearDownAtCaseEnd = lastInSlot;
        switchedTo.Clear();
        switchCount = 0;
        ctx.Map = mine[0].Map;
        RimRunnerTimeDriver.HoldTicks("BeforeLoadTick", beforeLoadTick);
        RimRunnerTimeDriver.AdvanceBurst(1);
    }

    internal static Map Acquire(IntVec2? size)
    {
        DropIfStale();
        if (leased.Count == 0)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " AcquireIsolatedMap 只给在独立小图上执行的用例，且只在用例体或场面步骤内可调");
        }

        return Lease(size ?? leased[0].Size, leased[0].Ground, leased[0].Owner, compareWorld: false);
    }

    internal static Map LeaseForScene(IntVec2 size, RimRunnerMapGround ground)
    {
        DropIfStale();
        if (frozen != null || leased.Count > 0 || sceneLease != null)
        {
            string holder = frozen != null ? "多阶段用例有停放的小图" : leased.Count > 0 ? "用例领取的小图未还" : "上一场的图未还";
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 场面领取独立小图时已有领用（" + holder + "）");
        }

        Map main = RimRunnerCurrentMapProbe.EngineCurrentMap()!;
        PooledMap entry = pool.FirstOrDefault(candidate => candidate.Size == size && candidate.Ground == ground
                                                            && candidate.Owner == null)
                          ?? Generate(size, ground, main);
        RimRunnerEnvChecks.NoteUse(RimRunnerFacility.IsolatedMap);
        entry.Restore = new RimRunnerMapRestore(entry.Map, RimRunnerComponentScope.None);
        sceneLease = entry;
        sceneShownFrom = main;
        return entry.Map;
    }

    internal static void ShowSceneMap()
    {
        if (sceneLease == null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 场面没有领取独立小图，无图可切");
        }

        Current.Game.CurrentMap = sceneLease.Map;
    }

    internal static List<string> ReleaseScene()
    {
        DropIfStale();
        PooledMap? entry = sceneLease;
        if (entry == null)
        {
            return [];
        }

        sceneLease = null;
        Map? back = sceneShownFrom;
        sceneShownFrom = null;
        if (back != null && Find.Maps.Contains(back) && RimRunnerCurrentMapProbe.EngineCurrentMap() != back)
        {
            Current.Game.CurrentMap = back;
        }

        if (!IsLive(entry))
        {
            pool.Remove(entry);
            return ["场面的独立小图已不在本局"];
        }

        List<string> drift = entry.Restore!.Close(_ => false).Select(unres => unres.Text).ToList();
        entry.Restore = null;
        drift.AddRange(entry.Pristine.DriftTo(RimRunnerMapImprint.Take(entry.Map)));
        if (drift.Count > 0)
        {
            pool.Remove(entry);
        }

        return drift;
    }

    private static Map Lease(IntVec2 size, RimRunnerMapGround ground, string? owner, bool compareWorld)
    {
        PooledMap entry = pool.FirstOrDefault(candidate => candidate.Size == size && candidate.Ground == ground
                                                            && candidate.Owner == null && !leased.Contains(candidate) && candidate != sceneLease)
                          ?? Generate(size, ground, frozen);
        RimRunnerEnvChecks.NoteUse(RimRunnerFacility.IsolatedMap);
        entry.Owner = owner;
        entry.Restore = new RimRunnerMapRestore(entry.Map,
            owner != null ? RimRunnerComponentScope.None
            : leased.Count == 0 ? RimRunnerComponentScope.MapAndGlobal : RimRunnerComponentScope.Map,
            compareWorld);
        leased.Add(entry);
        return entry.Map;
    }

    internal static Map ReturnLastLeaseForSelfTest()
    {
        DropIfStale();
        if (leased.Count < 2)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 只交回首张之外的图：首张是 ctx.Map");
        }

        PooledMap entry = leased[^1];
        leased.RemoveAt(leased.Count - 1);
        entry.Restore = null;
        entry.Owner = null;
        return entry.Map;
    }

    private static PooledMap Generate(IntVec2 size, RimRunnerMapGround ground, Map? main)
    {
        generating++;
        try
        {
            return GenerateCore(size, ground, main);
        }
        finally
        {
            generating--;
        }
    }

    private static PooledMap GenerateCore(IntVec2 size, RimRunnerMapGround ground, Map? main)
    {
        PlanetTile tile = FindTile(main);
        Settlement settlement = (Settlement)WorldObjectMaker.MakeWorldObject(
            DefDatabase<WorldObjectDef>.GetNamed(SettlementDefName));
        settlement.Tile = tile;
        settlement.SetFaction(Faction.OfPlayer);
        settlement.Name = "RimRunner " + settlement.ID;
        settlement.namedByPlayer = true;
        Find.WorldObjects.Add(settlement);
        Map map;
        RimRunnerIsolatedMapGenStep.Ground = ground;
        try
        {
            map = MapGenerator.GenerateMap(new IntVec3(size.x, 1, size.z), settlement,
                DefDatabase<MapGeneratorDef>.GetNamed(GeneratorDefName));
        }
        catch
        {
            if (!settlement.Destroyed)
            {
                settlement.Destroy();
            }

            throw;
        }
        finally
        {
            RimRunnerIsolatedMapGenStep.Ground = RimRunnerMapGround.Concrete;
        }

        MapComponentUtility.MapComponentTick(map);
        PooledMap entry = new()
        {
            Map = map,
            Parent = settlement,
            Size = size,
            Ground = ground,
            Pristine = RimRunnerMapImprint.Take(map)
        };
        pool.Add(entry);
        return entry;
    }

    internal static PlanetTile FindTile(Map? main)
    {
        if (main == null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 选小图 tile 要从主图起：主图为 null");
        }

        BiomeDef biome = main.Biome;
        if (biome.extraGenSteps.Any())
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 主图 biome " + biome.defName
                + " 带 extraGenSteps（" + string.Join("、", biome.extraGenSteps.Select(step => step.defName))
                + "），独立小图会随 biome 生成这些内容，与空图不匹配");
        }

        if (!RimRunnerParallelSite.ForceForSelfTest && SearchFrom(main.Tile, biome, includeStart: false) is { } found)
        {
            return found;
        }

        return RimRunnerParallelSite.TileFor(main.Tile, biome);
    }

    internal static PlanetTile? SearchFrom(PlanetTile start, BiomeDef biome, bool includeStart)
    {
        if (includeStart && IsUsableTile(start, biome))
        {
            return start;
        }

        HashSet<PlanetTile> seen = [start];
        Queue<PlanetTile> frontier = new();
        frontier.Enqueue(start);
        List<PlanetTile> neighbors = [];
        while (frontier.Count > 0 && seen.Count < TileSearchLimit)
        {
            PlanetTile current = frontier.Dequeue();
            neighbors.Clear();
            Find.WorldGrid.GetTileNeighbors(current, neighbors);
            foreach (PlanetTile next in neighbors)
            {
                if (!seen.Add(next))
                {
                    continue;
                }

                if (IsUsableTile(next, biome))
                {
                    return next;
                }

                frontier.Enqueue(next);
            }
        }

        return null;
    }

    private static bool IsUsableTile(PlanetTile tile, BiomeDef biome)
    {
        Tile info = Find.WorldGrid[tile];
        return info.PrimaryBiome == biome
               && biome.canBuildBase && biome.implemented
               && info.hilliness != Hilliness.Impassable
               && info.Mutators.Count == 0
               && info.pollution <= 0f
               && !Find.WorldObjects.AnyWorldObjectAt(tile);
    }

    internal static List<string> Release(Func<Thing, bool> held, List<string>? componentNotes = null, bool park = false,
        List<RimRunnerMapRestore>? worldRestores = null)
    {
        DropIfStale();
        RestoreAfterCapture();
        List<string> drift = [];
        Map current = RimRunnerCurrentMapProbe.EngineCurrentMap()!;
        bool shownByEngine = realFrameShown;
        realFrameShown = false;
        if (current != null && frozen != null && current != frozen && pool.Any(entry => entry.Map == current)
            && Find.Maps.Contains(frozen))
        {
            Current.Game.CurrentMap = frozen;
        }

        if (current != null && frozen != null && current != frozen && pool.Any(entry => entry.Map == current)
            && !shownByEngine)
        {
            drift.Add("当前地图被切到独立小图 " + current + "，收尾已切回主图");
        }

        drift.AddRange(switchedTo);
        if (switchCount > switchedTo.Count)
        {
            drift.Add("用例期内当前地图另被切到独立小图 " + (switchCount - switchedTo.Count) + " 次未记调用栈");
        }

        switchedTo.Clear();
        switchCount = 0;
        List<PooledMap> released = leased.ToList();
        leased.Clear();
        List<PooledMap> discard = [];
        List<PooledMap> parking = [];
        for (int index = 0; index < released.Count; index++)
        {
            PooledMap entry = released[index];
            if (!IsLive(entry))
            {
                if (entry.Owner == null && entry.Restore != null)
                {
                    worldRestores?.Add(entry.Restore);
                }

                pool.Remove(entry);
                continue;
            }

            if (entry.Owner != null)
            {
                (park ? parking : discard).Add(entry);
                continue;
            }

            List<string> mapDrift = entry.Restore!.Close(held).Select(unres => unres.Text).ToList();
            componentNotes?.AddRange(entry.Restore.ComponentNotes);
            worldRestores?.Add(entry.Restore);
            entry.Restore = null;
            mapDrift.AddRange(entry.Pristine.DriftTo(RimRunnerMapImprint.Take(entry.Map)));
            if (mapDrift.Count > 0)
            {
                drift.AddRange(mapDrift.Select(item => LabelOf(index) + "图面留痕（该图已弃用）：" + item));
                discard.Add(entry);
            }
        }

        if (parking.Count > 0)
        {
            parked[parking[0].Owner!] = parking;
            parkedMaps.UnionWith(parking.Select(entry => entry.Map));
        }

        if (tearDownAtCaseEnd)
        {
            tearDownAtCaseEnd = false;
            TearDownSlot();
        }
        else if (discard.Count > 0)
        {
            TearDown(discard);
        }

        return drift;
    }

    internal static void NoteCheckpointSave()
    {
        DropIfStale();
        atCheckpointSave = null;
        if (frozen == null)
        {
            return;
        }

        List<PooledMap> stray = pool.Where(entry => entry.Owner == null || !parkedMaps.Contains(entry.Map)).ToList();
        if (stray.Count > 0)
        {
            RimRunnerEngineFault.Raise("checkpoint 存档时池里有未停放的独立小图（"
                      + string.Join("、", stray.Select(entry => entry.Map.ToString())) + "），存档前拆掉");
            TearDown(stray);
        }

        CheckpointBinding binding = new() { MainId = frozen.uniqueID };
        foreach (KeyValuePair<string, List<PooledMap>> pair in parked)
        {
            binding.Parked[pair.Key] = pair.Value.Select(entry =>
            {
                entry.Restore?.NoteCheckpointSave();
                return new SavedMap
                {
                    MapId = entry.Map.uniqueID,
                    Size = entry.Size,
                    Ground = entry.Ground,
                    Pristine = entry.Pristine,
                    Restore = entry.Restore
                };
            }).ToList();
        }

        foreach (KeyValuePair<string, List<Map>> pair in heldGenerated)
        {
            binding.HeldGenerated[pair.Key] = pair.Value
                .Where(map => !map.Disposed && Find.Maps.Contains(map))
                .Select(map => map.uniqueID)
                .ToList();
        }

        atCheckpointSave = binding;
    }

    internal static void OnGameLoaded(bool checkpointLoad)
    {
        game = null;
        DropIfStale();
        CheckpointBinding? binding = atCheckpointSave;
        if (!checkpointLoad || binding == null)
        {
            return;
        }

        frozen = Find.Maps.FirstOrDefault(map => map.uniqueID == binding.MainId);
        if (frozen == null)
        {
            RimRunnerEngineFault.Raise("读回 checkpoint 后找不到存档时冻结的主图（uniqueID " + binding.MainId + "）");
            return;
        }

        foreach (KeyValuePair<string, List<SavedMap>> pair in binding.Parked)
        {
            List<PooledMap> entries = [];
            foreach (SavedMap saved in pair.Value)
            {
                Map map = Find.Maps.FirstOrDefault(candidate => candidate.uniqueID == saved.MapId);
                if (map == null)
                {
                    continue;
                }

                saved.Restore?.NoteCheckpointLoad();
                entries.Add(new PooledMap
                {
                    Map = map,
                    Parent = map.Parent as Settlement,
                    Size = saved.Size,
                    Ground = saved.Ground,
                    Pristine = saved.Pristine,
                    Restore = saved.Restore,
                    Owner = pair.Key
                });
            }

            if (entries.Count == 0)
            {
                continue;
            }

            pool.AddRange(entries);
            parked[pair.Key] = entries;
            parkedMaps.UnionWith(entries.Select(entry => entry.Map));
        }

        foreach (KeyValuePair<string, List<int>> pair in binding.HeldGenerated)
        {
            foreach (int mapId in pair.Value)
            {
                HoldGenerated(pair.Key, Find.Maps.FirstOrDefault(candidate => candidate.uniqueID == mapId));
            }
        }
    }

    internal static void NoteCurrentMapWrite(Map target)
    {
        if (target == null || game != Current.Game || leased.Count == 0
            || Capturing || realFrameShown || RimRunnerCurrentMap.Borrowing || pool.All(entry => entry.Map != target))
        {
            return;
        }

        switchCount++;
        if (switchedTo.Count >= SwitchLinesPerCase)
        {
            return;
        }

        string frames = string.Join(" ← ", new System.Diagnostics.StackTrace(2, false).GetFrames()
            ?.Select(RimRunnerCallSite.MethodOf)
            .Where(method => method?.DeclaringType != null && !(method.DeclaringType == typeof(Game) && method.Name == "set_CurrentMap"))
            .Take(6)
            .Select(method => method!.DeclaringType!.Name + "." + method.Name) ?? []);
        switchedTo.Add("用例期内当前地图被切到独立小图（" + target + "）：" + frames
                       + "；切换期间该图按相机逐 tick 交付");
    }

    internal static List<string> TakeSwitchNotesForSelfTest()
    {
        List<string> taken = switchedTo.ToList();
        switchedTo.Clear();
        switchCount = 0;
        return taken;
    }

    internal static string LabelOf(int leaseIndex) => leaseIndex == 0 ? "独立小图" : "独立小图 #" + (leaseIndex + 1);

    internal static void TearDownSlot()
    {
        DropIfStale();
        RestoreAfterCapture();
        leased.Clear();
        parked.Clear();
        parkedMaps.Clear();
        heldGenerated.Clear();
        heldGeneratedMaps.Clear();
        tearDownAtCaseEnd = false;
        if (realFrameShown && frozen != null && Find.Maps.Contains(frozen) && RimRunnerCurrentMapProbe.EngineCurrentMap() != frozen)
        {
            Current.Game.CurrentMap = frozen;
        }

        frozen = null;
        realFrameShown = false;
        TearDown(pool.ToList());
    }

    private static void TearDown(List<PooledMap> entries)
    {
        List<Exception> failures = [];
        foreach (PooledMap entry in entries)
        {
            pool.Remove(entry);
            parkedMaps.Remove(entry.Map);
            if (entry.Owner != null && entry.Restore != null && IsLive(entry))
            {
                try
                {
                    entry.Restore.Close(_ => false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            entry.Restore = null;
        }

        List<PooledMap> live = entries.Where(IsLive).ToList();
        if (live.Count > 0)
        {
            RimRunnerTimeDriver.AdvanceBurst(1);
        }

        foreach (PooledMap entry in entries)
        {
            try
            {
                RimRunnerMapLifecyclePatches.RunAsEngineTeardown(() =>
                {
                    if (IsLive(entry))
                    {
                        Current.Game.DeinitAndRemoveMap(entry.Map, notifyPlayer: false);
                    }

                    if (!entry.Parent!.Destroyed)
                    {
                        entry.Parent.Destroy();
                    }
                });
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(RimRunnerLog.Prefix + " 独立小图拆除失败", failures);
        }
    }

    private static bool IsLive(PooledMap entry) =>
        entry.Map != null && !entry.Map.Disposed && Find.Maps.Contains(entry.Map);

    internal static void ForgetGameForSelfTest() => game = null;

    private static void DropIfStale()
    {
        Game now = Current.Game;
        if (game == now)
        {
            return;
        }

        game = now;
        pool.Clear();
        leased.Clear();
        parked.Clear();
        parkedMaps.Clear();
        heldGenerated.Clear();
        heldGeneratedMaps.Clear();
        frozen = null;
        realFrameShown = false;
        shownFrom = null;
        sceneLease = null;
        sceneShownFrom = null;
        tearDownAtCaseEnd = false;
    }

    internal static void ShowForCapture(Map? map)
    {
        Map? target = map ?? Active;
        if (target == null || !Find.Maps.Contains(target))
        {
            return;
        }

        if (RimRunnerCurrentMapProbe.EngineCurrentMap() == target)
        {
            return;
        }

        List<object> keep = Find.Selector.SelectedObjectsListForReading
            .Where(selected => SelectedOn(selected) == target)
            .ToList();
        shownFrom ??= RimRunnerCurrentMapProbe.EngineCurrentMap();
        Current.Game.CurrentMap = target;
        Find.CameraDriver.JumpToCurrentMapLoc(target.Center);
        foreach (object selected in keep)
        {
            Find.Selector.Select(selected, playSound: false, forceDesignatorDeselect: false);
        }
    }

    private static Map? SelectedOn(object selected) => selected switch
    {
        Thing thing => thing.MapHeld,
        Zone zone => zone.Map,
        Plan plan => plan.Map,
        _ => null,
    };

    internal static void RestoreAfterCapture()
    {
        if (shownFrom == null)
        {
            return;
        }

        if (Find.Maps.Contains(shownFrom))
        {
            Current.Game.CurrentMap = shownFrom;
        }

        shownFrom = null;
    }
}

/// <summary>独立小图的常用尺寸档位，同尺寸同地面的图在执行槽内复用。</summary>
public enum RimRunnerMapTier
{
    /// <summary>缺省档，40²。</summary>
    Default,

    /// <summary>60²。</summary>
    Large,

    /// <summary>100²。</summary>
    Huge
}

public class RimRunnerIsolatedMapGenStep : GenStep
{
    internal static RimRunnerMapGround Ground = RimRunnerMapGround.Concrete;

    public override int SeedPart => 715390221;

    public override void Generate(Map map, GenStepParams parms)
    {
        TerrainDef terrain = Ground == RimRunnerMapGround.Soil ? TerrainDefOf.Soil : TerrainDefOf.Concrete;
        foreach (IntVec3 cell in map.AllCells)
        {
            map.terrainGrid.SetTerrain(cell, terrain);
        }
    }
}

[HarmonyPatch(typeof(WeatherDecider), "ChooseNextWeather")]
internal static class Patch_WeatherDecider_ChooseNextWeather_RimRunnerIsolatedMap
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "map")]
    private static extern ref Map DeciderMap(WeatherDecider decider);

    private static bool Prefix(WeatherDecider __instance, ref WeatherDef __result)
    {
        if (DeciderMap(__instance)?.generatorDef?.defName != RimRunnerIsolatedMap.GeneratorDefName)
        {
            return true;
        }

        __result = WeatherDefOf.Clear;
        return false;
    }
}

[HarmonyPatch(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.WildAnimalSpawnerTick))]
internal static class Patch_WildAnimalSpawner_Tick_RimRunnerIsolatedMap
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "map")]
    private static extern ref Map SpawnerMap(WildAnimalSpawner spawner);

    private static bool Prefix(WildAnimalSpawner __instance) =>
        SpawnerMap(__instance)?.generatorDef?.defName != RimRunnerIsolatedMap.GeneratorDefName;
}

[HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.WildPlantSpawnerTick))]
internal static class Patch_WildPlantSpawner_Tick_RimRunnerIsolatedMap
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "map")]
    private static extern ref Map SpawnerMap(WildPlantSpawner spawner);

    private static bool Prefix(WildPlantSpawner __instance) =>
        SpawnerMap(__instance)?.generatorDef?.defName != RimRunnerIsolatedMap.GeneratorDefName;
}

[HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Setter)]
internal static class Patch_Game_CurrentMap_Set_RimRunnerIsolatedMap
{
    private static void Prefix(Map value) => RimRunnerIsolatedMap.NoteCurrentMapWrite(value);
}

[HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
internal static class Patch_Map_MapPreTick_RimRunnerIsolatedMap
{
    private static bool Prefix(Map __instance)
    {
        if (RimRunnerIsolatedMap.IsFrozenMap(__instance))
        {
            return false;
        }

        RimRunnerMapTicks.Count(__instance);
        return true;
    }
}

[HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
internal static class Patch_Map_MapPostTick_RimRunnerIsolatedMap
{
    private static bool Prefix(Map __instance) => !RimRunnerIsolatedMap.IsFrozenMap(__instance);
}

[HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
internal static class Patch_Thing_DoTick_RimRunnerIsolatedMap
{
    private static bool Prefix(Thing __instance) => !RimRunnerIsolatedMap.IsFrozen(__instance);
}

[HarmonyPatch(typeof(MemoryThoughtHandler), nameof(MemoryThoughtHandler.TryGainMemory), [typeof(Thought_Memory), typeof(Pawn)])]
internal static class Patch_MemoryThoughtHandler_TryGainMemory_RimRunnerIsolatedMap
{
    private static bool Prefix(MemoryThoughtHandler __instance) => __instance.pawn == null || !RimRunnerIsolatedMap.IsFrozen(__instance.pawn);
}
