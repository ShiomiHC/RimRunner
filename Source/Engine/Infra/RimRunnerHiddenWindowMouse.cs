using UnityEngine.InputSystem;

namespace RimRunner;

internal static class RimRunnerHiddenWindowMouse
{
    internal static readonly Vector2 Parked = new(-10000f, -10000f);

    internal static readonly bool WindowHidden = GenCommandLine.CommandLineArgPassed("NoWindow");

    private static Thing? targetThing;
    private static IntVec3? targetCell;

    internal static HoverIssue? LastIssue { get; private set; }

    internal static bool HasTarget => targetThing != null || targetCell != null;

    internal static string TargetLabel => targetThing is { } thing
        ? thing.LabelShort
        : targetCell is { } cell ? "(" + cell.x + ", " + cell.z + ")" : string.Empty;

    internal static bool TestOverride;

    internal static void SetTarget(Thing thing)
    {
        targetThing = thing;
        targetCell = null;
        LastIssue = null;
    }

    internal static void SetTarget(IntVec3 cell)
    {
        targetCell = cell;
        targetThing = null;
        LastIssue = null;
    }

    internal static void ClearTarget()
    {
        targetThing = null;
        targetCell = null;
        LastIssue = null;
    }

    internal static void Update()
    {
        if (!WindowHidden || RimRunnerLivePointer.Held != null)
        {
            return;
        }

        LastIssue = null;
        Vector2 expected = Parked;
        if ((RimRunnerProcessMode.Film || TestOverride) && HasTarget && TryResolve(out Vector3 world))
        {
            Vector3 screen = Find.Camera.WorldToScreenPoint(world);
            expected = new Vector2(screen.x, screen.y);
        }

        Vector3 current = Input.mousePosition;
        if (current.x != expected.x || current.y != expected.y)
        {
            InputSystem.QueueMouseMove(expected);
        }
    }

    private static bool TryResolve(out Vector3 world)
    {
        world = default;
        Map? map = RimRunnerCurrentMapProbe.EngineCurrentMap();
        if (map == null)
        {
            return false;
        }

        if (targetThing is { } thing)
        {
            Thing? resolved = RimRunnerFilmSession.ResolveFollowed(thing);
            if (resolved == null || !resolved.Spawned || resolved.Map != map)
            {
                LastIssue = HoverIssue.OffMap;
                return false;
            }

            world = RimRunnerDrawnPosition.ThingCenter(resolved);
            if (resolved.Position.Fogged(map))
            {
                LastIssue = HoverIssue.Fogged;
            }
        }
        else if (targetCell is { } cell)
        {
            if (!cell.InBounds(map))
            {
                LastIssue = HoverIssue.CellOutOfBounds;
                return false;
            }

            world = RimRunnerDrawnPosition.CellCenter(map, cell);
        }
        else
        {
            return false;
        }

        Vector3 screen = Find.Camera.WorldToScreenPoint(world);
        if (screen.x < 0f || screen.x >= UnityEngine.Screen.width || screen.y < 0f || screen.y >= UnityEngine.Screen.height)
        {
            LastIssue = HoverIssue.OffScreen;
            return false;
        }

        return true;
    }
}

internal enum HoverIssue
{
    OffMap,
    CellOutOfBounds,
    OffScreen,
    Fogged
}
