namespace RimRunner;

internal sealed class RimRunnerMapRestore
{
    internal const string ProductComponentReason =
        "被测产品组件的存档状态在收尾时与开始执行前不匹配";

    internal const string GlobalComponentReason =
        "世界与整局层不回档：差会带进其后的用例，同一元素之后的写入者不会重复点名";

    internal const string MapComponentReason = "这张图回载基线档或弃用，差不带进下一条用例";

    private readonly Dictionary<int, int> filthOriginals = [];
    private readonly Dictionary<int, int> hitPointOriginals = [];
    private readonly Dictionary<int, (TerrainDef? top, TerrainDef? under, TerrainDef? foundation, TerrainDef? temp)> terrainOriginals = [];
    private readonly Dictionary<int, RoofDef?> roofOriginals = [];
    private readonly Dictionary<int, bool> pollutionOriginals = [];
    private readonly Dictionary<(int area, int cell), bool> areaOriginals = [];
    private readonly HashSet<int> createdAreas = [];
    private readonly HashSet<int> createdZones = [];
    private readonly HashSet<string> createdDesignations = [];
    private readonly int mapId;
    private RimRunnerMapImprint start;
    private RimRunnerMapImprint? startAtSave;
    private RimRunnerMapImprint? atSave;
    private readonly RimRunnerComponentState? components;
    private readonly RimRunnerRunWorldPrint? worldPrint;
    private Map? map;

    internal readonly List<string> Members = [];

    internal readonly RimRunnerCleanupScope ScenarioRestores = new();

    internal string BatchLabel => "屏障批（" + string.Join("、", Members) + "）";

    internal string GapLabel => BatchLabel + "收尾时的主图还原";

    internal IDisposable EnterGap() => RimRunnerLogMonitor.GapSegment(GapLabel);

    internal readonly List<string> ComponentNotes = [];

    internal readonly List<RimRunnerComponentState.Drift> ComponentDrifts = [];

    internal readonly List<string> WorldNotes = [];

    internal readonly List<RimRunnerRunWorldPrint.WorldNoteEntry> WorldNoteEntries = [];

    internal bool ComparesWorld => worldPrint != null;

    internal bool WorldCompared { get; private set; }

    internal RimRunnerMapRestore(Map startMap, RimRunnerComponentScope componentScope, bool compareWorld = false)
    {
        mapId = startMap.uniqueID;
        start = RimRunnerMapImprint.Take(startMap);
        if (componentScope != RimRunnerComponentScope.None)
        {
            components = new RimRunnerComponentState(startMap, componentScope == RimRunnerComponentScope.MapAndGlobal);
        }

        if (componentScope == RimRunnerComponentScope.MapAndGlobal && compareWorld && RimRunnerTraceTier.On)
        {
            worldPrint = new RimRunnerRunWorldPrint();
        }
    }

    internal RimRunnerMapRestore(Map startMap, RimRunnerComponentState components, bool compareWorld = false,
        Func<IReadOnlyCollection<int>, Dictionary<string, string>>? worldCapture = null)
    {
        mapId = startMap.uniqueID;
        start = RimRunnerMapImprint.Take(startMap);
        this.components = components;
        if (compareWorld)
        {
            worldPrint = new RimRunnerRunWorldPrint(worldCapture);
        }
    }

    internal void NoteCheckpointSave()
    {
        Map? current = LiveMap();
        if (current != null)
        {
            startAtSave = start;
            atSave = RimRunnerMapImprint.Take(current);
            components?.NoteCheckpointSave();
        }
    }

    internal void NoteCheckpointLoad()
    {
        Map? current = LiveMap();
        if (current != null && atSave != null)
        {
            start = RimRunnerMapImprint.Rebase(startAtSave!, atSave, RimRunnerMapImprint.Take(current));
            components?.NoteCheckpointLoad();
        }
    }

    private Map? LiveMap() => Find.Maps.FirstOrDefault(candidate => candidate.uniqueID == mapId && !candidate.Disposed);

