using System.Threading;

namespace RimRunner;

internal enum RimRunnerRunState
{
    Idle,
    Queued,
    Loading,
    Running,
    Saving,
    Driving
}

internal sealed class RimRunnerRunSnapshot
{
    public RimRunnerRunState State;
    public string? Command;
    public string? RunId;
}

internal static class RimRunnerRunCoordinator
{
    private static readonly Lock Sync = new();
    private static RimRunnerRunState state;
    private static string? activeCommand;
    private static string? activeRunId;

    internal static bool TryReserve(
        string command, string? runId, out RimRunnerRunSnapshot snapshot)
    {
        lock (Sync)
        {
            if (state != RimRunnerRunState.Idle)
            {
                snapshot = SnapshotUnsafe();
                return false;
            }

            SetActiveUnsafe(RimRunnerRunState.Queued, command, runId);
            snapshot = SnapshotUnsafe();
            return true;
        }
    }

    internal static bool TryReserveSave(string command, out RimRunnerRunSnapshot snapshot)
    {
        lock (Sync)
        {
            if (state != RimRunnerRunState.Idle)
            {
                snapshot = SnapshotUnsafe();
                return false;
            }

            SetActiveUnsafe(RimRunnerRunState.Saving, command, null);
            snapshot = SnapshotUnsafe();
            return true;
        }
    }

    internal static bool TryReserveDrive(string command, out RimRunnerRunSnapshot snapshot)
    {
        lock (Sync)
        {
            if (state != RimRunnerRunState.Idle)
            {
                snapshot = SnapshotUnsafe();
                return false;
            }

            SetActiveUnsafe(RimRunnerRunState.Driving, command, null);
            snapshot = SnapshotUnsafe();
            return true;
        }
    }

    internal static bool TryPrepareLoading(
        string command, string? runId, out RimRunnerRunSnapshot snapshot)
    {
        lock (Sync)
        {
            if (state == RimRunnerRunState.Idle)
            {
                SetActiveUnsafe(RimRunnerRunState.Loading, command, runId);
                snapshot = SnapshotUnsafe();
                return true;
            }

            if (state == RimRunnerRunState.Queued && MatchesUnsafe(command, runId))
            {
                state = RimRunnerRunState.Loading;
                snapshot = SnapshotUnsafe();
                return true;
            }

            snapshot = SnapshotUnsafe();
            return false;
        }
    }

    internal static bool TryStart(
        string command, string? runId, out RimRunnerRunSnapshot snapshot)
    {
        lock (Sync)
        {
            if (state == RimRunnerRunState.Idle)
            {
                SetActiveUnsafe(RimRunnerRunState.Running, command, runId);
                snapshot = SnapshotUnsafe();
                return true;
            }

            if ((state == RimRunnerRunState.Queued
                 || state == RimRunnerRunState.Loading)
                && MatchesUnsafe(command, runId))
            {
                state = RimRunnerRunState.Running;
                snapshot = SnapshotUnsafe();
                return true;
            }

            snapshot = SnapshotUnsafe();
            return false;
        }
    }

    internal static bool BeginCheckpointLoading(
        string command, string? runId, out RimRunnerRunSnapshot snapshot)
    {
        lock (Sync)
        {
            if (state == RimRunnerRunState.Running && MatchesUnsafe(command, runId))
            {
                state = RimRunnerRunState.Loading;
                snapshot = SnapshotUnsafe();
                return true;
            }

            snapshot = SnapshotUnsafe();
            return false;
        }
    }

    internal static void Complete(string? command, string? runId)
    {
        lock (Sync)
        {
            if (!MatchesUnsafe(command, runId))
            {
                return;
            }

            state = RimRunnerRunState.Idle;
            activeCommand = null;
            activeRunId = null;
        }

        RimRunnerSaveProbe.Disarm();
        RimRunnerInput.EndRun();
    }

    internal static RimRunnerRunSnapshot GetSnapshot()
    {
        lock (Sync)
        {
            return SnapshotUnsafe();
        }
    }

    internal static bool IsIdle
    {
        get
        {
            lock (Sync)
            {
                return state == RimRunnerRunState.Idle;
            }
        }
    }

    internal static bool IsDriving
    {
        get
        {
            lock (Sync)
            {
                return state == RimRunnerRunState.Driving;
            }
        }
    }

    private static bool MatchesUnsafe(string? command, string? runId)
    {
        return string.Equals(activeCommand, command, StringComparison.Ordinal)
               && string.Equals(activeRunId, runId, StringComparison.Ordinal);
    }

    private static void SetActiveUnsafe(
        RimRunnerRunState nextState, string command, string? runId)
    {
        state = nextState;
        activeCommand = command;
        activeRunId = runId;
    }

    private static RimRunnerRunSnapshot SnapshotUnsafe()
    {
        return new RimRunnerRunSnapshot
        {
            State = state,
            Command = activeCommand,
            RunId = activeRunId
        };
    }
}
