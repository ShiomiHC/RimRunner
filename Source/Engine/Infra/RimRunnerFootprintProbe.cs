using System.Globalization;

namespace RimRunner;

internal static class RimRunnerFootprintProbe
{
    internal static readonly bool Enabled = RimRunnerTraceTier.On;

    private sealed class Footprint
    {
        internal required string CaseId;
        internal IntVec3 Size;
        internal IntVec3 Center;
        internal bool Any;
        internal int MinX;
        internal int MaxX;
        internal int MinZ;
        internal int MaxZ;
        internal int Spawns;
        internal readonly HashSet<int> Pawns = [];
        internal readonly HashSet<int> Own = [];
        internal int FarReach = -1;
        internal string? Far;
        internal int Handed;
        internal int Cohabit;
        internal Map? Map;
    }

    private static readonly List<Footprint> footprints = [];
    private static readonly Dictionary<string, Footprint> byCase = new(StringComparer.Ordinal);
    private static readonly List<Explosion> explosions = [];
    private static Footprint? armed;
    private static Map? armedMap;
    private static readonly HashSet<int> ambientPawns = [];
    private static readonly HashSet<int> foreignPawns = [];
    private static readonly Dictionary<int, Footprint> owners = [];
    private static Thing? ticking;
    private static int wildSpawnDepth;

    internal static void ResetRun()
    {
        footprints.Clear();
        byCase.Clear();
        explosions.Clear();
        ambientPawns.Clear();
        foreignPawns.Clear();
        owners.Clear();
        ticking = null;
        armed = null;
        armedMap = null;
    }

    internal static Thing? EnterTick(Thing thing)
    {
        Thing? outer = ticking;
        ticking = thing;
        return outer;
    }

    internal static void ExitTick(Thing? outer)
    {
        ticking = outer;
    }

    internal static void EnterWildSpawn()
    {
        wildSpawnDepth++;
    }

    internal static void ExitWildSpawn()
    {
        wildSpawnDepth--;
    }

    internal static void Arm(string caseId, Map? map)
    {
        if (!Enabled || map == null)
        {
            return;
        }

        if (!byCase.TryGetValue(caseId, out Footprint? footprint))
        {
            footprint = new Footprint { CaseId = caseId, Size = map.Size, Center = map.Center };
            byCase.Add(caseId, footprint);
            footprints.Add(footprint);
        }

        armed = footprint;
        armedMap = map;
        footprint.Map = map;
        explosions.Clear();
        foreignPawns.Clear();
        IReadOnlyList<Pawn> present = map.mapPawns.AllPawnsSpawned;
        for (int i = 0; i < present.Count; i++)
        {
            if (!footprint.Own.Contains(present[i].thingIDNumber))
            {
                foreignPawns.Add(present[i].thingIDNumber);
            }
        }

        int cohabit = 0;
        List<Thing> things = map.listerThings.AllThings;
        for (int i = 0; i < things.Count; i++)
        {
            if (owners.TryGetValue(things[i].thingIDNumber, out Footprint? owner) && owner != footprint)
            {
                cohabit++;
            }
        }

        footprint.Cohabit = Math.Max(footprint.Cohabit, cohabit);
        SampleTick();
    }

