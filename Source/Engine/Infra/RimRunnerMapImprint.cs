namespace RimRunner;

internal sealed class RimRunnerMapImprint
{
    internal readonly Dictionary<int, (ThingDef def, IntVec3 cell, int amount)> Things = [];
    internal readonly Dictionary<int, int> BuildingHitPoints = [];
    internal readonly Dictionary<int, List<Thing>> Instances = [];
    internal int CellCount;
    internal int Width;
    internal required TerrainDef?[] Top;
    internal required TerrainDef?[] Under;
    internal required TerrainDef?[] Foundation;
    internal required TerrainDef?[] Temp;
    internal required RoofDef?[] Roof;
    internal required bool[] Fog;
    internal bool[]? Pollution;
    internal readonly Dictionary<int, (string label, bool[] cells)> Areas = [];
    internal readonly Dictionary<int, (string label, HashSet<int> cells)> Zones = [];
    internal readonly HashSet<string> Designations = [];
    internal readonly List<IntVec3> CollapseMarked = [];

    internal static RimRunnerMapImprint Take(Map map)
    {
        int cells = map.cellIndices.NumGridCells;
        bool biotech = ModsConfig.BiotechActive;
        RimRunnerMapImprint imprint = new()
        {
            CellCount = cells,
            Width = map.Size.x,
            Top = new TerrainDef?[cells],
            Under = new TerrainDef?[cells],
            Foundation = new TerrainDef?[cells],
            Temp = new TerrainDef?[cells],
            Roof = new RoofDef?[cells],
            Fog = new bool[cells],
            Pollution = biotech ? new bool[cells] : null
        };
        foreach (Thing thing in map.listerThings.AllThings)
        {
            if (thing.def.category == ThingCategory.Mote)
            {
                continue;
            }

            imprint.Things[thing.thingIDNumber] = thing is Pawn
                ? (thing.def, IntVec3.Invalid, 1)
                : (thing.def, thing.Position, thing is Filth filth ? filth.thickness : thing.stackCount);
            if (thing.def.category == ThingCategory.Building && thing.def.useHitPoints)
            {
                imprint.BuildingHitPoints[thing.thingIDNumber] = thing.HitPoints;
            }

            imprint.Instances[thing.thingIDNumber] = [thing];
        }

        TerrainGrid terrain = map.terrainGrid;
        Array.Copy(terrain.topGrid, imprint.Top, cells);
        Array.Copy(terrain.foundationGrid, imprint.Foundation, cells);
        for (int index = 0; index < cells; index++)
        {
            imprint.Under[index] = terrain.UnderTerrainAt(index);
            imprint.Temp[index] = terrain.TempTerrainAt(index);
            imprint.Roof[index] = map.roofGrid.RoofAt(index);
            imprint.Fog[index] = map.fogGrid.IsFogged(index);
            if (biotech)
            {
                imprint.Pollution![index] = map.pollutionGrid.IsPolluted(map.cellIndices.IndexToCell(index));
            }
        }

        foreach (Area area in map.areaManager.AllAreas)
        {
            bool[] grid = new bool[cells];
            for (int index = 0; index < cells; index++)
            {
                grid[index] = area[index];
            }

            imprint.Areas[area.ID] = (area.Label, grid);
        }

        foreach (Zone zone in map.zoneManager.AllZones)
        {
            imprint.Zones[zone.ID] = (zone.label, new HashSet<int>(zone.Cells.Select(map.cellIndices.CellToIndex)));
        }

        foreach (Designation designation in map.designationManager.AllDesignations)
        {
            imprint.Designations.Add(DesignationKey(designation));
        }

        imprint.CollapseMarked.AddRange(map.roofCollapseBuffer.CellsMarkedToCollapse);
        return imprint;
    }

