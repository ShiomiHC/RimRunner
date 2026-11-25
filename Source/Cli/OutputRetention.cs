using System.Text.RegularExpressions;
using RimRunner;

namespace RimRunnerCli;

internal static class OutputRetention
{
    internal const int OrphanGraceDays = 3;
    internal const int SupersededSavesKeep = 3;

    private static readonly Regex LiveShot = new(@"^rimrunner-(\d{8})-\d{6}-\d{3}-", RegexOptions.CultureInvariant);
    private static readonly Regex RunShot = new(@"^rimrunner-([0-9a-f]{8}|norun)-", RegexOptions.CultureInvariant);
    private static readonly Regex RunText = new(@"^([0-9a-f]{8}|norun)-.+-\d+\.txt$", RegexOptions.CultureInvariant);

    internal sealed record Outcome(int ReportsDeleted, int ArtifactsDeleted, IReadOnlyList<string> Failures);

    internal static Outcome Sweep(string outputRoot, DateTime now)
    {
        var failures = new List<string>();
        string cutoff = RimRunnerArchiveName.AgeCutoff(now);
        string reports = Path.Combine(outputRoot, RimRunnerDataLayout.ReportsDirName);
        int reportsDeleted = 0;
        if (Directory.Exists(reports))
        {
            reportsDeleted += PruneArchive(reports, RimRunnerArchiveName.BatchKeepRecent,
                RimRunnerArchiveName.BatchKeepFailed, cutoff, keepNewest: true, failures);
            string single = Path.Combine(reports, RimRunnerDataLayout.SingleSubDirName);
            if (Directory.Exists(single))
            {
                HashSet<string>? roster = RosterIds(outputRoot);
                foreach (string dir in Directory.GetDirectories(single))
                {
                    bool retired = roster != null && !roster.Contains(Path.GetFileName(dir));
                    reportsDeleted += PruneArchive(dir, RimRunnerArchiveName.SingleKeepRecent,
                        RimRunnerArchiveName.SingleKeepFailed, cutoff, keepNewest: !retired, failures);
                    if (retired && !Directory.EnumerateFileSystemEntries(dir).Any())
                        TryDelete(() => Directory.Delete(dir), dir, failures);
                }
            }
        }

        HashSet<string> runs = RetainedRuns(reports);
        int artifactsDeleted = 0;
        string artifacts = Path.Combine(outputRoot, RimRunnerDataLayout.ArtifactsDirName);
        DateTime graceLine = now.AddDays(-OrphanGraceDays);
        string screenshots = Path.Combine(artifacts, RimRunnerDataLayout.ArtifactsScreenshotsDirName);
        if (Directory.Exists(screenshots))
        {
            foreach (string path in Directory.GetFiles(screenshots))
            {
                string name = Path.GetFileName(path);
                Match live = LiveShot.Match(name);
                bool stale = live.Success
                    ? string.CompareOrdinal(live.Groups[1].Value, cutoff) < 0
                    : RunShot.Match(name) is { Success: true } run && Orphan(run.Groups[1].Value, path, runs, graceLine);
                if (stale && TryDelete(() => File.Delete(path), path, failures)) artifactsDeleted++;
            }
        }

        string readouts = Path.Combine(artifacts, RimRunnerDataLayout.ArtifactsReadoutsDirName);
        if (Directory.Exists(readouts))
        {
            foreach (string path in Directory.GetFiles(readouts))
            {
                if (RunText.Match(Path.GetFileName(path)) is { Success: true } run
                    && Orphan(run.Groups[1].Value, path, runs, graceLine)
                    && TryDelete(() => File.Delete(path), path, failures))
                    artifactsDeleted++;
            }
        }

        artifactsDeleted += PruneSupersededSaves(
            Path.Combine(artifacts, RimRunnerDataLayout.ArtifactsSavesDirName), now, failures);

        return new Outcome(reportsDeleted, artifactsDeleted, failures);
    }

    internal static int PruneSupersededSaves(string savesDir, DateTime now, List<string> failures)
    {
        if (!Directory.Exists(savesDir)) return 0;
        string cutoff = RimRunnerArchiveName.AgeCutoff(now);
        int deleted = 0;
        var retained = new List<(string Path, string Name, string Day, string FileName)>();
        foreach (string path in Directory.GetFiles(savesDir))
        {
            string fileName = Path.GetFileName(path);
            if (WorktreeHarvest.SupersededSaveName.Match(fileName) is { Success: true } match)
                retained.Add((path, match.Groups["name"].Value, match.Groups["day"].Value, fileName));
        }

        foreach (var group in retained.GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            int rank = 0;
            foreach (var entry in group.OrderByDescending(entry => entry.FileName, StringComparer.Ordinal))
            {
                bool stale = rank++ >= SupersededSavesKeep || string.CompareOrdinal(entry.Day, cutoff) < 0;
                if (stale && TryDelete(() => File.Delete(entry.Path), entry.Path, failures)) deleted++;
            }
        }

        return deleted;
    }

    internal static int PruneArchive(string dir, int keepRecent, int keepFailed, string cutoff, bool keepNewest,
        List<string> failures)
    {
        string[] paths = Directory.GetFiles(dir, "20*.json");
        var keep = new HashSet<string>(
            RimRunnerArchiveName.SelectKeep(paths, keepRecent, keepFailed, cutoff, keepNewest), StringComparer.Ordinal);
        int deleted = 0;
        foreach (string path in paths)
        {
            if (!keep.Contains(path) && TryDelete(() => File.Delete(path), path, failures)) deleted++;
        }

        return deleted;
    }

    private static HashSet<string>? RosterIds(string outputRoot)
    {
        CaseManifest? manifest = CaseManifest.TryLoadFile(Path.Combine(outputRoot,
            RimRunnerDataLayout.IndexDirName, RimRunnerDataLayout.CaseManifestFileName));
        return manifest == null || manifest.Cases.Count == 0
            ? null
            : new HashSet<string>(manifest.Cases.Select(entry => entry.Id), StringComparer.Ordinal);
    }

    private static HashSet<string> RetainedRuns(string reports)
    {
        var runs = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(reports)) return runs;
        foreach (string path in Directory.EnumerateFiles(reports, "20*.json", SearchOption.AllDirectories))
        {
            if (RimRunnerArchiveName.Parse(path) is { } parts)
                runs.Add(parts.RunId[..Math.Min(8, parts.RunId.Length)]);
        }

        return runs;
    }

    private static bool Orphan(string run, string path, HashSet<string> runs, DateTime graceLine) =>
        !runs.Contains(run) && File.GetLastWriteTime(path) < graceLine;

    private static bool TryDelete(Action delete, string path, List<string> failures)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(path + "：" + exception.Message);
            return false;
        }
    }
}