    internal static void Disarm()
    {
        if (armed == null)
        {
            return;
        }

        try
        {
            SampleTick();
            for (int i = 0; i < explosions.Count; i++)
            {
                Explosion explosion = explosions[i];
                int radius = Mathf.CeilToInt(explosion.radius);
                IntVec3 at = explosion.Position;
                Include(armed, new IntVec3(at.x - radius, 0, at.z - radius), new IntVec3(at.x + radius, 0, at.z + radius),
                    "explosion:" + explosion.damType?.defName + "@r" + explosion.radius.ToString("0.#", CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            explosions.Clear();
            foreignPawns.Clear();
            armed = null;
            armedMap = null;
        }
    }

    internal static void OnSpawn(Thing? thing)
    {
        if (thing == null)
        {
            return;
        }

        if (wildSpawnDepth > 0)
        {
            NoteAmbient(thing);
            return;
        }

        if (!thing.Spawned || (thing is Pawn spawnedPawn && ambientPawns.Contains(spawnedPawn.thingIDNumber)))
        {
            return;
        }

        Footprint? target = armed;
        if (ticking != null && owners.TryGetValue(ticking.thingIDNumber, out Footprint? owner) && owner != armed)
        {
            target = owner;
            if (armed != null && thing.Map == armedMap)
            {
                armed.Handed++;
            }
        }

        if (target == null || thing.Map != (target == armed ? armedMap : target.Map)
            || thing.def.category == ThingCategory.Mote)
        {
            return;
        }

        owners[thing.thingIDNumber] = target;
        target.Spawns++;
        if (thing is Pawn ownPawn)
        {
            target.Own.Add(ownPawn.thingIDNumber);
            if (target == armed)
            {
                foreignPawns.Remove(ownPawn.thingIDNumber);
            }
            else if (armed != null)
            {
                foreignPawns.Add(ownPawn.thingIDNumber);
            }
        }

        if (thing is Explosion explosion)
        {
            if (target == armed)
            {
                explosions.Add(explosion);
                return;
            }
        }

        CellRect rect = thing.OccupiedRect();
        Include(target, new IntVec3(rect.minX, 0, rect.minZ), new IntVec3(rect.maxX, 0, rect.maxZ),
            (thing is Pawn ? "spawn-pawn:" : "spawn:") + thing.def.defName);
    }

    internal static void SampleTick()
    {
        if (armed == null)
        {
            return;
        }

        IReadOnlyList<Pawn> pawns = armedMap!.mapPawns.AllPawnsSpawned;
        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn pawn = pawns[i];
            if (ambientPawns.Contains(pawn.thingIDNumber) || foreignPawns.Contains(pawn.thingIDNumber))
            {
                continue;
            }

            armed.Pawns.Add(pawn.thingIDNumber);
            IntVec3 at = pawn.Position;
            Include(armed, at, at, null, pawn);
        }
    }

    internal static List<string> Lines()
    {
        List<string> lines = new(footprints.Count);
        for (int i = 0; i < footprints.Count; i++)
        {
            Footprint footprint = footprints[i];
            string head = footprint.CaseId + " map=" + footprint.Size.x + "x" + footprint.Size.z;
            if (!footprint.Any)
            {
                lines.Add(head + " none");
                continue;
            }

            int edge = Math.Min(Math.Min(footprint.MinX, footprint.MinZ),
                Math.Min(footprint.Size.x - 1 - footprint.MaxX, footprint.Size.z - 1 - footprint.MaxZ));
            lines.Add(head
                      + " W=" + (footprint.Center.x - footprint.MinX)
                      + " E=" + (footprint.MaxX - footprint.Center.x)
                      + " S=" + (footprint.Center.z - footprint.MinZ)
                      + " N=" + (footprint.MaxZ - footprint.Center.z)
                      + " edge=" + edge
                      + " spawns=" + footprint.Spawns
                      + " pawns=" + footprint.Pawns.Count
                      + " handed=" + footprint.Handed
                      + " cohab=" + footprint.Cohabit
                      + " far=" + footprint.Far);
        }

        return lines;
    }

    private static void NoteAmbient(Thing thing)
    {
        if (thing is Pawn pawn)
        {
            ambientPawns.Add(pawn.thingIDNumber);
            return;
        }

        if (thing is Skyfaller skyfaller)
        {
            foreach (Thing inner in skyfaller.innerContainer)
            {
                if (inner is Pawn innerPawn)
                {
                    ambientPawns.Add(innerPawn.thingIDNumber);
                }
            }
        }
    }

    private static void Include(Footprint footprint, IntVec3 min, IntVec3 max, string? what, Pawn? pawn = null)
    {
        if (!footprint.Any)
        {
            footprint.Any = true;
            footprint.MinX = min.x;
            footprint.MaxX = max.x;
            footprint.MinZ = min.z;
            footprint.MaxZ = max.z;
        }
        else
        {
            footprint.MinX = Math.Min(footprint.MinX, min.x);
            footprint.MaxX = Math.Max(footprint.MaxX, max.x);
            footprint.MinZ = Math.Min(footprint.MinZ, min.z);
            footprint.MaxZ = Math.Max(footprint.MaxZ, max.z);
        }

        int reach = Math.Max(
            Math.Max(footprint.Center.x - min.x, max.x - footprint.Center.x),
            Math.Max(footprint.Center.z - min.z, max.z - footprint.Center.z));
        if (reach > footprint.FarReach)
        {
            footprint.FarReach = reach;
            footprint.Far = (what ?? "pawn:" + pawn!.def.defName) + "@" + min.x + "," + min.z
                            + (min == max ? "" : ".." + max.x + "," + max.z);
        }
    }
}

[HarmonyPatch]
internal static class Patch_WildAnimalSpawner_RimRunnerFootprint
{
    private static MethodBase? target;

    private static bool Prepare()
    {
        if (!RimRunnerFootprintProbe.Enabled)
        {
            return false;
        }

        if (target != null)
        {
            return true;
        }

        target = AccessTools.Method(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.SpawnRandomWildAnimalAt),
            [typeof(IntVec3), typeof(bool), typeof(PawnKindDef)]);
        if (target == null)
        {
            RimRunnerEngineFault.Raise("足迹探针挂载失败：无法解析 WildAnimalSpawner.SpawnRandomWildAnimalAt，"
                                          + "野生动物刷新会混进足迹");
            return false;
        }

        return true;
    }

    private static MethodBase TargetMethod()
    {
        return target!;
    }

    private static void Prefix()
    {
        RimRunnerFootprintProbe.EnterWildSpawn();
    }

    private static void Finalizer()
    {
        RimRunnerFootprintProbe.ExitWildSpawn();
    }
}

[HarmonyPatch]
internal static class Patch_WildPlantSpawner_RimRunnerFootprint
{
    private static MethodBase? target;

    private static bool Prepare()
    {
        if (!RimRunnerFootprintProbe.Enabled)
        {
            return false;
        }

        if (target != null)
        {
            return true;
        }

        target = AccessTools.Method(typeof(WildPlantSpawner), nameof(WildPlantSpawner.CheckSpawnWildPlantAt),
            [typeof(IntVec3), typeof(float), typeof(float), typeof(bool)]);
        if (target == null)
        {
            RimRunnerEngineFault.Raise("足迹探针挂载失败：无法解析 WildPlantSpawner.CheckSpawnWildPlantAt，"
                                          + "环境生成的植物会混进足迹");
            return false;
        }

        return true;
    }

    private static MethodBase TargetMethod()
    {
        return target!;
    }

    private static void Prefix()
    {
        RimRunnerFootprintProbe.EnterWildSpawn();
    }

    private static void Finalizer()
    {
        RimRunnerFootprintProbe.ExitWildSpawn();
    }
}

[HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
internal static class Patch_Thing_DoTick_RimRunnerFootprint
{
    private static bool Prepare()
    {
        return RimRunnerFootprintProbe.Enabled;
    }

    private static void Prefix(Thing __instance, out Thing? __state)
    {
        __state = RimRunnerFootprintProbe.EnterTick(__instance);
    }

    private static void Finalizer(Thing? __state)
    {
        RimRunnerFootprintProbe.ExitTick(__state);
    }
}
