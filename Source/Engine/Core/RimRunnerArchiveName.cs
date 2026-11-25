using System;
using System.Collections.Generic;
using System.Globalization;

namespace RimRunner;

public static class RimRunnerArchiveName
{
    public const string PassedMark = "PASS";
    public const string FailedMark = "FAIL";
    public const string RejectedMark = "REJECTED";

    public const char SentinelFaultGlyph = 'S';
    public const char EngineFaultGlyph = 'E';
    public const char NoAssertionsGlyph = 'n';
    public const char IncompleteGlyph = 'i';

    public const int BatchKeepRecent = 20;
    public const int BatchKeepFailed = 10;
    public const int SingleKeepRecent = 10;
    public const int SingleKeepFailed = 5;

    public const int MaxAgeDays = 30;

    public static string AgeCutoff(DateTime now) =>
        now.AddDays(-MaxAgeDays).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static string Format(string timestamp, string mark,
        int fail, int unclaimedFault, int sentinelFault, int engineFault, int noAssertions, int incomplete,
        string batchTag, string runId)
    {
        string counts = mark == FailedMark
            ? "-" + FormatCounts(fail, unclaimedFault, sentinelFault, engineFault, noAssertions, incomplete)
            : "";

        return timestamp
               + "-" + mark
               + counts
               + "-" + batchTag
               + "-" + runId
               + ".json";
    }

    internal const string CountsLegend = "fail / unclaimedFault / sentinelFault / engineFault / noAssertions / incomplete";

    public static string FormatCounts(int fail, int unclaimedFault, int sentinelFault, int engineFault, int noAssertions,
        int incomplete)
    {
        return fail.ToString(CultureInfo.InvariantCulture)
               + RimRunnerOutcomes.GlyphFail
               + unclaimedFault.ToString(CultureInfo.InvariantCulture)
               + RimRunnerOutcomes.GlyphUnclaimedFault
               + sentinelFault.ToString(CultureInfo.InvariantCulture)
               + SentinelFaultGlyph
               + engineFault.ToString(CultureInfo.InvariantCulture)
               + EngineFaultGlyph
               + noAssertions.ToString(CultureInfo.InvariantCulture)
               + NoAssertionsGlyph
               + incomplete.ToString(CultureInfo.InvariantCulture)
               + IncompleteGlyph;
    }

    public static RimRunnerArchiveParts? Parse(string fileNameOrPath)
    {
        string[] s = StripDirectoryAndExtension(fileNameOrPath).Split('-');

        if (s.Length < 6) return null;
        if (!IsDigits(s[0], 8) || !IsDigits(s[1], 6) || !IsDigits(s[2], 3)) return null;
        if (s[3] is not PassedMark and not FailedMark and not RejectedMark) return null;

        bool hasCounts = TryCounts(s[4], out int fail, out int unclaimed, out int sentinel, out int engine,
            out int noAssertions, out int incomplete);
        if (!hasCounts) fail = unclaimed = sentinel = engine = noAssertions = incomplete = 0;
        int tagStart = hasCounts ? 5 : 4;

        if (tagStart > s.Length - 2) return null;

        return new RimRunnerArchiveParts
        {
            Timestamp = s[0] + "-" + s[1] + "-" + s[2],
            Failed = s[3] != PassedMark,
            Rejected = s[3] == RejectedMark,
            HasCounts = hasCounts,
            Fail = fail,
            UnclaimedFault = unclaimed,
            SentinelFault = sentinel,
            EngineFault = engine,
            NoAssertions = noAssertions,
            Incomplete = incomplete,
            BatchTag = string.Join("-", s, tagStart, s.Length - 1 - tagStart),
            RunId = s[^1],
        };
    }

