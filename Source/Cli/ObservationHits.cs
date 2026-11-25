namespace RimRunnerCli;

internal static class ObservationHits
{
    internal const string PinnedSubDirName = "observed";
    internal const string LogFileName = "observation-hits.jsonl";
    internal const string KeyField = "key";
    internal const string TimeFormat = "yyyy-MM-ddTHH:mm:ss";
    internal const string HitMark = "[观察命中]";
    private const int LineCap = 240;

    internal const string LogExtension = ".log";

    internal const string IsolatedRootVariable = "RIMRUNNER_ISOLATED_ROOT";

    internal sealed record Hit(string Key, int Lines, string FirstLine);

    internal static string? MachineRoot(string? isolatedRoot, string profile)
    {
        if (!string.IsNullOrWhiteSpace(isolatedRoot))
        {
            return isolatedRoot.Trim();
        }

        return string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".rimrunner");
    }

    internal static string LogPath(string machineRoot) => Path.Combine(machineRoot, LogFileName);

    internal static List<Hit> Match(string text)
    {
        var hits = new List<Hit>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith(HitMark, StringComparison.Ordinal)) continue;
            foreach (string key in KeysIn(line).Distinct(StringComparer.Ordinal))
            {
                int index = hits.FindIndex(hit => hit.Key == key);
                if (index >= 0)
                {
                    hits[index] = hits[index] with { Lines = hits[index].Lines + 1 };
                    continue;
                }

                string first = line.Trim();
                hits.Add(new Hit(key, 1, first.Length > LineCap ? first[..LineCap] + "…" : first));
            }
        }

        return hits;
    }

    private static IEnumerable<string> KeysIn(string line)
    {
        string head = RimRunner.RimRunnerObservationKeys.Head;
        for (int at = line.IndexOf(head, StringComparison.Ordinal); at >= 0;
             at = line.IndexOf(head, at + head.Length, StringComparison.Ordinal))
        {
            int start = at + head.Length;
            int end = line.IndexOf(']', start);
            if (end <= start) continue;
            string key = line[start..end];
            if (key.All(c => char.IsAsciiLetterLower(c) || c == '-')) yield return key;
        }
    }
}
