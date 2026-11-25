namespace RimRunner;

public static class RimRunnerLiveContract
{
    public const string KeySchema = "schema";

    public static class Schema
    {
        public const string Progress = "progress";
        public const string Pawns = "pawns";
        public const string Gizmos = "gizmos";
        public const string Menu = "menu";
        public const string Windows = "windows";
        public const string Dev = "dev";
        public const string Stat = "stat";
        public const string Hediffs = "hediffs";
        public const string Screenshot = "screenshot";
        public const string Save = "save";
        public const string Profiler = "profiler";
        public const string Showcase = "showcase";
        public const string ShardCells = "shardCells";
        public const string Film = "film";
        public const string FilmRoster = "filmRoster";
        public const string Drive = "drive";
        public const string Actions = "actions";
        public const string Assemblies = "assemblies";
        public const string UnownedErrors = "unownedErrors";
        public const string Error = "error";
    }

    public static class UnownedErrors
    {
        public const string Count = "count";
        public const string Heads = "heads";
        public const string Head = "head";
        public const string Times = "times";
        public const string Sustained = "sustained";
    }

    public static class Drive
    {
        public const string Verb = "verb";
        public const string Args = "args";
        public const string Kind = "kind";
        public const string Assembly = "assembly";
        public const string KindDo = "do";
        public const string KindUntil = "until";
        public const string Met = "met";
        public const string LongEventSeconds = "longEventSeconds";
        public const int DefaultLongEventSeconds = 5;
        public const string LimitSeconds = "limitSeconds";
        public const int DefaultLimitSeconds = 30 * 60;

        public const string Id = "id";
        public const string State = "state";
        public const string Reason = "reason";
        public const string StartTick = "startTick";
        public const string EndTick = "endTick";
        public const string TicksAdvanced = "ticksAdvanced";
        public const string Frames = "frames";
        public const string ElapsedMs = "elapsedMs";
        public const string Lines = "lines";
        public const string Logs = "logs";
        public const string ErrorPrefix = "[Error] ";
        public const string WarningPrefix = "[Warning] ";

        public const string StateQueued = "queued";
        public const string StateRunning = "running";
        public const string StateDone = "done";
        public const string StateRefused = "refused";
        public const string StateAborted = "aborted";
        public const string StateFailed = "failed";

        public const string Record = "drive";
        public const string RecordCount = "count";
        public const string RecordVerbs = "verbs";
    }

    public static class ShardCells
    {
        public const int UnprocessableStatus = 422;
        public const string Case = "case";
        public const string MinShardWeight = "minShardWeight";
        public const string Fingerprint = "fingerprint";
        public const string Cells = "cells";
        public const string Key = "key";
        public const string Weight = "weight";
        public const string Warnings = "warnings";
        public const string WarningKey = "key";
        public const string WarningText = "text";
    }

    public static class Pawns
    {
        public const string Items = "items";
        public const string Nick = "nick";
        public const string Name = "name";
        public const string Id = "id";
        public const string Faction = "faction";
    }

    public static class Gizmos
    {
        public const string Selected = "selected";
        public const string Items = "items";
        public const string Index = "index";
        public const string Label = "label";
        public const string Type = "type";
        public const string Visible = "visible";
        public const string Disabled = "disabled";
        public const string DisabledReason = "disabledReason";
        public const string Toggle = "toggle";
        public const string Group = "group";
    }

    public static class Dev
    {
        public const string Path = "path";
        public const string Find = "find";
        public const string Kind = "kind";
        public const string Items = "items";
        public const string Hidden = "hidden";
        public const string Total = "total";
    }

    public static class Menu
    {
        public const string Open = "open";
        public const string Items = "items";
        public const string Index = "index";
        public const string Label = "label";
        public const string Disabled = "disabled";
        public const string Shown = "shown";
    }

