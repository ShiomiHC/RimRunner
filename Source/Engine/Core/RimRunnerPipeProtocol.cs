namespace RimRunner;

internal static class RimRunnerPipeProtocol
{
    public const string Id = "id";
    public const string Op = "op";
    public const string Params = "params";
    public const string Status = "status";
    public const string Body = "body";
    public const string Event = "event";

    public static class Ops
    {
        public const string Run = "run";
        public const string ShardCells = "shard.cells";
        public const string Progress = "progress";
        public const string Save = "save";
        public const string Pawns = "pawns";
        public const string Gizmos = "gizmos";
        public const string Menu = "menu";
        public const string Windows = "windows";
        public const string Dev = "dev";
        public const string Stat = "stat";
        public const string Hediffs = "hediffs";
        public const string Screenshot = "screenshot";
        public const string Profiler = "profiler";
        public const string Showcase = "showcase";
        public const string ShowcaseStage = "showcase.stage";
        public const string ShowcaseTeardown = "showcase.teardown";
        public const string ShowcaseStep = "showcase.step";
        public const string FilmRoster = "film.roster";
        public const string FilmShot = "film.shot";
        public const string FilmStage = "film.stage";
        public const string FilmPaths = "film.paths";
        public const string FilmEnd = "film.end";
        public const string Drive = "drive";
        public const string DriveStatus = "drive.status";
        public const string DriveCancel = "drive.cancel";
        public const string Actions = "actions";
        public const string Assemblies = "assemblies";
        public const string UnownedErrors = "errors.unowned";
    }

    public static class Events
    {
        public const string Hello = "hello";

        public const string State = "state";

        public const string Pulse = "pulse";

        public const string GpuMemory = "gpuMemory";

        public const string Log = "log";

        public const string Output = "output";

        public const string Sample = "sample";

        public const string Report = "report";

        public const string Ledger = "ledger";

        public const string Screenshot = "screenshot";

        public const string FilmFrame = "film.frame";

        public const string FilmResult = "film.result";

        public const string FilmFed = "film.fed";

        public const string ShardClaim = "shard.claim";

        public const string ShardDone = "shard.done";

        public const string Answer = "answer";

        public const string Heartbeat = "heartbeat";
    }

    public static class Keys
    {
        public const string Pid = "pid";

        public const string LoadedPackIds = "loadedPackIds";

        public const string ModAssemblyShortfalls = "modAssemblyShortfalls";

        public const string AssemblyMvids = "assemblyMvids";

        public const string ProductCapabilities = "productCapabilities";

        public const string Mode = "mode";

        public const string InGame = "inGame";
        public const string RunState = "runState";
        public const string ActiveCommand = "activeCommand";
        public const string ActiveRunId = "activeRunId";
        public const string ProfilerArmed = "profilerArmed";

        public const string StartupFault = "startupFault";

        public const string At = "at";
        public const string Level = "level";
        public const string Text = "text";
        public const string Frame = "frame";
        public const string LoadMarks = "loadMarks";
        public const string IdleSeconds = "idleSeconds";
        public const string LevelInfo = "info";
        public const string LevelWarn = "warn";

        public const string DeviceLocalBytes = "deviceLocalBytes";
        public const string OffloadedBytes = "offloadedBytes";
        public const string UncachedTextures = "uncachedTextures";

        public const string Path = "path";
        public const string Append = "append";
        public const string Quiet = "quiet";
        public const string CaseId = "caseId";

        public const string RunId = "runId";
        public const string Latest = "latest";
        public const string KeepRecent = "keepRecent";
        public const string KeepFailed = "keepFailed";

        public const string Label = "label";
        public const string Cap = "cap";
        public const string Cells = "cells";
        public const string Drop = "drop";

        public const string Snapshot = "snapshot";

        public const string Ask = "ask";
        public const string Ordinal = "ordinal";

        public const string Seq = "seq";
        public const string Index = "index";
        public const string Still = "still";
        public const string Error = "error";
        public const string Result = "result";
    }

    public const string PairKey = "key";
    public const string PairText = "text";

    public const int PulseIntervalMilliseconds = 2000;
    public const int HeartbeatIntervalMilliseconds = 5000;
    public const int WatchdogSeconds = 120;
}