    internal static RimRunnerMapImprint Rebase(RimRunnerMapImprint start, RimRunnerMapImprint before, RimRunnerMapImprint after)
    {
        RimRunnerMapImprint rebased = start.Clone();
        Dictionary<(ThingDef def, IntVec3 cell, int amount), Queue<int>> unclaimed = after.Things
            .Where(pair => !before.Things.ContainsKey(pair.Key))
            .GroupBy(pair => pair.Value)
            .ToDictionary(group => group.Key, group => new Queue<int>(group.Select(pair => pair.Key)));
        foreach (KeyValuePair<int, (ThingDef def, IntVec3 cell, int amount)> thing in start.Things)
        {
            if (after.Things.ContainsKey(thing.Key))
            {
                rebased.Instances[thing.Key] = rebased.Instances[thing.Key].Union(after.Instances[thing.Key]).ToList();
                continue;
            }

            if (!before.Things.TryGetValue(thing.Key, out var atSave))
            {
                continue;
            }

            rebased.Things.Remove(thing.Key);
            List<Thing> instances = rebased.Instances[thing.Key];
            rebased.Instances.Remove(thing.Key);
            bool hasHitPoints = rebased.BuildingHitPoints.TryGetValue(thing.Key, out int hitPoints);
            rebased.BuildingHitPoints.Remove(thing.Key);
            if (unclaimed.TryGetValue(atSave, out Queue<int>? ids) && ids.Count > 0)
            {
                int id = ids.Dequeue();
                rebased.Things[id] = thing.Value;
                rebased.Instances[id] = instances.Union(after.Instances[id]).ToList();

                if (hasHitPoints)
                {
                    rebased.BuildingHitPoints[id] = hitPoints;
                }
            }
        }

        if (before.CellCount == after.CellCount && start.CellCount == after.CellCount)
        {
            for (int index = 0; index < after.CellCount; index++)
            {
                if (before.Top[index] != after.Top[index])
                {
                    rebased.Top[index] = after.Top[index];
                }

                if (before.Under[index] != after.Under[index])
                {
                    rebased.Under[index] = after.Under[index];
                }

                if (before.Foundation[index] != after.Foundation[index])
                {
                    rebased.Foundation[index] = after.Foundation[index];
                }

                if (before.Temp[index] != after.Temp[index])
                {
                    rebased.Temp[index] = after.Temp[index];
                }

                if (before.Roof[index] != after.Roof[index])
                {
                    rebased.Roof[index] = after.Roof[index];
                }

                if (before.Fog[index] != after.Fog[index])
                {
                    rebased.Fog[index] = after.Fog[index];
                }

                if (rebased.Pollution != null && before.Pollution != null && after.Pollution != null
                    && before.Pollution[index] != after.Pollution[index])
                {
                    rebased.Pollution[index] = after.Pollution[index];
                }
            }
        }

        return rebased;
    }

    private RimRunnerMapImprint Clone()
    {
        RimRunnerMapImprint copy = new()
        {
            CellCount = CellCount,
            Width = Width,
            Top = (TerrainDef?[])Top.Clone(),
            Under = (TerrainDef?[])Under.Clone(),
            Foundation = (TerrainDef?[])Foundation.Clone(),
            Temp = (TerrainDef?[])Temp.Clone(),
            Roof = (RoofDef?[])Roof.Clone(),
            Fog = (bool[])Fog.Clone(),
            Pollution = (bool[]?)Pollution?.Clone()
        };
        foreach (KeyValuePair<int, (ThingDef def, IntVec3 cell, int amount)> thing in Things)
        {
            copy.Things[thing.Key] = thing.Value;
        }

        foreach (KeyValuePair<int, List<Thing>> instances in Instances)
        {
            copy.Instances[instances.Key] = [.. instances.Value];
        }

        foreach (KeyValuePair<int, int> hitPoints in BuildingHitPoints)
        {
            copy.BuildingHitPoints[hitPoints.Key] = hitPoints.Value;
        }

        foreach (KeyValuePair<int, (string label, bool[] cells)> area in Areas)
        {
            copy.Areas[area.Key] = area.Value;
        }

        foreach (KeyValuePair<int, (string label, HashSet<int> cells)> zone in Zones)
        {
            copy.Zones[zone.Key] = zone.Value;
        }

        copy.Designations.UnionWith(Designations);
        copy.CollapseMarked.AddRange(CollapseMarked);
        return copy;
    }

    internal static string DesignationKey(Designation designation)
    {
        LocalTargetInfo target = designation.target;
        return designation.def.defName + "@" + (target.HasThing ? target.Thing.ThingID : target.Cell.ToString());
    }

    internal List<string> DriftTo(RimRunnerMapImprint later)
    {
        List<string> drift = [];
        AddThings(drift, "缺少", Things.Where(pair => !later.Things.ContainsKey(pair.Key))
            .Select(pair => Describe(pair.Value)).ToList());
        AddThings(drift, "多出", later.Things.Where(pair => !Things.ContainsKey(pair.Key))
            .Select(pair => Describe(pair.Value)).ToList());
        AddThings(drift, "已改变", Things
            .Where(pair => later.Things.TryGetValue(pair.Key, out var now) && now != pair.Value)
            .Select(pair => Describe(pair.Value) + "→" + Describe(later.Things[pair.Key])).ToList());
        drift.AddRange(GridDrift(later, _ => true));
        return drift;
    }

