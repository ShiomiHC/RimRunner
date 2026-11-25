using System.Diagnostics;
using System.Runtime.CompilerServices;
using RimWorld.Planet;

namespace RimRunner;

internal static class RimRunnerParallelSite
{
    internal const string DefName = "RimRunner_ParallelSiteLayer";

    internal static bool ForceForSelfTest;

    internal static long LastRegisterMs { get; private set; } = -1;
    internal static int LastRegisterTiles { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "surfaceLayer")]
    private static extern ref ScenPart_PlanetLayer SurfaceLayerPart(Scenario scenario);

    internal static bool IsOn(PlanetTile tile) => tile.Valid && tile.Layer?.Def?.defName == DefName;

    internal static List<PlanetLayer> Registered() =>
        Current.Game?.World == null
            ? []
            : Find.WorldGrid.PlanetLayers.Values.Where(layer => layer.Def?.defName == DefName)
                .OrderBy(layer => layer.LayerID).ToList();

    internal static PlanetTile TileFor(PlanetTile mainTile, BiomeDef biome)
    {
        foreach (PlanetLayer layer in Registered())
        {
            if (RimRunnerIsolatedMap.SearchFrom(new PlanetTile(mainTile.tileId, layer), biome, includeStart: true) is { } tile)
            {
                return tile;
            }
        }

        PlanetTile fresh = new(mainTile.tileId, Register());
        return RimRunnerIsolatedMap.SearchFrom(fresh, biome, includeStart: true)
               ?? throw new InvalidOperationException(RimRunnerLog.Prefix + " 主殖民地 " + mainTile
                   + " 周围与新注册的替补层上都找不到能立独立小图的 tile：须同 biome（" + biome.defName
                   + "）、可建基地、可通行、无世界对象、无 tile 变体、无污染");
    }

    private static PlanetLayer Register()
    {
        Stopwatch timer = Stopwatch.StartNew();
        PlanetLayerDef def = DefDatabase<PlanetLayerDef>.GetNamed(DefName);
        SurfaceLayer surface = Find.WorldGrid.Surface;
        PlanetLayer layer = Find.WorldGrid.RegisterPlanetLayer(def, SurfaceLayerPart(Find.Scenario).Settings, null, null,
            surface.ViewCenter, surface.ViewAngle);
        layer.Tiles.Clear();
        for (int index = 0; index < layer.TilesCount; index++)
        {
            SurfaceTile source = surface[index];
            layer.Tiles.Add(new SurfaceTile(new PlanetTile(index, layer))
            {
                PrimaryBiome = source.PrimaryBiome,
                elevation = source.elevation,
                hilliness = source.hilliness,
                temperature = source.temperature,
                rainfall = source.rainfall,
                swampiness = source.swampiness
            });
        }

        layer.Standardize();
        LastRegisterMs = timer.ElapsedMilliseconds;
        LastRegisterTiles = layer.TilesCount;
        return layer;
    }

    internal static int TearDownAll()
    {
        List<PlanetLayer> layers = Registered();
        if (layers.Count == 0)
        {
            return 0;
        }

        List<Exception> failures = [];
        List<Map> maps = Find.Maps.Where(map => layers.Contains(map.Tile.Layer)).ToList();
        if (maps.Count > 0)
        {
            RimRunnerTimeDriver.AdvanceBurst(1);
        }

        foreach (Map map in maps)
        {
            Try(failures, () => RimRunnerMapLifecyclePatches.RunAsEngineTeardown(
                () => Current.Game.DeinitAndRemoveMap(map, notifyPlayer: false)));
        }

        foreach (PlanetLayer layer in layers)
        {
            foreach (WorldObject worldObject in Find.WorldObjects.AllWorldObjectsOnLayer(layer).ToList())
            {
                Try(failures, () =>
                {
                    if (!worldObject.Destroyed)
                    {
                        worldObject.Destroy();
                    }
                });
            }

            Try(failures, () => Find.WorldGrid.RemovePlanetLayer(layer));
        }

        Try(failures, Find.World.tileTemperatures.ClearCaches);
        if (failures.Count > 0)
        {
            throw new AggregateException(RimRunnerLog.Prefix + " 替补层拆除失败", failures);
        }

        return layers.Count;
    }

    private static void Try(List<Exception> failures, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