    private static bool IsDigits(string text, int length)
    {
        if (text.Length != length) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is < '0' or > '9') return false;
        }

        return true;
    }

    private static bool TryCounts(string text, out int fail, out int unclaimed, out int sentinel, out int engine,
        out int noAssertions, out int incomplete)
    {
        int i = 0;
        sentinel = engine = noAssertions = incomplete = 0;
        if (!TryNumberThen(text, ref i, RimRunnerOutcomes.GlyphFail, out fail)) { unclaimed = 0; return false; }
        if (!TryNumberThen(text, ref i, RimRunnerOutcomes.GlyphUnclaimedFault, out unclaimed)) return false;
        sentinel = OptionalCount(text, ref i, SentinelFaultGlyph);
        engine = OptionalCount(text, ref i, EngineFaultGlyph);
        if (!TryNumberThen(text, ref i, NoAssertionsGlyph, out noAssertions)) return false;
        if (i == text.Length) return true;
        return TryNumberThen(text, ref i, IncompleteGlyph, out incomplete) && i == text.Length;
    }

    private static int OptionalCount(string text, ref int i, char terminator)
    {
        int before = i;
        if (TryNumberThen(text, ref i, terminator, out int value))
        {
            return value;
        }

        i = before;
        return 0;
    }

    private static bool TryNumberThen(string text, ref int i, char terminator, out int value)
    {
        value = 0;
        int start = i;
        while (i < text.Length && text[i] >= '0' && text[i] <= '9')
        {
            value = value * 10 + (text[i] - '0');
            i++;
        }

        if (i == start || i >= text.Length || text[i] != terminator) return false;
        i++;
        return true;
    }

    public static bool IsFailed(string fileNameOrPath)
    {
        string name = StripDirectoryAndExtension(fileNameOrPath);
        string[] segments = name.Split('-');
        return segments.Length > 3
               && (segments[3] == FailedMark || segments[3] == RejectedMark);
    }

    public static string[] SelectKeep(string[] names, int keepRecent, int keepFailed)
    {
        if (names.Length == 0)
        {
            return [];
        }

        string[] sorted = (string[])names.Clone();
        Array.Sort(sorted, StringComparer.Ordinal);

        var keep = new List<string>();
        var seen = new HashSet<string>(
            StringComparer.Ordinal);

        for (int i = sorted.Length - 1; i >= 0 && keep.Count < keepRecent; i--)
        {
            if (seen.Add(sorted[i])) keep.Add(sorted[i]);
        }

        int failedKept = 0;
        for (int i = sorted.Length - 1; i >= 0 && failedKept < keepFailed; i--)
        {
            if (!IsFailed(sorted[i])) continue;

            failedKept++;
            if (seen.Add(sorted[i])) keep.Add(sorted[i]);
        }

        return keep.ToArray();
    }

    public static string[] SelectKeep(string[] names, int keepRecent, int keepFailed, string cutoff, bool keepNewest)
    {
        string[] keep = SelectKeep(names, keepRecent, keepFailed);
        string? newest = null;
        foreach (string name in names)
        {
            if (newest == null || string.CompareOrdinal(StripDirectoryAndExtension(name),
                    StripDirectoryAndExtension(newest)) > 0)
                newest = name;
        }

        var aged = new List<string>();
        foreach (string name in keep)
        {
            string stem = StripDirectoryAndExtension(name);
            if (string.CompareOrdinal(stem.Substring(0, Math.Min(8, stem.Length)), cutoff) >= 0
                || (keepNewest && name == newest))
                aged.Add(name);
        }

        return aged.ToArray();
    }

    private static string StripDirectoryAndExtension(string path)
    {
        int start = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] is '\\' or '/') start = i + 1;
        }

        int end = path.Length;
        for (int i = path.Length - 1; i > start; i--)
        {
            if (path[i] == '.') { end = i; break; }
        }

        return path[start..end];
    }
}

public sealed record RimRunnerArchiveParts
{
    public required string Timestamp { get; init; }

    public bool Failed { get; init; }

    public bool Rejected { get; init; }

    public bool HasCounts { get; init; }

    public int Fail { get; init; }
    public int UnclaimedFault { get; init; }
    public int SentinelFault { get; init; }
    public int EngineFault { get; init; }
    public int NoAssertions { get; init; }
    public int Incomplete { get; init; }

    public required string BatchTag { get; init; }

    public required string RunId { get; init; }
}