    internal List<string> GridDrift(RimRunnerMapImprint later, Func<int, bool> include)
    {
        List<string> drift = [];
        if (later.CellCount != CellCount)
        {
            drift.Add("格数 " + CellCount + "→" + later.CellCount);
            return drift;
        }

        AddCells(drift, "地形", include, index => Top[index] != later.Top[index]
                                                || Under[index] != later.Under[index]
                                                || Foundation[index] != later.Foundation[index]
                                                || Temp[index] != later.Temp[index],
            index => DescribeTerrain(later, index));
        AddCells(drift, "屋顶", include, index => Roof[index] != later.Roof[index],
            index => (Roof[index]?.defName ?? "无") + "→" + (later.Roof[index]?.defName ?? "无"));
        AddCells(drift, "雾", include, index => Fog[index] != later.Fog[index],
            index => (Fog[index] ? "雾" : "明") + "→" + (later.Fog[index] ? "雾" : "明"));
        if (Pollution != null && later.Pollution != null)
        {
            AddCells(drift, "污染", include, index => Pollution[index] != later.Pollution[index],
                index => (Pollution[index] ? "污" : "净") + "→" + (later.Pollution[index] ? "污" : "净"));
        }

        foreach (int id in Areas.Keys.Union(later.Areas.Keys))
        {
            bool hadBefore = Areas.TryGetValue(id, out var before);
            bool hasAfter = later.Areas.TryGetValue(id, out var after);
            if (!hadBefore || !hasAfter)
            {
                drift.Add("区域 " + (hadBefore ? before.label : after.label) + "#" + id + (hadBefore ? " 已删除" : " 新建"));
                continue;
            }

            AddCells(drift, "区域 " + before.label + "#" + id, include, index => before.cells[index] != after.cells[index],
                index => before.cells[index] ? "去" : "增");
        }

        foreach (int id in Zones.Keys.Union(later.Zones.Keys))
        {
            bool hadBefore = Zones.TryGetValue(id, out var before);
            bool hasAfter = later.Zones.TryGetValue(id, out var after);
            if (!hadBefore || !hasAfter)
            {
                drift.Add("区 " + (hadBefore ? before.label : after.label) + (hadBefore ? " 已删除" : " 新建"));
                continue;
            }

            int changed = before.cells.Where(include).Count(index => !after.cells.Contains(index))
                          + after.cells.Where(include).Count(index => !before.cells.Contains(index));
            if (changed > 0)
            {
                drift.Add("区 " + before.label + " " + changed + " 格");
            }
        }

        List<string> lostDesignations = Designations.Where(key => !later.Designations.Contains(key)).ToList();
        List<string> newDesignations = later.Designations.Where(key => !Designations.Contains(key)).ToList();
        AddKeys(drift, "标记缺少", lostDesignations);
        AddKeys(drift, "标记多出", newDesignations);
        if (CollapseMarked.Count != later.CollapseMarked.Count)
        {
            drift.Add("待塌屋顶格 " + CollapseMarked.Count + "→" + later.CollapseMarked.Count);
        }

        return drift;
    }

    internal static string Describe((ThingDef def, IntVec3 cell, int amount) thing)
    {
        return thing.def.defName + (thing.cell.IsValid ? "@" + thing.cell : "") + (thing.amount > 1 ? "×" + thing.amount : "");
    }

    private static void AddThings(List<string> drift, string verb, List<string> described)
    {
        if (described.Count > 0)
        {
            drift.Add(verb + " " + described.Count + " 件：" + string.Join("、", described));
        }
    }

    private static void AddKeys(List<string> drift, string what, List<string> keys)
    {
        if (keys.Count > 0)
        {
            drift.Add(what + " " + keys.Count + " 个：" + string.Join("、", keys));
        }
    }

    private void AddCells(List<string> drift, string layer, Func<int, bool> include, Func<int, bool> differs,
        Func<int, string> describe)
    {
        int count = 0;
        List<string> samples = [];
        for (int index = 0; index < CellCount; index++)
        {
            if (!differs(index) || !include(index))
            {
                continue;
            }

            count++;
            if (samples.Count < 3)
            {
                samples.Add(CellOf(index) + " " + describe(index));
            }
        }

        if (count > 0)
        {
            drift.Add(layer + " " + count + " 格：" + string.Join("、", samples)
                      + (count > samples.Count ? "，其余 " + (count - samples.Count) + " 格未列" : ""));
        }
    }

    private string CellOf(int index) => new IntVec3(index % Width, 0, index / Width).ToString();

    private string DescribeTerrain(RimRunnerMapImprint later, int index)
    {
        List<string> layers = [];
        void Layer(string name, TerrainDef?[] before, TerrainDef?[] after)
        {
            if (before[index] != after[index])
            {
                layers.Add(name + " " + (before[index]?.defName ?? "无") + "→" + (after[index]?.defName ?? "无"));
            }
        }

        Layer("顶层", Top, later.Top);
        Layer("底层", Under, later.Under);
        Layer("地基", Foundation, later.Foundation);
        Layer("临时", Temp, later.Temp);
        return string.Join("，", layers);
    }
}
