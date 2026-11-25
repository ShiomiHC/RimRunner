using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimRunnerCli;

internal static class WorktreeHarvest
{
    internal const string CollectedRunIdsFileName = "collected-runids.txt";
    internal const int KeepTrees = 20;

    internal const int SampleBatchBytes = 4 * 1024 * 1024;

    private const int SharingRetries = 40;
    private const int SharingRetryDelayMs = 50;

    public sealed record Conflict(string Source, string Target);

    public sealed record SupersededSave(string Current, string Retained, bool FromWorktree);

    internal static readonly Regex SupersededSaveName =
        new(@"^(?<name>.+)\.(?<day>\d{8})-\d{6}(?:-[0-9a-f]{8})?\.rws$", RegexOptions.CultureInvariant);

    private const string SaveExtension = ".rws";

    public sealed class Outcome
    {
        public List<Conflict> Conflicts { get; } = [];
        public List<SupersededSave> SavesSuperseded { get; } = [];
        public List<string> PruneFailures { get; } = [];
        public int ArtifactsCopied { get; set; }
        public int ArtifactsSkipped { get; set; }
        public int RunIdsAppended { get; set; }
        public int RunIdsAlreadyPresent { get; set; }
        public int SampleFilesTouched { get; set; }
        internal int SampleBatchesWritten { get; set; }
        public int UnparsedSampleLines { get; set; }
        public int ReportsCopied { get; set; }
        public int ReportsSkipped { get; set; }
        public string ReportsTarget { get; set; } = "";
    }

    public static Outcome Collect(string sourceOutput, string targetOutput, string treeName,
        SampleStore.Kind samples = SampleStore.Kind.Jsonl) =>
        Collect(sourceOutput, targetOutput, treeName, SampleBatchBytes, samples);

    internal static Outcome Collect(string sourceOutput, string targetOutput, string treeName, int sampleBatchBytes,
        SampleStore.Kind samples = SampleStore.Kind.Jsonl)
    {
        var outcome = new Outcome();
        string targetArtifacts = Path.Combine(targetOutput, RimRunner.RimRunnerDataLayout.ArtifactsDirName);
        CopyTree(
            Path.Combine(sourceOutput, RimRunner.RimRunnerDataLayout.ArtifactsDirName),
            targetArtifacts,
            outcome);
        if (outcome.SavesSuperseded.Count > 0)
        {
            OutputRetention.PruneSupersededSaves(
                Path.Combine(targetArtifacts, RimRunner.RimRunnerDataLayout.ArtifactsSavesDirName),
                DateTime.Now, outcome.PruneFailures);
        }

        AppendSamples(sourceOutput, targetOutput, outcome, sampleBatchBytes, samples);

        string sourceReports = Path.Combine(sourceOutput, RimRunner.RimRunnerDataLayout.ReportsDirName);
        string treesRoot = Path.Combine(targetOutput, RimRunner.RimRunnerDataLayout.ReportsDirName,
            RimRunner.RimRunnerDataLayout.WorktreesSubDirName);
        string treeTarget = Path.Combine(treesRoot, treeName);
        outcome.ReportsTarget = treeTarget;
        CopyArchives(sourceReports, treeTarget, outcome);
        string singleRoot = Path.Combine(sourceReports, RimRunner.RimRunnerDataLayout.SingleSubDirName);
        if (Directory.Exists(singleRoot))
        {
            foreach (string caseDir in Directory.GetDirectories(singleRoot))
            {
                CopyArchives(caseDir, Path.Combine(treeTarget, RimRunner.RimRunnerDataLayout.SingleSubDirName,
                    Path.GetFileName(caseDir)), outcome);
            }
        }

        if (Directory.Exists(treeTarget))
        {
            Directory.SetLastWriteTimeUtc(treeTarget, DateTime.UtcNow);
            PruneTrees(treesRoot);
        }

        return outcome;
    }