    internal List<Unrestorable> Close(Func<Thing, bool> held)
    {
        List<Unrestorable> unrestorable = [];
        map = LiveMap();
        if (map == null)
        {
            return unrestorable;
        }

        unrestorable.AddRange(CollectLeftovers(held));
        RimRunnerMapImprint now = RimRunnerMapImprint.Take(map);
        RecordOriginals(now);
        unrestorable.AddRange(Irreversible(now));
        unrestorable.AddRange(Restore());
        now = RimRunnerMapImprint.Take(map);
        unrestorable.AddRange(Residual(now));
        unrestorable.AddRange(LostThings(now));
        ClearMotes();
        if (components != null)
        {
            CloseComponents(unrestorable);
        }

        if (worldPrint != null)
        {
            foreach (RimRunnerRunWorldPrint.WorldNoteEntry entry in worldPrint.Close())
            {
                WorldNoteEntries.Add(entry);
                WorldNotes.Add(entry.Text);
            }

            WorldCompared = true;
        }

        return unrestorable;
    }

    private void CloseComponents(List<Unrestorable> unrestorable)
    {
        string batch = Members.Count > 1 ? "（屏障批 " + string.Join("、", Members) + "）" : "";
        foreach (RimRunnerComponentState.Drift drift in components!.Close())
        {
            ComponentDrifts.Add(drift);
            if (drift.Product)
            {
                RimRunnerSentinel.Warn(RimRunnerWarningKey.ProductComponentDrift,
                    ProductComponentReason + "。" + (drift.OnMap ? MapComponentReason : GlobalComponentReason), drift + batch);
            }

            if (drift.OnMap)
            {
                unrestorable.Add(new Unrestorable(UnrestorableKind.Component, 1, drift.ToString()));
            }
            else if (!drift.Product && RimRunnerSaveProbe.Armed && drift.FilledElements != null)
            {
                RimRunnerSaveProbe.NoteFirstFill(drift.TypeName, drift.FilledElements);
            }
            else if (!drift.Product)
            {
                ComponentNotes.Add(drift.ToString());
            }
        }
    }

    private bool ArrivedSinceStart(Thing thing) => !start.Things.ContainsKey(thing.thingIDNumber);

    private static bool Collectible(Thing thing) =>
        thing.def.category != ThingCategory.Plant && thing.def.category != ThingCategory.Mote && thing.def.destroyable;

    private List<Unrestorable> CollectLeftovers(Func<Thing, bool> held)
    {
        List<Unrestorable> failures = [];
        for (int pass = 0; pass < 3; pass++)
        {
            List<Thing> mine = map!.listerThings.AllThings.Where(thing => ArrivedSinceStart(thing) && Collectible(thing))
                .OrderBy(thing => thing is Pawn ? 0 : thing is Corpse ? 1 : 2)
                .ToList();
            bool removedAny = false;
            foreach (Thing thing in mine)
            {
                if (thing.Destroyed || !thing.Spawned)
                {
                    continue;
                }

                if (held(thing))
                {
                    continue;
                }

                try
                {
                    using (RimRunnerLogMonitor.GapSubject(RimRunnerEnvironmentSweep.DescribeSubject(thing, (thing as Corpse)?.InnerPawn)))
                    {
                        Remove(thing);
                    }

                    removedAny = true;
                }
                catch (Exception exception)
                {
                    failures.Add(new Unrestorable(UnrestorableKind.CleanupFailed, 1,
                        thing.LabelShort + "（" + thing.ThingID + "）：" + exception.Message));
                }
            }

            if (!removedAny)
            {
                break;
            }
        }

        return failures;
    }

    private void ClearMotes()
    {
        List<Thing> motes = [];
        foreach (IntVec3 cell in map!.AllCells)
        {
            foreach (Thing thing in map.thingGrid.ThingsListAtFast(cell))
            {
                if (thing.def.category == ThingCategory.Mote)
                {
                    motes.Add(thing);
                }
            }
        }

        foreach (Thing mote in motes)
        {
            if (!mote.Destroyed)
            {
                mote.Destroy();
            }
        }
    }

    private void Remove(Thing thing)
    {
        switch (thing)
        {
            case Pawn pawn:
                RimRunnerPawns.Cleanup(pawn);
                return;
            case Corpse corpse:
                RimRunnerEnvironmentSweep.RecycleCorpses([corpse], _ => false);
                return;
        }

        if (thing.Faction != Faction.OfPlayer && thing.TryGetComp<CompSpawnerPawn>() != null)
        {
            RimRunnerEnvironmentSweep.DestroyPawnSpawners(map!, [thing], _ => false);
            return;
        }

        thing.Destroy();
    }

