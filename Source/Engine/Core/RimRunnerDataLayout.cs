using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RimRunner;

public static class RimRunnerDataLayout
{
    public const string IndexDirName = "index";
    public const string CaseManifestFileName = "cases.json";
    public const string ShowcaseManifestFileName = "showcases.json";

    public const string ReportsDirName = "reports";

    public const string HistoryDirName = "history";
    public const string SamplesDirName = "samples";

    public const string ArtifactsDirName = "artifacts";
    public const string ArtifactsScreenshotsDirName = "screenshots";
    public const string ArtifactsReadoutsDirName = "readouts";
    public const string ArtifactsSavesDirName = "saves";

    public const string FilmDirName = "film";
    public const string FilmFramesSuffix = ".frames";
    public const string FilmResultFileName = "film.json";
    public const string FilmAudioSuffix = ".audio";
    public const string FilmAudioEventsFileName = "audio.json";
    public const string FilmAudioPcmFileName = "audio.pcm";

    public const string RimRunnerLogSubDirName = "rimrunner-log";
    public const string WorktreesSubDirName = "worktrees";

    public const string SingleSubDirName = "single";

    public const string PassRateLedgerStem = "passrate";

    public static string? LedgerFileName(string stem, string? modList)
    {
        if (modList == null)
        {
            return stem + ".json";
        }

        foreach (char character in modList)
        {
            if (!char.IsLetterOrDigit(character) && character != '-' && character != '_')
            {
                return null;
            }
        }

        return stem + "-" + modList + ".json";
    }

    public static string SaveLedgerKey(string? modList, string savePath)
    {
        string full = savePath;
        StringBuilder stem = new();
        foreach (char character in Path.GetFileNameWithoutExtension(full))
        {
            stem.Append(char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '_');
        }

        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())), 0, 4)
            .ToLowerInvariant();
        return (modList == null ? "" : modList + "-") + "save-" + stem + "-" + suffix;
    }

    public static class ManifestKeys
    {
        public const string GeneratedAt = "generatedAt";
        public const string Cases = "cases";
        public const string AssemblyMvids = "assemblyMvids";

        public const string CaseId = "id";
        public const string CaseKind = "kind";
        public const string CaseMinTextures = "minTextures";
        public const string CaseQuietMachine = "quietMachine";
        public const string CaseShardable = "shardable";
        public const string CaseEnvCheck = "envCheck";
        public const string CaseType = "type";
        public const string CaseRequiresPackages = "requiresPackages";
        public const string CaseGroups = "groups";
        public const string CaseEngineOwned = "engineOwned";
    }

    public static class ShowcaseManifestKeys
    {
        public const string GeneratedAt = "generatedAt";
        public const string Showcases = "showcases";
        public const string AssemblyMvids = "assemblyMvids";
        public const string Id = "id";
        public const string Title = "title";
        public const string Steps = "steps";
        public const string RequiresPackages = "requiresPackages";
    }
}
