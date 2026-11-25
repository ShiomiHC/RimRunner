using System.Security.Cryptography;
using System.Text;

namespace RimRunnerCli;

internal static class SampleStore
{
    internal const string JsonlName = "jsonl";
    internal const string SqliteName = "sqlite";
    internal const string JsonlExtension = ".jsonl";
    internal const string MigratedSuffix = ".migrated";

    internal enum Kind { Jsonl, Sqlite }

    internal static Kind? ParseKind(string? text) => text switch
    {
        JsonlName => Kind.Jsonl,
        SqliteName => Kind.Sqlite,
        _ => null,
    };

    internal static string NameOf(Kind kind) => kind == Kind.Sqlite ? SqliteName : JsonlName;

    internal static Kind KindFor(string productRoot)
    {
        string root = TreeScope.Of(productRoot).MainOrSelfProductRoot;
        return ParseKind(OrchestratorDescriptor.Load(root).Descriptor.Samples.Store) ?? Kind.Jsonl;
    }

    internal static string DirOf(string outputRoot) =>
        Path.Combine(outputRoot, RimRunner.RimRunnerDataLayout.SamplesDirName);

    internal static string DatabaseOf(string outputRoot) => Path.Combine(DirOf(outputRoot), SqliteSampleStore.FileName);

    internal static Kind? Present(string outputRoot, out bool both)
    {
        string dir = DirOf(outputRoot);
        bool jsonl = Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*" + JsonlExtension).Any();
        bool sqlite = File.Exists(DatabaseOf(outputRoot));
        both = jsonl && sqlite;
        return sqlite ? Kind.Sqlite : jsonl ? Kind.Jsonl : null;
    }

    internal static string? MixedFault(string outputRoot, Kind kind)
    {
        Kind? present = Present(outputRoot, out bool both);
        if (both)
            return $"读数库 {DirOf(outputRoot)} 里 jsonl 与 SQLite 两种格式并存";
        if (present is { } other && other != kind)
            return $"读数库 {DirOf(outputRoot)} 是 {NameOf(other)}，本产品选的是 {NameOf(kind)}";
        return null;
    }

    internal sealed class Dropped
    {
        internal bool Bom { get; set; }
        internal int Duplicates { get; set; }
        internal int Unparsed { get; set; }
    }

    internal static IEnumerable<string> ReadJsonl(string path, Dropped dropped)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool first = true;
        foreach (string raw in File.ReadLines(path, new UTF8Encoding(false)))
        {
            string line = raw;
            if (first && line.StartsWith('\uFEFF'))
            {
                dropped.Bom = true;
                line = line[1..];
            }

            line = line.Trim();
            first = false;
            if (line.Length == 0) continue;
            if (!seen.Add(System.Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(line)))))
            {
                dropped.Duplicates++;
                continue;
            }

            if (!SampleLine.Parses(line))
            {
                dropped.Unparsed++;
                continue;
            }

            yield return line;
        }
    }

    internal sealed record Outcome(int Cases, long Lines, int Duplicates, int Unparsed);

    internal static Outcome Convert(string outputRoot, Kind to, Action<string> log)
    {
        string dir = DirOf(outputRoot);
        Kind? present = Present(outputRoot, out bool both);
        if (both) throw new InvalidDataException($"读数库 {dir} 里两种格式并存，无从判断以哪份为准");
        if (present == null || present == to) return new Outcome(0, 0, 0, 0);
        return to == Kind.Sqlite ? ToSqlite(outputRoot, log) : ToJsonl(outputRoot, log);
    }

    private static Outcome ToSqlite(string outputRoot, Action<string> log)
    {
        string dir = DirOf(outputRoot);
        string target = DatabaseOf(outputRoot);
        string staging = target + ".converting";
        DeleteDatabase(staging);
        string[] files = [.. Directory.GetFiles(dir, "*" + JsonlExtension).Order(StringComparer.Ordinal)];
        long lineCount = 0;
        int duplicates = 0, unparsed = 0;
        try
        {
            using (SqliteSampleStore store = SqliteSampleStore.Open(staging))
            {
                foreach (string file in files)
                {
                    string caseId = Path.GetFileNameWithoutExtension(file);
                    var dropped = new Dropped();
                    try
                    {
                        store.AppendMany(ReadJsonl(file, dropped).Select(line => (caseId, line)));
                    }
                    catch (InvalidDataException exception)
                    {
                        throw new InvalidDataException($"{file}：{exception.Message}", exception);
                    }

                    long count = Verify(ReadJsonl(file, new Dropped()), store.Lines(caseId).Select(stored => stored.Line),
                        $"{file} 转入 SQLite 后还原出的行与原文件不匹配");
                    duplicates += dropped.Duplicates;
                    unparsed += dropped.Unparsed;
                    lineCount += count;
                    log($"  {caseId}：{count} 行"
                        + (dropped.Bom ? "，删除首行 BOM" : "")
                        + (dropped.Duplicates > 0 ? $"，删除逐字重复 {dropped.Duplicates} 行" : "")
                        + (dropped.Unparsed > 0 ? $"，无法解析的 {dropped.Unparsed} 行未转" : ""));
                }
            }

            File.Move(staging, target);
        }
        catch
        {
            DeleteDatabase(staging);
            throw;
        }

        foreach (string file in files) File.Move(file, file + MigratedSuffix);
        return new Outcome(files.Length, lineCount, duplicates, unparsed);
    }

    private static Outcome ToJsonl(string outputRoot, Action<string> log)
    {
        string dir = DirOf(outputRoot);
        string source = DatabaseOf(outputRoot);
        var staged = new List<(string Staging, string Target)>();
        long lineCount = 0;
        try
        {
            using (SqliteSampleStore store = SqliteSampleStore.Open(source))
            {
                foreach (string caseId in store.CaseIds())
                {
                    string target = Path.Combine(dir, caseId + JsonlExtension);
                    string staging = target + ".converting";
                    staged.Add((staging, target));
                    using (var writer = new StreamWriter(staging, false, new UTF8Encoding(false)))
                    {
                        writer.NewLine = "\n";
                        foreach (SqliteSampleStore.StoredLine stored in store.Lines(caseId)) writer.WriteLine(stored.Line);
                    }

                    long count = Verify(ReadJsonl(staging, new Dropped()), store.Lines(caseId).Select(stored => stored.Line),
                        $"用例 {caseId} 转成 jsonl 后读回的行与库里不匹配");
                    lineCount += count;
                    log($"  {caseId}：{count} 行");
                }
            }

            foreach ((string staging, string target) in staged) File.Move(staging, target);
        }
        catch
        {
            foreach ((string staging, _) in staged) File.Delete(staging);
            throw;
        }

        MoveDatabase(source, source + MigratedSuffix);
        return new Outcome(staged.Count, lineCount, 0, 0);
    }

    private static long Verify(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        long count = 0;
        using IEnumerator<string> left = expected.GetEnumerator();
        using IEnumerator<string> right = actual.GetEnumerator();
        while (true)
        {
            bool hasLeft = left.MoveNext(), hasRight = right.MoveNext();
            if (!hasLeft && !hasRight) return count;
            if (hasLeft != hasRight || !string.Equals(left.Current, right.Current, StringComparison.Ordinal))
                throw new InvalidDataException(message);
            count++;
        }
    }

    private static void DeleteDatabase(string path)
    {
        foreach (string file in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }

    private static void MoveDatabase(string path, string to)
    {
        File.Move(path, to);
        foreach (string suffix in new[] { "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }
}
