using System.Diagnostics;
using System.Runtime.CompilerServices;
using RimWorld.Planet;

namespace RimRunner;

[HarmonyPatch(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Add))]
internal static class Patch_WorldObjectsHolder_Add_RimRunnerArrivalTag
{
    private static bool Prepare() => GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);

    private static void Postfix(WorldObject o)
    {
        try
        {
            if (o is IThingHolder)
            {
                RimRunnerArrivalTag.NoteWorldObject(o);
            }
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("入场打标自身异常：" + exception);
        }
    }
}

[HarmonyPatch]
internal static class Patch_TravellingTransporters_Arrive_RimRunnerArrivalTag
{
    private static MethodBase? target;

    private static bool Prepare()
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            return false;
        }

        if (target != null)
        {
            return true;
        }

        target = AccessTools.Method(typeof(TravellingTransporters), "DoArrivalAction");
        if (target == null)
        {
            RimRunnerEngineFault.Raise("入场打标挂载失败：无法解析 TravellingTransporters.DoArrivalAction。"
                      + "在途运输舱落图时，本轮的入场标不带发起它的运输舱");
            return false;
        }

        return true;
    }

    private static MethodBase TargetMethod() => target!;

    private static void Prefix(TravellingTransporters __instance) => RimRunnerArrivalTag.EnterArrival(__instance);

    private static void Finalizer(TravellingTransporters __instance) => RimRunnerArrivalTag.ExitArrival(__instance);
}

internal static class RimRunnerArrivalTag
{
    private sealed class Arrival
    {
        internal int Tick;
        internal string? CaseId;
        internal bool Teardown;
        internal required StackTrace Trace;
        internal string? Caller;
        internal string? CarrierDef;
        internal Arrival? CarrierArrival;
    }

    private const int CallerFrames = 3;
    private const int MaxCarrierDepth = 4;

    private static readonly ConditionalWeakTable<object, Arrival> arrivals = [];
    private static readonly List<Thing> scratch = [];
    private static readonly List<TravellingTransporters> arriving = [];
    private static bool simulatingBetween;

