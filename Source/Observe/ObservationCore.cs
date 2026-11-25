using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimRunnerCli;

internal static class ObservationCore
{
    internal const string PendingWord = "pending";

    internal const string TableFileName = "观察签名.json";
    internal const string LedgerFileName = "长期观察与冻结.md";
    internal const string PinnedSubDirName = "observed";
    internal const string CursorLabel = "命中已裁至";
    internal const string NoneCursor = "无";
    internal const string ThresholdLabel = "提醒门槛";
    internal const string RemindFromLabel = "提醒起于";
    internal const string RemindFromFormat = "yyyy-MM-dd";
    internal const string HitLogFileName = "observation-hits.jsonl";
    internal const string CursorFormat = "yyyy-MM-ddTHH:mm:ss";
    internal const string HitMark = "[观察命中]";
    internal const string FaultMark = "[签名故障]";
    private const int LineCap = 240;

    internal const string LogExtension = ".log";

    internal const string IsolatedRootVariable = "RIMRUNNER_ISOLATED_ROOT";

    internal sealed record Hit(string Section, int Lines, string FirstLine);

    internal static string? MachineRoot(string? isolatedRoot, string profile)
    {
        if (!string.IsNullOrWhiteSpace(isolatedRoot))
        {
            return isolatedRoot.Trim();
        }

        return string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".rimrunner");
    }

    internal static string HitLogPath(string machineRoot) => Path.Combine(machineRoot, HitLogFileName);

    private static string[]? ReadLines(TextWriter output, string path, string what)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return [.. lines];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"{FaultMark} 无法读取{what}：{path}：{exception.Message}");
            return null;
        }
    }

    internal static int Pending(TextWriter output, string tablePath, string? ledgerPath, string hitLogPath,
        DateTime? today = null)
    {
        if (!File.Exists(tablePath))
        {
            output.WriteLine($"{FaultMark} 签名表不存在：{tablePath}");
            return ExitCodes.Incomplete;
        }

        ledgerPath ??= Path.Combine(Path.GetDirectoryName(tablePath)!, LedgerFileName);
        if (!File.Exists(ledgerPath))
        {
            output.WriteLine($"{FaultMark} 账不存在：{ledgerPath}");
            return ExitCodes.Incomplete;
        }

        if (ReadLines(output, ledgerPath, "账") is not { } ledgerLines) return ExitCodes.Incomplete;

        bool faulted = false;
        var cursors = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        var thresholds = new Dictionary<string, int>(StringComparer.Ordinal);
        var muted = new HashSet<string>(StringComparer.Ordinal);
        DateTime date = (today ?? DateTime.Now).Date;
        var order = new List<string>();
        foreach ((string section, string? cursor, string? threshold, string? remindFrom) in Cursors(ledgerLines))
        {
            order.Add(section);
            if (remindFrom != null)
            {
                if (DateTime.TryParseExact(remindFrom, RemindFromFormat, null,
                        System.Globalization.DateTimeStyles.None, out DateTime from))
                {
                    if (date < from)
                    {
                        muted.Add(section);
                    }
                }
                else
                {
                    output.WriteLine($"{FaultMark} 「{section}」的「{RemindFromLabel}」不是 {RemindFromFormat}：{remindFrom}");
                    faulted = true;
                }
            }

            if (threshold != null)
            {
                if (int.TryParse(threshold, System.Globalization.NumberStyles.None, null, out int n) && n >= 1)
                {
                    thresholds[section] = n;
                }
                else
                {
                    output.WriteLine($"{FaultMark} 「{section}」的「{ThresholdLabel}」不是正整数：{threshold}");
                    faulted = true;
                }
            }

            if (cursor == null)
            {
                output.WriteLine($"{FaultMark} 「{section}」没有「{CursorLabel}」行");
                faulted = true;
                continue;
            }

            if (cursor == NoneCursor)
            {
                cursors[section] = null;
            }
            else if (DateTime.TryParseExact(cursor, CursorFormat, null,
                         System.Globalization.DateTimeStyles.None, out DateTime parsed))
            {
                cursors[section] = parsed;
            }
            else
            {
                output.WriteLine($"{FaultMark} 「{section}」的「{CursorLabel}」不是 {CursorFormat} 或「{NoneCursor}」：{cursor}");
                faulted = true;
            }
        }

        if (!File.Exists(hitLogPath))
        {
            return faulted ? ExitCodes.Incomplete : 0;
        }

        if (ReadLines(output, hitLogPath, "命中日志") is not { } hitLines) return ExitCodes.Incomplete;

        var pending = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        int lineNo = 0;
        foreach (string line in hitLines)
        {
            lineNo++;
            if (line.Trim().Length == 0)
            {
                continue;
            }

            JsonElement hit;
            DateTime at;
            string? section;
            try
            {
                hit = JsonDocument.Parse(line).RootElement.Clone();
                section = hit.GetProperty("section").GetString();
                at = DateTime.ParseExact(hit.GetProperty("at").GetString()!, CursorFormat, null);
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException
                                                  or FormatException or InvalidOperationException
                                                  or ArgumentNullException)
            {
                output.WriteLine($"{FaultMark} 命中日志第 {lineNo} 行未能读取（{hitLogPath}）：{exception.Message}");
                faulted = true;
                continue;
            }

            if (section == null || !cursors.TryGetValue(section, out DateTime? cursor) || at <= cursor)
            {
                continue;
            }

            if (!pending.TryGetValue(section, out List<JsonElement>? list))
            {
                pending[section] = list = [];
            }

            list.Add(hit);
        }

        List<string> due = [.. order.Where(s => !muted.Contains(s)
                                                && pending.TryGetValue(s, out List<JsonElement>? found)
                                                && found.Count >= thresholds.GetValueOrDefault(s, 1))];
        foreach (string section in due)
        {
            List<JsonElement> hits = pending[section];
            JsonElement last = hits[^1];
            string? Text(string name) =>
                last.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            string since = cursors[section] is DateTime c
                ? $"{CursorLabel} {c.ToString(CursorFormat)} 之后命中 {hits.Count} 次"
                : $"命中 {hits.Count} 次，一次都没裁过";
            string where = last.TryGetProperty("coordinator", out JsonElement coordinator) && coordinator.ValueKind == JsonValueKind.True
                ? "全机协调日志"
                : (Text("productRoot") ?? "") + " · " + (Text("instance") is { } instance
                    ? $"实例 {instance} 分界后"
                    : $"runId {Text("runId") ?? "无"}");
            output.WriteLine($"[观察待裁] {LedgerFileName}「{section}」—— {since}，" +
                             $"最近一次 {Text("at")} · {where}");
            output.WriteLine($"  首行：{Text("firstLine")}");
            output.WriteLine(Text("log") is string logPath
                ? $"  RimRunner 日志：{PinnedOr(logPath)}"
                : "  RimRunner 日志未落盘");
        }

        if (due.Count > 0)
        {
            output.WriteLine($"  命中日志：{hitLogPath}");
        }

        return faulted ? ExitCodes.Incomplete : due.Count > 0 ? 1 : 0;
    }

    private static string PinnedOr(string console)
    {
        if (File.Exists(console))
        {
            return console;
        }

        string pinned = Path.Combine(Path.GetDirectoryName(console) ?? "", PinnedSubDirName, Path.GetFileName(console));
        return File.Exists(pinned) ? pinned : console + "（已不在盘上）";
    }

    internal static IEnumerable<(string Section, string? Cursor, string? Threshold, string? RemindFrom)> Cursors(IEnumerable<string> ledger)
    {
        var cursorRow = new Regex(@"^\|\s*" + CursorLabel + @"\s*\|\s*(.*?)\s*\|\s*$", RegexOptions.CultureInvariant);
        var thresholdRow = new Regex(@"^\|\s*" + ThresholdLabel + @"\s*\|\s*(.*?)\s*\|\s*$", RegexOptions.CultureInvariant);
        var remindFromRow = new Regex(@"^\|\s*" + RemindFromLabel + @"\s*\|\s*(.*?)\s*\|\s*$", RegexOptions.CultureInvariant);
        string? section = null;
        string? cursor = null;
        string? threshold = null;
        string? remindFrom = null;
        foreach (string raw in ledger)
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                if (section != null)
                {
                    yield return (section, cursor, threshold, remindFrom);
                }

                section = line.StartsWith("### ", StringComparison.Ordinal) ? line[4..].Trim() : null;
                cursor = null;
                threshold = null;
                remindFrom = null;
                continue;
            }

            if (section == null) continue;
            if (cursorRow.Match(line) is { Success: true } match)
            {
                cursor = match.Groups[1].Value.Trim('`');
            }
            else if (thresholdRow.Match(line) is { Success: true } thresholdMatch)
            {
                threshold = thresholdMatch.Groups[1].Value.Trim('`');
            }
            else if (remindFromRow.Match(line) is { Success: true } remindFromMatch)
            {
                remindFrom = remindFromMatch.Groups[1].Value.Trim('`');
            }
        }

        if (section != null)
        {
            yield return (section, cursor, threshold, remindFrom);
        }
    }

    internal static int Scan(string dir, TextWriter output, string tablePath, IReadOnlyList<string> extensions)
    {
        if (!Directory.Exists(dir))
        {
            output.WriteLine($"[FAIL] 目录不存在：{dir}");
            return ExitCodes.Incomplete;
        }

        IReadOnlyList<(string Section, string Key)> table;
        try
        {
            table = Load(tablePath);
        }
        catch (SignatureException exception)
        {
            output.WriteLine($"{FaultMark} {exception.Message}");
            return ExitCodes.Incomplete;
        }

        string[] files = [.. extensions.SelectMany(
            extension => Directory.GetFiles(dir, "*" + extension, SearchOption.AllDirectories))];
        Array.Sort(files, StringComparer.Ordinal);
        int hitFiles = 0;
        foreach (string file in files)
        {
            List<Hit> hits = Match(File.ReadAllText(file), table);
            if (hits.Count == 0)
            {
                continue;
            }

            hitFiles++;
            foreach (Hit hit in hits)
            {
                output.WriteLine($"{Path.GetFileName(file)} · 「{hit.Section}」· {hit.Lines} 行 · {hit.FirstLine}");
            }
        }

        output.WriteLine($"[回扫] {files.Length} 份 · 命中 {hitFiles} 份 · 签名 {table.Count} 节");
        return 0;
    }

    internal static List<Hit> Match(string text, IReadOnlyList<(string Section, string Key)> table)
    {
        var hits = new List<Hit>();
        string[] lines = text.Split('\n');
        foreach ((string section, string key) in table)
        {
            string marker = RimRunner.RimRunnerObservationKeys.Marker(key);
            int count = 0;
            string? first = null;
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith(HitMark, StringComparison.Ordinal)
                    || line.StartsWith(FaultMark, StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains(marker, StringComparison.Ordinal))
                {
                    count++;
                    first ??= line.Trim();
                }
            }

            if (count > 0)
            {
                hits.Add(new Hit(section, count,
                    first!.Length > LineCap ? first[..LineCap] + "…" : first));
            }
        }

        return hits;
    }

    internal static IReadOnlyList<(string Section, string Key)> Load(string tablePath)
    {
        if (!File.Exists(tablePath))
        {
            throw new SignatureException($"签名表不存在：{tablePath}");
        }

        string ledgerPath = Path.Combine(Path.GetDirectoryName(tablePath)!, LedgerFileName);
        if (!File.Exists(ledgerPath))
        {
            throw new SignatureException($"签名表旁边没有 {LedgerFileName}：{ledgerPath}");
        }

        var sections = new HashSet<string>(
            File.ReadLines(ledgerPath)
                .Where(l => l.StartsWith("### ", StringComparison.Ordinal))
                .Select(l => l[4..].Trim()),
            StringComparer.Ordinal);

        Dictionary<string, string>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(tablePath));
        }
        catch (JsonException exception)
        {
            throw new SignatureException($"无法解析签名表（{tablePath}）：{exception.Message}");
        }

        var table = new List<(string, string)>();
        foreach ((string section, string key) in raw ?? [])
        {
            if (!sections.Contains(section))
            {
                throw new SignatureException(
                    $"签名表的「{section}」在 {LedgerFileName} 里没有同名小节");
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new SignatureException($"签名表的「{section}」没有键");
            }

            table.Add((section, key));
        }

        return table;
    }

    internal sealed class SignatureException(string message) : Exception(message);
}