    private static void CopyArchives(string sourceDir, string targetDir, Outcome outcome)
    {
        if (!Directory.Exists(sourceDir)) return;

        foreach (string path in Directory.GetFiles(sourceDir, "*.json"))
        {
            if (RimRunner.RimRunnerArchiveName.Parse(Path.GetFileName(path)) == null) continue;
            CopyOne(path, Path.Combine(targetDir, Path.GetFileName(path)), outcome, isArtifact: false);
        }
    }

    private static void CopyTree(string sourceDir, string targetDir, Outcome outcome)
    {
        if (!Directory.Exists(sourceDir)) return;

        string savesDir = Path.Combine(sourceDir, RimRunner.RimRunnerDataLayout.ArtifactsSavesDirName);
        foreach (string path in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(targetDir, Path.GetRelativePath(sourceDir, path));
            bool exported = string.Equals(Path.GetDirectoryName(path), savesDir, StringComparison.OrdinalIgnoreCase)
                            && path.EndsWith(SaveExtension, StringComparison.OrdinalIgnoreCase);
            if (exported && File.Exists(target) && !SameBytes(path, target))
                PlaceSave(path, target, outcome);
            else
                CopyOne(path, target, outcome, isArtifact: true);
        }
    }

    private static void PlaceSave(string source, string target, Outcome outcome)
    {
        DateTime sourceTime = File.GetLastWriteTime(source);
        DateTime targetTime = File.GetLastWriteTime(target);
        if (sourceTime > targetTime)
        {
            string retained = RetainedPath(target, targetTime, target, out bool already);
            if (already)
                File.Delete(target);
            else
                File.Move(target, retained);
            File.Copy(source, target, overwrite: false);
            outcome.SavesSuperseded.Add(new SupersededSave(target, retained, FromWorktree: true));
            outcome.ArtifactsCopied++;
            return;
        }

        string aside = RetainedPath(target, sourceTime, source, out bool present);
        if (present)
        {
            outcome.ArtifactsSkipped++;
            return;
        }

        File.Copy(source, aside, overwrite: false);
        outcome.SavesSuperseded.Add(new SupersededSave(target, aside, FromWorktree: false));
        outcome.ArtifactsCopied++;
    }

    private static string RetainedPath(string current, DateTime time, string content, out bool already)
    {
        string dir = Path.GetDirectoryName(current)!;
        string stem = Path.GetFileNameWithoutExtension(current) + "." + time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string plain = Path.Combine(dir, stem + SaveExtension);
        if (!File.Exists(plain))
        {
            already = false;
            return plain;
        }

        already = SameBytes(plain, content);
        if (already) return plain;

        string hash;
        using (FileStream stream = File.OpenRead(content))
            hash = Convert.ToHexStringLower(SHA256.HashData(stream))[..8];
        string hashed = Path.Combine(dir, stem + "-" + hash + SaveExtension);
        if (!File.Exists(hashed)) return hashed;
        already = SameBytes(hashed, content);
        if (already) return hashed;
        throw new IOException($"导出档的留存名已被内容不匹配的文件占用：{hashed}");
    }