    private static readonly bool enabled = GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);

    internal static void OnSpawn(Thing thing, Map map, bool respawningAfterLoad)
    {
        if (!enabled || respawningAfterLoad || map == null || thing == null || thing.Spawned)
        {
            return;
        }

        NoteSpawn(thing);
    }

    private static void NoteSpawn(Thing thing)
    {
        if (thing is Pawn)
        {
            Record(thing, CarrierOf(thing));
            return;
        }

        if (thing is Corpse or MinifiedThing)
        {
            return;
        }

        if (thing is IThingHolder holder && Holds(holder))
        {
            Record(thing, CarrierOf(thing));
            return;
        }

        if (thing.def.category == ThingCategory.Item)
        {
            object? carrier = CarrierOf(thing);
            if (carrier is not null and not Pawn)
            {
                Record(thing, carrier);
            }
        }
    }

    internal static void NoteWorldObject(WorldObject worldObject) => Record(worldObject, null);

    internal static void EnterArrival(TravellingTransporters transporters) => arriving.Add(transporters);

    internal static void ExitArrival(TravellingTransporters transporters)
    {
        int index = arriving.LastIndexOf(transporters);
        if (index >= 0)
        {
            arriving.RemoveAt(index);
        }
    }

    internal enum Window
    {
        InCase,
        Teardown,
        OtherCase,
        Between,
    }

    internal sealed class Reading
    {
        internal int Tick;
        internal string? CaseId;
        internal bool Teardown;
        internal string? Source;
        internal IReadOnlyList<string> Carriers = [];
        internal Reading? CarrierOrigin;
        internal string? OriginCarrier;

        internal Window WindowFor(string? ownerId) =>
            CaseId == null ? Window.Between
            : CaseId != ownerId ? Window.OtherCase
            : Teardown ? Window.Teardown : Window.InCase;
    }

    internal static string? Describe(object? thing, string? ownerId) =>
        Read(thing) is { } reading ? Render(reading, ownerId) : null;

    internal static Reading? Read(object? thing) =>
        thing != null && arrivals.TryGetValue(thing, out Arrival? arrival) ? ReadingOf(arrival) : null;

    private static Reading ReadingOf(Arrival arrival)
    {
        arrival.Caller ??= RimRunnerCallSite.Describe(arrival.Trace, CallerFrames, IsSpawnPlumbing) ?? "<来源?>";
        Reading reading = new()
        {
            Tick = arrival.Tick,
            CaseId = arrival.CaseId,
            Teardown = arrival.Teardown,
            Source = arrival.Caller,
        };

        List<string> chain = [];
        Arrival? origin = null;
        string? originDef = null;
        Arrival? current = arrival;
        for (int depth = 0; depth < MaxCarrierDepth && current?.CarrierDef != null; depth++)
        {
            chain.Add(current.CarrierDef);
            if (current.CarrierArrival != null)
            {
                origin = current.CarrierArrival;
                originDef = current.CarrierDef;
            }

            current = current.CarrierArrival;
        }

        reading.Carriers = chain;
        reading.OriginCarrier = originDef;
        reading.CarrierOrigin = origin == null ? null : ReadingOf(origin);
        return reading;
    }

    private static string Render(Reading reading, string? ownerId)
    {
        string line = "入场 " + DescribeOne(reading, ownerId);
        return reading.Carriers.Count == 0
            ? line
            : line + "；经 " + string.Join("←", reading.Carriers)
                     + (reading.CarrierOrigin == null ? "" : "；" + reading.OriginCarrier + " 入场 " + DescribeOne(reading.CarrierOrigin, ownerId));
    }

    internal static IDisposable SimulateBetweenCases()
    {
        if (simulatingBetween)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 用例间模拟已开启，不得嵌套");
        }

        simulatingBetween = true;
        return new BetweenScope();
    }

    private sealed class BetweenScope : IDisposable
    {
        public void Dispose()
        {
            simulatingBetween = false;
        }
    }

    private static string DescribeOne(Reading reading, string? ownerId)
    {
        string window = reading.WindowFor(ownerId) switch
        {
            Window.Between => "用例间",
            Window.OtherCase => reading.CaseId + " " + (reading.Teardown ? "收尾" : "体内"),
            Window.Teardown => "收尾",
            _ => "体内",
        };
        return "tick " + reading.Tick + " 于" + window + "←" + reading.Source;
    }

    private static void Record(object key, object? carrier)
    {
        RimRunnerContext? active = RimRunnerContext.Active;
        bool inCase = !simulatingBetween && RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Case => true,
            RimRunnerPhase.Scene or RimRunnerPhase.Enumeration or RimRunnerPhase.None => false
        };
        RimRunnerContext? inCaseCtx = inCase ? active! : null;
        var arrival = new Arrival
        {
            Tick = Find.TickManager?.TicksGame ?? -1,
            CaseId = inCaseCtx?.Current!.id,
            Teardown = inCaseCtx != null && inCaseCtx.TearingDown,
            Trace = new StackTrace(1, false)
        };

        if (carrier != null)
        {
            arrival.CarrierDef = carrier switch
            {
                Thing thing => thing.def?.defName,
                WorldObject worldObject => worldObject.def?.defName,
                _ => null
            } ?? carrier.GetType().Name;
            arrivals.TryGetValue(carrier, out arrival.CarrierArrival);
        }

        arrivals.Remove(key);
        arrivals.Add(key, arrival);
    }

    private static object? CarrierOf(Thing thing)
    {
        IThingHolder holder = thing.ParentHolder;
        for (int depth = 0; holder != null && depth < 8; depth++)
        {
            switch (holder)
            {
                case Thing carrier:
                    return carrier;
                case WorldObject worldObject:
                    return worldObject;
                case Map:
                    return null;
            }

            holder = holder.ParentHolder;
        }

        return arriving.Count > 0 ? arriving[^1] : null;
    }

    private static bool Holds(IThingHolder holder)
    {
        scratch.Clear();
        try
        {
            ThingOwnerUtility.GetAllThingsRecursively(holder, scratch, allowUnreal: true);
            return scratch.Count > 0;
        }
        finally
        {
            scratch.Clear();
        }
    }

    private static bool IsSpawnPlumbing(Type type) =>
        type == typeof(GenSpawn) || type == typeof(GenPlace) || type == typeof(GenDrop) || type == typeof(SkyfallerMaker)
        || typeof(ThingOwner).IsAssignableFrom(type)
        || type == typeof(RimRunnerArrivalTag)
        || type == typeof(Patch_GenSpawn_Spawn_RimRunnerBounds)
        || type == typeof(Patch_WorldObjectsHolder_Add_RimRunnerArrivalTag)
        || type == typeof(WorldObjectsHolder);
}