    private void RecordOriginals(RimRunnerMapImprint now)
    {
        if (now.CellCount != start.CellCount)
        {
            return;
        }

        for (int index = 0; index < start.CellCount; index++)
        {
            if (start.Top[index] != now.Top[index] || start.Under[index] != now.Under[index]
                                                   || start.Foundation[index] != now.Foundation[index]
                                                   || start.Temp[index] != now.Temp[index])
            {
                terrainOriginals[index] = (start.Top[index], start.Under[index], start.Foundation[index], start.Temp[index]);
            }

            if (start.Roof[index] != now.Roof[index])
            {
                roofOriginals[index] = start.Roof[index];
            }

            if (start.Pollution != null && now.Pollution != null && start.Pollution[index] != now.Pollution[index])
            {
                pollutionOriginals[index] = start.Pollution[index];
            }
        }

        foreach (KeyValuePair<int, (string label, bool[] cells)> area in now.Areas)
        {
            if (!start.Areas.TryGetValue(area.Key, out var before))
            {
                createdAreas.Add(area.Key);
                continue;
            }

            for (int index = 0; index < start.CellCount; index++)
            {
                if (before.cells[index] != area.Value.cells[index])
                {
                    areaOriginals[(area.Key, index)] = before.cells[index];
                }
            }
        }

        createdZones.UnionWith(now.Zones.Keys.Where(id => !start.Zones.ContainsKey(id)));
        createdDesignations.UnionWith(now.Designations.Where(key => !start.Designations.Contains(key)));
        foreach (KeyValuePair<int, (ThingDef def, IntVec3 cell, int amount)> thing in start.Things)
        {
            if (!now.Things.TryGetValue(thing.Key, out var after))
            {
                continue;
            }

            if (thing.Value.def.category == ThingCategory.Filth && after.amount != thing.Value.amount)
            {
                filthOriginals[thing.Key] = thing.Value.amount;
            }

            if (start.BuildingHitPoints.TryGetValue(thing.Key, out int hp)
                && now.BuildingHitPoints.TryGetValue(thing.Key, out int hpNow) && hp != hpNow)
            {
                hitPointOriginals[thing.Key] = hp;
            }
        }
    }

    private List<Unrestorable> Irreversible(RimRunnerMapImprint now)
    {
        List<Unrestorable> lines = [];
        if (now.CellCount == start.CellCount)
        {
            int revealed = 0;
            for (int index = 0; index < start.CellCount; index++)
            {
                if (start.Fog[index] && !now.Fog[index])
                {
                    revealed++;
                }
            }

            if (revealed > 0)
            {
                lines.Add(new Unrestorable(UnrestorableKind.FogRevealed, revealed, ""));
            }
        }

        foreach (KeyValuePair<int, (string label, bool[] cells)> area in start.Areas.Where(pair => !now.Areas.ContainsKey(pair.Key)))
        {
            lines.Add(new Unrestorable(UnrestorableKind.AreaRemoved, 1, area.Value.label));
        }

        foreach (KeyValuePair<int, (string label, HashSet<int> cells)> zone in start.Zones)
        {
            if (!now.Zones.TryGetValue(zone.Key, out var after))
            {
                lines.Add(new Unrestorable(UnrestorableKind.ZoneRemoved, 1, zone.Value.label));
            }
            else if (!zone.Value.cells.SetEquals(after.cells))
            {
                lines.Add(new Unrestorable(UnrestorableKind.ZoneCellsChanged, 1,
                    zone.Value.label + " 的格已改变（" + zone.Value.cells.Count + "→" + after.cells.Count + "）"));
            }
        }

        List<string> lost = start.Designations.Where(key => !now.Designations.Contains(key)).ToList();
        if (lost.Count > 0)
        {
            lines.Add(new Unrestorable(UnrestorableKind.DesignationMissing, lost.Count, string.Join("、", lost)));
        }

        return lines;
    }