    public static class Windows
    {
        public const string Items = "items";
        public const string Index = "index";
        public const string Type = "type";
        public const string Title = "title";
        public const string Layer = "layer";
        public const string ForcePause = "forcePause";
        public const string CloseOnCancel = "closeOnCancel";
    }

    public static class Stat
    {
        public const string Pawn = "pawn";
        public const string StatName = "stat";
        public const string Value = "value";
        public const string Explanation = "explanation";
        public const string Description = "description";
    }

    public static class Hediffs
    {
        public const string Pawn = "pawn";
        public const string PainTotal = "painTotal";
        public const string Items = "hediffs";
        public const string Def = "def";
        public const string Severity = "severity";
        public const string Part = "part";
        public const string PainOffset = "painOffset";
        public const string IsBad = "isBad";
        public const string TicksToDisappear = "ticksToDisappear";
        public const string StatEntries = "statEntries";
        public const string EntryLabel = "label";
        public const string EntryValue = "value";
        public const string ProductDiagnostics = "productDiagnostics";
    }

    public static class Screenshot
    {
        public const string ArtifactId = "artifactId";
        public const string State = "state";
        public const string Path = "path";
        public const string Width = "width";
        public const string Height = "height";
        public const string Error = "error";
        public const string FileName = "fileName";
    }

    public static class Save
    {
        public const string Saved = "saved";
        public const string Path = "path";
        public const string Bytes = "bytes";
        public const string Overwrote = "overwrote";
    }

    public static class Error
    {
        public const string Message = "error";
        public const string Available = "available";
        public const string Hint = "hint";
        public const string State = "state";
        public const string ActiveCommand = "activeCommand";
        public const string ActiveRunId = "activeRunId";
    }

    public static class Progress
    {
        public const string ProgramState = "programState";
        public const string MainThreadAvailable = "mainThreadAvailable";
        public const string CasesDone = "casesDone";
        public const string CasesTotal = "casesTotal";
        public const string CurrentCase = "currentCase";
        public const string TestPawns = "testPawns";
        public const string Job = "job";
        public const string Position = "position";
        public const string Downed = "downed";
        public const string Dead = "dead";
        public const string Phase = "phase";
        public const string AssertCount = "assertCount";
        public const string BodyTicks = "bodyTicks";
        public const string BodyFrames = "bodyFrames";
        public const string FrameSpeed = "frameSpeed";
        public const string Burst = "burst";
        public const string Samples = "samples";
        public const string SegmentCount = "segmentCount";
        public const string Segments = "segments";
        public const string LatestSegment = "latestSegment";
        public const string GameTick = "gameTick";
        public const string AgeMs = "ageMs";
    }

    public static class Profiler
    {
        public const string Armed = "armed";
        public const string ArmedGroups = "armedGroups";
        public const string Reset = "reset";
        public const string Groups = "groups";
    }

    public static class ScreenshotState
    {
        public const string Queued = "queued";
        public const string Capturing = "capturing";
        public const string Complete = "complete";
        public const string Error = "error";
    }

    public static class SceneFinding
    {
        public const string Kind = "kind";
        public const string Ordinal = "ordinal";
        public const string Assert = "assert";
        public const string Warning = "warning";
        public const string FilmNote = "filmNote";

        public static string Tag(string? kind) => kind switch
        {
            Assert => "失败断言",
            Warning => "带键警告",
            FilmNote => "拍摄提示",
            _ => "来源未知（" + (kind ?? "无") + "）"
        };
    }

    public static class Showcase
    {
        public const string State = "state";
        public const string Reloading = "reloading";
        public const string Id = "id";
        public const string Title = "title";
        public const string AbortReason = "abortReason";
        public const string Warnings = "warnings";
        public const string WarningLabel = "label";
        public const string WarningDetail = "detail";
        public const string WarningOffending = "offending";
        public const string Messages = "messages";
        public const string StagingErrors = "stagingErrors";
        public const string StepAborts = "stepAborts";
        public const string OperationErrors = "operationErrors";
        public const string StartupOwnErrors = "startupOwnErrors";
        public const string StartupForeignErrors = "startupForeignErrors";
        public const string StartupUnattributedErrors = "startupUnattributedErrors";
        public const string ColdLoadErrors = "coldLoadErrors";
        public const string ReloadErrors = "reloadErrors";
        public const string RegistryFaults = "registryFaults";
        public const string Drives = "drives";
        public const string Stepping = "stepping";
        public const string SteppingName = "name";
        public const string SteppingTicks = "ticks";
        public const string SteppingWithin = "withinTicks";
        public const string LastStep = "lastStep";