    private static void CopyOne(string source, string target, Outcome outcome, bool isArtifact)
    {
        if (File.Exists(target))
        {
            if (SameBytes(source, target))
            {
                if (isArtifact) outcome.ArtifactsSkipped++;
                else outcome.ReportsSkipped++;
            }
            else
            {
                outcome.Conflicts.Add(new Conflict(source, target));
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: false);
        if (isArtifact) outcome.ArtifactsCopied++;
        else outcome.ReportsCopied++;
    }

    internal static bool SameBytes(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;

        const int blockBytes = 1024 * 1024;
        using FileStream left = File.OpenRead(a);
        using FileStream right = File.OpenRead(b);
        byte[] leftBlock = new byte[blockBytes];
        byte[] rightBlock = new byte[blockBytes];
        while (true)
        {
            int read = left.ReadAtLeast(leftBlock, blockBytes, throwOnEndOfStream: false);
            if (right.ReadAtLeast(rightBlock, read, throwOnEndOfStream: false) != read) return false;
            if (read == 0) return true;
            if (!leftBlock.AsSpan(0, read).SequenceEqual(rightBlock.AsSpan(0, read))) return false;
        }
    }

    private static void AppendSamples(string sourceOutput, string targetOutput, Outcome outcome,
        int sampleBatchBytes, SampleStore.Kind samples)
    {
        if (SampleStore.Present(sourceOutput, out bool both) is not { } present) return;
        if (both)
            throw new InvalidDataException($"读数库 {SampleStore.DirOf(sourceOutput)} 里 jsonl 与 SQLite 两种格式并存");
        if (SampleStore.MixedFault(targetOutput, samples) is string mixed) throw new InvalidDataException(mixed);
        if (present != samples)
            outcome.UnparsedSampleLines += SampleStore.Convert(sourceOutput, samples, _ => { }).Unparsed;
        if (samples == SampleStore.Kind.Sqlite)
            AppendSamplesSqlite(sourceOutput, targetOutput, outcome);
        else
            AppendSamplesJsonl(sourceOutput, targetOutput, outcome, sampleBatchBytes);
    }

    private static void AppendSamplesSqlite(string sourceOutput, string targetOutput, Outcome outcome)
    {
        string collectedPath = Path.Combine(sourceOutput, CollectedRunIdsFileName);
        var collected = new HashSet<string>(
            File.Exists(collectedPath) ? File.ReadAllLines(collectedPath) : [], StringComparer.Ordinal);
        collected.Remove("");
        string sourcePath = SampleStore.DatabaseOf(sourceOutput);
        string targetPath = SampleStore.DatabaseOf(targetOutput);
        var appended = new HashSet<string>(StringComparer.Ordinal);
        var pendingRuns = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<SqliteSampleStore.StoredLine>();
        var overlaps = new List<SampleOverlapException.Overlap>();
        using (SqliteSampleStore source = SqliteSampleStore.Open(sourcePath))
        using (SqliteSampleStore target = SqliteSampleStore.Open(targetPath))
        {
            foreach (string caseId in source.CaseIds())
            {
                List<SqliteSampleStore.StoredLine> lines =
                    [.. source.Lines(caseId).Where(line => !collected.Contains(line.RunId))];
                if (lines.Count == 0) continue;
                var runIds = new HashSet<string>(lines.Select(line => line.RunId), StringComparer.Ordinal);
                appended.UnionWith(runIds);
                Dictionary<string, List<string>> held = target.Lines(caseId, runIds)
                    .GroupBy(line => line.RunId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Select(line => line.Line).ToList(),
                        StringComparer.Ordinal);
                var mismatched = new SortedSet<string>(StringComparer.Ordinal);
                foreach ((string runId, List<string> rows) in held)
                {
                    List<string> mine = [.. lines.Where(line => line.RunId == runId).Select(line => line.Line)];
                    if (rows.Count > mine.Count || !rows.SequenceEqual(mine.Take(rows.Count), StringComparer.Ordinal))
                        mismatched.Add(runId);
                }

                if (mismatched.Count > 0)
                {
                    overlaps.Add(new SampleOverlapException.Overlap($"{sourcePath}（{caseId}）", targetPath, [.. mismatched]));
                    continue;
                }

                var skip = held.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
                foreach (SqliteSampleStore.StoredLine line in lines)
                {
                    if (skip.TryGetValue(line.RunId, out int left) && left > 0)
                    {
                        skip[line.RunId] = left - 1;
                        continue;
                    }

                    pending.Add(line);
                    pendingRuns.Add(line.RunId);
                }
            }

            if (overlaps.Count > 0) throw new SampleOverlapException(overlaps);
            if (pending.Count > 0)
            {
                target.AppendMany(pending.Select(line => (line.CaseId, line.Line)));
                outcome.SampleFilesTouched = pending.Select(line => line.CaseId).Distinct(StringComparer.Ordinal).Count();
                outcome.SampleBatchesWritten = 1;
            }
        }

        if (appended.Count > 0)
        {
            File.AppendAllLines(collectedPath, appended.OrderBy(id => id, StringComparer.Ordinal),
                new UTF8Encoding(false));
        }

        outcome.RunIdsAppended = pendingRuns.Count;
        outcome.RunIdsAlreadyPresent = appended.Count - pendingRuns.Count;
    }

    private static void AppendSamplesJsonl(string sourceOutput, string targetOutput, Outcome outcome,
        int sampleBatchBytes)
    {
        string sourceDir = Path.Combine(sourceOutput, RimRunner.RimRunnerDataLayout.SamplesDirName);
        if (!Directory.Exists(sourceDir)) return;

        string collectedPath = Path.Combine(sourceOutput, CollectedRunIdsFileName);
        var collected = new HashSet<string>(
            File.Exists(collectedPath) ? File.ReadAllLines(collectedPath) : [], StringComparer.Ordinal);
        collected.Remove("");
        var appended = new HashSet<string>(StringComparer.Ordinal);
        string targetDir = Path.Combine(targetOutput, RimRunner.RimRunnerDataLayout.SamplesDirName);

        var plans = new List<SamplePlan>();
        foreach (string path in Directory.GetFiles(sourceDir, "*.jsonl"))
        {
            plans.Add(PlanLines(path, Path.Combine(targetDir, Path.GetFileName(path)), line =>
            {
                string? runId = RunIdOf(line);
                if (runId == null)
                {
                    outcome.UnparsedSampleLines++;
                    return false;
                }

                if (collected.Contains(runId)) return false;
                appended.Add(runId);
                return true;
            }));
        }

        ReconcileOverlap(plans);
        foreach (SamplePlan plan in plans)
        {
            int batches = AppendPlanned(plan, sampleBatchBytes);
            outcome.SampleBatchesWritten += batches;
            if (batches > 0) outcome.SampleFilesTouched++;
        }

        if (appended.Count > 0)
        {
            File.AppendAllLines(collectedPath, appended.OrderBy(id => id, StringComparer.Ordinal),
                new UTF8Encoding(false));
        }

        int stillPending = appended.Count(runId => plans.Any(plan => plan.RunIds.Contains(runId)));
        outcome.RunIdsAppended = stillPending;
        outcome.RunIdsAlreadyPresent = appended.Count - stillPending;
    }

    internal sealed class SamplePlan(string source, string target)
    {
        public string Source { get; } = source;
        public string Target { get; } = target;
        internal List<bool> Keep { get; } = [];
        public HashSet<string> RunIds { get; } = new(StringComparer.Ordinal);
    }

    internal static SamplePlan PlanLines(string source, string target, Func<string, bool> keep)
    {
        var plan = new SamplePlan(source, target);
        foreach (string line in File.ReadLines(source))
        {
            bool kept = line.Length > 0 && keep(line);
            plan.Keep.Add(kept);
            if (kept && LineRunId(line) is string runId) plan.RunIds.Add(runId);
        }

        return plan;
    }

    internal static void ReconcileOverlap(IReadOnlyList<SamplePlan> plans)
    {
        var overlaps = new List<SampleOverlapException.Overlap>();
        var settled = new List<(SamplePlan Plan, Dictionary<string, int> Held)>();
        foreach (SamplePlan plan in plans)
        {
            if (plan.RunIds.Count == 0 || !File.Exists(plan.Target)) continue;
            var held = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (var stream = new FileStream(plan.Target, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            {
                while (reader.ReadLine() is string line)
                {
                    if (line.Length > 0 && LineRunId(line) is string runId && plan.RunIds.Contains(runId))
                    {
                        if (!held.TryGetValue(runId, out List<string>? rows)) held[runId] = rows = [];
                        rows.Add(line);
                    }
                }
            }

            if (held.Count == 0) continue;

            var pending = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            ForEachKept(plan, (_, runId, line) =>
            {
                if (!held.ContainsKey(runId)) return;
                if (!pending.TryGetValue(runId, out List<string>? rows)) pending[runId] = rows = [];
                rows.Add(line);
            });

            var mismatched = new SortedSet<string>(StringComparer.Ordinal);
            foreach ((string runId, List<string> rows) in held)
            {
                List<string> source = pending[runId];
                if (rows.Count > source.Count || !rows.SequenceEqual(source.Take(rows.Count), StringComparer.Ordinal))
                    mismatched.Add(runId);
            }

            if (mismatched.Count > 0)
                overlaps.Add(new SampleOverlapException.Overlap(plan.Source, plan.Target, [.. mismatched]));
            else
                settled.Add((plan, held.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal)));
        }

        if (overlaps.Count > 0) throw new SampleOverlapException(overlaps);

        foreach ((SamplePlan plan, Dictionary<string, int> held) in settled)
        {
            var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
            ForEachKept(plan, (index, runId, _) =>
            {
                if (held.TryGetValue(runId, out int skip) && skip > 0)
                {
                    plan.Keep[index] = false;
                    held[runId] = skip - 1;
                }
                else
                {
                    remaining[runId] = remaining.GetValueOrDefault(runId) + 1;
                }
            });
            plan.RunIds.RemoveWhere(runId => !remaining.ContainsKey(runId));
        }
    }

    private static void ForEachKept(SamplePlan plan, Action<int, string, string> visit)
    {
        int index = 0;
        foreach (string line in File.ReadLines(plan.Source))
        {
            if (index >= plan.Keep.Count) break;
            if (plan.Keep[index] && LineRunId(line) is string runId) visit(index, runId, line);
            index++;
        }
    }

    internal static int AppendPlanned(SamplePlan plan, int batchBytes)
    {
        var encoding = new UTF8Encoding(false);
        using var batch = new MemoryStream();
        int batches = 0;

        void Flush()
        {
            if (batch.Length == 0) return;
            if (batches == 0) Directory.CreateDirectory(Path.GetDirectoryName(plan.Target)!);
            AppendShared(plan.Target, batch.GetBuffer().AsSpan(0, (int)batch.Length), healTail: batches == 0);
            batches++;
            batch.SetLength(0);
        }

        int index = 0;
        foreach (string line in File.ReadLines(plan.Source))
        {
            if (index >= plan.Keep.Count) break;
            if (!plan.Keep[index++]) continue;
            if (batch.Length > 0 && batch.Length + encoding.GetMaxByteCount(line.Length) + 1 > batchBytes)
                Flush();
            byte[] bytes = encoding.GetBytes(line);
            batch.Write(bytes, 0, bytes.Length);
            batch.WriteByte((byte)'\n');
        }

        Flush();
        return batches;
    }

    private const string RunIdPrefix = "{\"runId\":\"";

    private static string? LineRunId(string line)
    {
        if (line.StartsWith(RunIdPrefix, StringComparison.Ordinal))
        {
            int end = line.IndexOf('"', RunIdPrefix.Length);
            if (end > RunIdPrefix.Length && line.IndexOf('\\', RunIdPrefix.Length, end - RunIdPrefix.Length) < 0)
                return line[RunIdPrefix.Length..end];
        }

        return RunIdOf(line);
    }

    private static string? RunIdOf(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("runId", out JsonElement value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static void AppendShared(string target, string text) =>
        AppendShared(target, new UTF8Encoding(false).GetBytes(text), healTail: true);

    internal static void AppendShared(string target, ReadOnlySpan<byte> payload, bool healTail)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(target, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.Read);
                if (healTail && stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n')
                    {
                        stream.WriteByte((byte)'\n');
                    }
                }

                stream.Seek(0, SeekOrigin.End);
                stream.Write(payload);
                return;
            }
            catch (IOException exception) when (attempt < SharingRetries && IsSharingViolation(exception))
            {
                Thread.Sleep(SharingRetryDelayMs);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static void PruneTrees(string treesRoot)
    {
        DirectoryInfo[] trees = new DirectoryInfo(treesRoot).GetDirectories();
        foreach (DirectoryInfo stale in trees.OrderByDescending(dir => dir.LastWriteTimeUtc).Skip(KeepTrees))
        {
            if ((stale.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            stale.Delete(recursive: true);
        }
    }
}