    private List<Unrestorable> LostThings(RimRunnerMapImprint now)
    {
        List<string> lost = [];
        List<string> changed = [];
        foreach (KeyValuePair<int, (ThingDef def, IntVec3 cell, int amount)> thing in start.Things)
        {
            ThingCategory category = thing.Value.def.category;
            if (category is ThingCategory.Plant or ThingCategory.Filth
                or ThingCategory.Pawn or ThingCategory.Mote)
            {
                continue;
            }

            List<Thing> instances = start.Instances[thing.Key];
            if (!now.Things.TryGetValue(thing.Key, out var after))
            {
                if (RimRunnerNaturalExpiry.Gone(instances))
                {
                    continue;
                }

                lost.Add(RimRunnerMapImprint.Describe(thing.Value));
            }
            else if (after != thing.Value)
            {
                if (after.def == thing.Value.def && after.cell == thing.Value.cell
                                                 && after.amount == thing.Value.amount - RimRunnerNaturalExpiry.Dissolved(instances))
                {
                    continue;
                }

                changed.Add(RimRunnerMapImprint.Describe(thing.Value) + "→" + RimRunnerMapImprint.Describe(after));
            }
        }

        List<Unrestorable> lines = [];
        if (lost.Count > 0)
        {
            lines.Add(new Unrestorable(UnrestorableKind.Missing, lost.Count, string.Join("、", lost)));
        }

        if (changed.Count > 0)
        {
            lines.Add(new Unrestorable(UnrestorableKind.Moved, changed.Count, string.Join("、", changed)));
        }

        return lines;
    }

    private List<Unrestorable> Restore()
    {
        List<Unrestorable> failures = [];
        void Attempt(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failures.Add(new Unrestorable(UnrestorableKind.WriteFailed, 1, what + "失败：" + exception.Message));
            }
        }

        foreach (Zone zone in map!.zoneManager.AllZones.Where(zone => createdZones.Contains(zone.ID)).ToList())
        {
            Attempt("区 " + zone.label, () => zone.Delete(playSound: false));
        }

        foreach (Designation designation in map.designationManager.AllDesignations
                     .Where(designation => createdDesignations.Contains(RimRunnerMapImprint.DesignationKey(designation))).ToList())
        {
            Attempt("标记 " + designation.def.defName, designation.Delete);
        }

        foreach (Area area in map.areaManager.AllAreas.Where(area => createdAreas.Contains(area.ID)).ToList())
        {
            Attempt("区域 " + area.Label, area.Delete);
        }

        Dictionary<int, Area> areas = map.areaManager.AllAreas.ToDictionary(area => area.ID);
        foreach (KeyValuePair<(int area, int cell), bool> original in areaOriginals)
        {
            if (areas.TryGetValue(original.Key.area, out Area? area) && area[original.Key.cell] != original.Value)
            {
                area[original.Key.cell] = original.Value;
            }
        }

        foreach (KeyValuePair<int, (TerrainDef? top, TerrainDef? under, TerrainDef? foundation, TerrainDef? temp)> original in terrainOriginals)
        {
            IntVec3 cell = map.cellIndices.IndexToCell(original.Key);
            Attempt("地形 " + cell, () => RestoreTerrain(cell, original.Key, original.Value));
        }

        foreach (KeyValuePair<int, RoofDef?> original in roofOriginals)
        {
            map.roofGrid.SetRoof(map.cellIndices.IndexToCell(original.Key), original.Value);
        }

        foreach (KeyValuePair<int, bool> original in pollutionOriginals)
        {
            map.pollutionGrid.SetPolluted(map.cellIndices.IndexToCell(original.Key), original.Value, silent: true);
        }

        map.roofCollapseBuffer.Clear();
        foreach (IntVec3 cell in start.CollapseMarked)
        {
            map.roofCollapseBuffer.MarkToCollapse(cell);
        }

        foreach (Thing thing in map.listerThings.AllThings.ToList())
        {
            if (thing is Filth filth && filthOriginals.TryGetValue(thing.thingIDNumber, out int thickness))
            {
                filth.thickness = thickness;
            }

            if (hitPointOriginals.TryGetValue(thing.thingIDNumber, out int hitPoints))
            {
                thing.HitPoints = hitPoints;
            }
        }