        public const string StateNone = "none";
        public const string StateStaging = "staging";
        public const string StateTearingDown = "tearingDown";
        public const string StateStaged = "staged";
        public const string StateStepping = "stepping";
        public const string StateAborted = "aborted";
    }

    public static class Film
    {
        public const string Ready = "ready";
        public const string Busy = "busy";
        public const string Seq = "seq";
        public const string Result = "result";

        public const string Kind = "kind";
        public const string Id = "id";
        public const string Shot = "shot";
        public const string Step = "step";
        public const string Ok = "ok";
        public const string Failure = "failure";
        public const string AbortReason = "abortReason";
        public const string Warnings = "warnings";
        public const string WarningLabel = "label";
        public const string WarningDetail = "detail";
        public const string WarningOffending = "offending";
        public const string Steps = "steps";
        public const string StepName = "name";
        public const string StepStartFrame = "startFrame";
        public const string StepEndFrame = "endFrame";
        public const string Messages = "messages";
        public const string StagingErrors = "stagingErrors";
        public const string StepAborts = "stepAborts";
        public const string Errors = "errors";
        public const string StartupErrors = "startupErrors";
        public const string LoadErrors = "loadErrors";
        public const string Reloaded = "reloaded";
        public const string Frames = "frames";
        public const string Ticks = "ticks";
        public const string Width = "width";
        public const string Height = "height";
        public const string Seed = "seed";
        public const string SceneSeed = "sceneSeed";
        public const string TimeUnpinned = "timeUnpinned";
        public const string ElapsedMs = "elapsedMs";

        public const string KindShot = "shot";
        public const string KindStage = "stage";
    }

    public static class Actions
    {
        public const string Builtins = "builtins";
        public const string Conditions = "conditions";
        public const string Scripts = "scripts";
        public const string Assembly = "assembly";
        public const string State = "state";
        public const string Kind = "kind";
        public const string Fault = "fault";
        public const string StateLoaded = "loaded";
        public const string StateRegisterError = "registerError";
        public const string StateUnloaded = "unloaded";
        public const string StateCompileError = "compileError";
        public const string Busy = "busy";
        public const string Id = "id";
        public const string Title = "title";
        public const string Params = "params";
        public const string Name = "name";
        public const string Required = "required";
        public const string Meaning = "meaning";
        public const string Placeholder = "placeholder";
    }

    public static class Assemblies
    {
        public const string Loaded = "loaded";
        public const string Name = "name";
        public const string Path = "path";
        public const string Mvid = "mvid";
        public const string Tpa = "tpa";
    }

    public static class FilmRoster
    {
        public const string RegistryFaults = "registryFaults";
        public const string Showcases = "showcases";
        public const string Id = "id";
        public const string Title = "title";
        public const string Leaves = "leaves";
        public const string Paths = "paths";
        public const string Leaf = "leaf";
        public const string Index = "index";
        public const string Path = "path";
        public const string Speeds = "speeds";
        public const string Dwells = "dwells";
        public const string Shots = "shots";
        public const string Name = "name";
        public const string Clip = "clip";
        public const string Formats = "formats";
        public const string Width = "width";
        public const string Height = "height";
        public const string MaxBytes = "maxBytes";
        public const string Fps = "fps";
        public const string Seed = "seed";

        public const string FormatPng = "png";
        public const string FormatGif = "gif";
        public const string FormatMp4 = "mp4";
    }
}
