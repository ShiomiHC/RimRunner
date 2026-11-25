namespace RimRunner;

public static class RimRunnerWireKeys
{
    public static class Progress
    {
        public const string LoadedPackIds = RimRunnerPipeProtocol.Keys.LoadedPackIds;
    }
    public static class RunRequest
    {
        public const string SaveProbe = "saveProbe";

        public const string ComponentExemption = "componentExemption";

        public const string WarnChecked = "warnChecked";

        public const string Inputs = "inputs";

        public static string? NormalizeInputPath(string relativePath, out string? problem)
        {
            problem = null;
            string path = relativePath.Trim().Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal))
            {
                path = path.Substring(2);
            }

            if (path.Length == 0)
            {
                problem = "为空";
                return null;
            }

            if (path.StartsWith("/", StringComparison.Ordinal) || path.Contains(':'))
            {
                problem = "须是数据根下的相对路径";
                return null;
            }

            if (path.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                problem = "不得含空段、. 或 ..";
                return null;
            }

            return path;
        }
    }

    public static class BuildSaveSidecar
    {
        public const string Suffix = ".newgame-result.json";

        public const string ComponentFirstFills = "componentFirstFills";

        public const string ComponentTickDriven = "componentTickDriven";
    }

    public static class ConfigManifest
    {
        public const string FileName = "RimRunner.ConfigInputs.json";
        public const string PrefsKeys = "prefsKeys";
        public const string ModSettings = "modSettings";
        public const string Key = "key";
        public const string Layer = "layer";
        public const string PackageId = "packageId";
        public const string ModClass = "modClass";
        public const string Folders = "folders";
    }

    public static class Report
    {
        public const string Schema = "schema";
        public const int SchemaVersion = 10;

        public const string AssemblyMvids = RimRunnerPipeProtocol.Keys.AssemblyMvids;

        public const string ProductCapabilities = RimRunnerPipeProtocol.Keys.ProductCapabilities;

        public const string ContentFingerprint = "contentFingerprint";

        public const string ComponentFirstFills = "componentFirstFills";

        public const string LedgerFaults = "ledgerFaults";

        public const string Rejections = "rejections";

        public const string UnattributedStartupErrors = "unattributedStartupErrors";

        public const string CaseRejections = "caseRejections";
        public const string OutOfBatchFaults = "outOfBatchFaults";
        public const string Warnings = "warnings";
        public const string WarningsAcked = "warningsAcked";

        public const string Borrowed = "borrowed";

        public const string Inputs = "inputs";
        public const string InputPath = "path";
        public const string InputSource = "source";
        public const string InputHash = "hash";
        public const string InputCases = "cases";
        public const string InputSpecified = "指定";
        public const string InputOwnTree = "本树";

        public const string InputsUnread = "inputsUnread";

        public const string EnvChecksClean = "envChecksClean";

        public const string InapplicableCases = "inapplicableCases";

        public const string GroupSkippedCases = "groupSkippedCases";

        public const string AbsentCases = "absentCases";

        public const string CustomSave = "customSave";
        public const string CustomSaveNotes = "customSaveNotes";

        public const string AmbientWindows = "ambientWindows";

        public const string AmbientWindowsFresh = "ambientWindowsFresh";

        public const string MemoryStart = "memoryStart";
        public const string MemoryEnd = "memoryEnd";

        public const string MemoryPrivateBytes = "privateBytes";
        public const string MemoryManagedCommitted = "managedCommitted";
        public const string MemoryManagedUsed = "managedUsed";

        public const string Shard = "shard";
        public const string ShardGroup = "group";
        public const string ShardIndex = "index";
        public const string ShardCount = "count";
        public const string ShardCase = "caseId";
        public const string ShardCells = "cells";
        public const string ShardWeight = "weight";
        public const string ShardFingerprint = "fingerprint";
    }

    public static class ShardRun
    {
        public const string Group = "shardGroup";
        public const string Index = "shardIndex";
        public const string Count = "shardCount";
        public const string Queue = "shardQueue";
        public const string Fingerprint = "shardFingerprint";
    }

    public static class RunSource
    {
        public const string Commit = "sourceCommit";
        public const string Worktree = "sourceWorktree";
        public const string Changes = "sourceChanges";
        public const string ChangeCount = "sourceChangeCount";
        public const string Error = "sourceError";
    }
}

public static class RimRunnerRejectionGate
{
    public const string Startup = "startup";
    public const string Contract = "contract";
    public const string Registry = "registry";
    public const string Texture = "texture";
    public const string Command = "command";
    public const string Package = "package";
    public const string Quiet = "quiet";
    public const string Shard = "shard";
    public const string Engine = "engine";
}

public static class RimRunnerInstanceMode
{
    public const string Batch = "batch";
    public const string Showcase = "showcase";
    public const string Film = "film";
}