        return failures;
    }

    private void RestoreTerrain(IntVec3 cell, int index,
        (TerrainDef? top, TerrainDef? under, TerrainDef? foundation, TerrainDef? temp) original)
    {
        TerrainGrid grid = map!.terrainGrid;
        if (grid.TempTerrainAt(index) != original.temp && grid.TempTerrainAt(index) != null)
        {
            grid.RemoveTempTerrain(cell, doLeavings: false, preventDestroyEffects: true);
        }

        if (grid.FoundationAt(index) != original.foundation && grid.FoundationAt(index) != null)
        {
            grid.RemoveFoundation(cell, doLeavings: false);
        }

        if (grid.topGrid[index] != original.top && original.top != null)
        {
            grid.SetTerrain(cell, original.top);
        }

        if (grid.UnderTerrainAt(index) != original.under && original.under != null)
        {
            grid.SetUnderTerrain(cell, original.under);
        }

        if (grid.FoundationAt(index) != original.foundation && original.foundation != null)
        {
            grid.SetFoundation(cell, original.foundation);
        }

        if (grid.TempTerrainAt(index) != original.temp && original.temp != null)
        {
            grid.SetTempTerrain(cell, original.temp);
        }
    }

    private List<Unrestorable> Residual(RimRunnerMapImprint now)
    {
        List<Unrestorable> lines = [];
        if (now.CellCount != start.CellCount)
        {
            return lines;
        }

        int terrain = terrainOriginals.Count(pair => (now.Top[pair.Key], now.Under[pair.Key], now.Foundation[pair.Key], now.Temp[pair.Key]) != pair.Value);
        int roof = roofOriginals.Count(pair => now.Roof[pair.Key] != pair.Value);
        int pollution = now.Pollution == null ? 0 : pollutionOriginals.Count(pair => now.Pollution[pair.Key] != pair.Value);
        int area = areaOriginals.Count(pair => now.Areas.TryGetValue(pair.Key.area, out var after) && after.cells[pair.Key.cell] != pair.Value);
        int zones = now.Zones.Keys.Count(createdZones.Contains);
        int areasLeft = now.Areas.Keys.Count(createdAreas.Contains);
        int designations = now.Designations.Count(createdDesignations.Contains);
        void Add(string what, int count)
        {
            if (count > 0)
            {
                lines.Add(new Unrestorable(UnrestorableKind.Residual, count, what));
            }
        }

        Add("地形", terrain);
        Add("屋顶", roof);
        Add("污染", pollution);
        Add("区域格", area);
        Add("新建的区", zones);
        Add("新建的区域", areasLeft);
        Add("新建的标记", designations);
        return lines;
    }

    internal sealed class Unrestorable(UnrestorableKind kind, int count, string detail)
    {
        internal readonly UnrestorableKind Kind = kind;

        internal readonly int Count = count;

        internal readonly string Detail = detail;

        internal string Text => Kind switch
        {
            UnrestorableKind.CleanupFailed => "无法清理 " + Detail,
            UnrestorableKind.FogRevealed => "揭开 " + Count + " 格雾",
            UnrestorableKind.AreaRemoved => "区域 " + Detail + " 已删除",
            UnrestorableKind.ZoneRemoved => "区 " + Detail + " 已删除",
            UnrestorableKind.ZoneCellsChanged => "区 " + Detail,
            UnrestorableKind.DesignationMissing => "基线标记缺少 " + Count + " 个：" + Detail,
            UnrestorableKind.Missing => "基线物件缺少 " + Count + " 件：" + Detail,
            UnrestorableKind.Moved => "基线物件挪位或改量 " + Count + " 件：" + Detail,
            UnrestorableKind.WriteFailed => "回写" + Detail,
            UnrestorableKind.Residual => Detail + " " + Count + " 处回写后仍不匹配",
            UnrestorableKind.Component => Detail,
        };
    }

    internal enum UnrestorableKind
    {
        CleanupFailed,
        FogRevealed,
        AreaRemoved,
        ZoneRemoved,
        ZoneCellsChanged,
        DesignationMissing,
        Missing,
        Moved,
        WriteFailed,
        Residual,
        Component,
    }
}

internal enum RimRunnerComponentScope
{
    None,
    Map,
    MapAndGlobal,
}
