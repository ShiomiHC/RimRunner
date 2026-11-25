namespace RimRunnerCli;

public static class FilmRetention
{
    public sealed record Quota(int KeepRecent, int KeepTagged, int KeepFramesRuns)
    {
        public const int DefaultKeepRecent = 10;
        public const int DefaultKeepTagged = 10;
        public const int DefaultKeepFramesRuns = 2;

        public static readonly Quota Default = new(DefaultKeepRecent, DefaultKeepTagged, DefaultKeepFramesRuns);

        public string Describe() =>
            $"保留最近 {KeepRecent} 份 ∪ 带 tag 的最近 {KeepTagged} 份，逐帧只保留最近 {KeepFramesRuns} 份";
    }

    internal static HashSet<string> SelectKeep(IReadOnlyList<string> names, int keepRecent, int keepTagged,
        string? current = null)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<FilmRuns.RunName>();
        foreach (string name in names)
        {
            if (FilmRuns.ParseDirName(name) is { } run) parsed.Add(run);
            else keep.Add(name);
        }

        parsed.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
        foreach (FilmRuns.RunName run in parsed.Take(keepRecent)) keep.Add(run.Name);
        foreach (FilmRuns.RunName run in parsed.Where(run => run.Tag != null).Take(keepTagged)) keep.Add(run.Name);
        if (current != null && names.Contains(current, StringComparer.Ordinal)) keep.Add(current);
        return keep;
    }

    internal sealed record Outcome(int RunsDeleted, int FramesDeleted, IReadOnlyList<string> Failures);

    internal static Outcome Prune(string runsDir, Quota quota, string current)
    {
        if (!Directory.Exists(runsDir)) return new Outcome(0, 0, []);
        string[] names = [.. Directory.GetDirectories(runsDir).Select(Path.GetFileName).OfType<string>()];
        HashSet<string> keep = SelectKeep(names, quota.KeepRecent, quota.KeepTagged, current);
        var failures = new List<string>();
        int runsDeleted = 0;
        foreach (string name in names.Where(name => !keep.Contains(name)))
        {
            if (TryDelete(Path.Combine(runsDir, name), failures)) runsDeleted++;
        }

        string[] framesKeep =
        [
            .. names.Where(keep.Contains).Select(FilmRuns.ParseDirName).OfType<FilmRuns.RunName>()
                .Select(run => run.Name).OrderByDescending(name => name, StringComparer.Ordinal)
                .Take(quota.KeepFramesRuns),
        ];
        int framesDeleted = 0;
        foreach (string name in names.Where(name => keep.Contains(name) && FilmRuns.ParseDirName(name) != null
                                                    && name != current && !framesKeep.Contains(name)))
        {
            foreach (string frames in Directory.GetDirectories(Path.Combine(runsDir, name),
                         "*" + RimRunner.RimRunnerDataLayout.FilmFramesSuffix))
            {
                if (TryDelete(frames, failures)) framesDeleted++;
            }
        }

        return new Outcome(runsDeleted, framesDeleted, failures);
    }

    private static bool TryDelete(string dir, List<string> failures)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{dir}：{exception.Message}");
            return false;
        }
    }
}
