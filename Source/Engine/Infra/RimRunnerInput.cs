using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RimRunner;

/// <summary>数据根下输入件的读取入口：指定的（`run --input`）优先，其次本树，再沿借读链取主树的。</summary>
/// <remarks>每次调用都重读，读到的来源与正文哈希记进报告的 inputs；跨批次缓存读到的正文会让 `--input` 对后面的批不生效，CLI 按「没有用例读到」报错。</remarks>
public static class RimRunnerInput
{
    private static readonly Lock SyncRoot = new();

    private static Dictionary<string, string>? specified;

    private static bool recording;

    private static readonly List<RimRunnerInputRead> reads = [];

    /// <summary>读一份必需的输入件，各来源都没有即抛。</summary>
    /// <param name="relativePath">数据根下的相对路径，`/` 或 `\` 分隔，不得是绝对路径、不得含 `..`。</param>
    /// <param name="missingHint">缺席时断言消息里告诉读者怎么补上（例如生成它的命令）。</param>
    /// <returns>输入件正文。</returns>
    public static string Read(string relativePath, string missingHint)
    {
        if (TryRead(relativePath, out string text))
        {
            return text;
        }

        RimRunnerFault.Require(false, "输入件不在数据根下——" + missingHint, Path.Combine(RimRunnerPaths.DataRoot, relativePath));
        throw new FileNotFoundException(relativePath);
    }

    /// <summary>读一份可选的输入件。</summary>
    /// <param name="relativePath">数据根下的相对路径，写法同 Read。</param>
    /// <param name="text">输入件正文；各来源都没有时为空串。</param>
    /// <returns>读到即 true；指定、本树与借读链都没有为 false。</returns>
    public static bool TryRead(string relativePath, out string text)
    {
        string key = NormalizeOrThrow(relativePath);
        lock (SyncRoot)
        {
            if (specified != null && specified.TryGetValue(key, out string? given))
            {
                text = given;
                Note(key, RimRunnerWireKeys.Report.InputSpecified, text);
                return true;
            }
        }

        string own = Path.Combine(RimRunnerPaths.DataRoot, key);
        if (Present(own))
        {
            text = File.ReadAllText(own);
            Note(key, RimRunnerWireKeys.Report.InputOwnTree, text);
            return true;
        }

        foreach (RimRunnerBorrowLayer layer in RimRunnerPaths.BorrowLayers)
        {
            string borrowed = Path.Combine(layer.DataRoot, key);
            if (Present(borrowed))
            {
                text = File.ReadAllText(borrowed);
                Note(key, layer.Name, text);
                return true;
            }
        }

        text = "";
        return false;
    }

    private static bool Present(string path) => RimRunnerPathPresence.Of(path) switch
    {
        PathPresence.Present => true,
        PathPresence.Absent => false,
        _ => throw new IOException(RimRunnerLog.Prefix + " 无法确认输入件是否存在（查询失败），不回落到借读层：" + path),
    };

    private static string NormalizeOrThrow(string relativePath) =>
        RimRunnerWireKeys.RunRequest.NormalizeInputPath(relativePath, out string? problem)
        ?? throw new ArgumentException(RimRunnerLog.Prefix + " 输入件路径" + problem + "：" + relativePath,
            nameof(relativePath));

    internal static void BeginRun(IReadOnlyList<KeyValuePair<string, string>> given)
    {
        lock (SyncRoot)
        {
            specified = given.Count == 0 ? null : given.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            recording = true;
            reads.Clear();
        }
    }

    internal static void EndRun()
    {
        lock (SyncRoot)
        {
            specified = null;
            recording = false;
            reads.Clear();
        }
    }

    internal static T WithSpecified<T>(IReadOnlyList<KeyValuePair<string, string>> given, Func<T> query)
    {
        lock (SyncRoot)
        {
            specified = given.Count == 0 ? null : given.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        try
        {
            return query();
        }
        finally
        {
            lock (SyncRoot)
            {
                specified = null;
            }
        }
    }

    private static void Note(string key, string source, string text)
    {
        string? caseId = RimRunnerSession.CurrentCaseId;
        lock (SyncRoot)
        {
            if (!recording)
            {
                return;
            }

            string hash = Hash(text);
            RimRunnerInputRead? row = reads.FirstOrDefault(read => read.path == key && read.source == source && read.hash == hash);
            if (row == null)
            {
                row = new RimRunnerInputRead { path = key, source = source, hash = hash };
                reads.Add(row);
            }

            if (caseId != null && !row.cases.Contains(caseId))
            {
                row.cases.Add(caseId);
            }
        }
    }

    internal static List<RimRunnerInputRead> Snapshot()
    {
        lock (SyncRoot)
        {
            return [.. reads.Select(read => new RimRunnerInputRead
            {
                path = read.path, source = read.source, hash = read.hash, cases = [.. read.cases]
            })];
        }
    }

    internal static List<string> UnreadSpecified()
    {
        lock (SyncRoot)
        {
            if (specified == null)
            {
                return [];
            }

            return [.. specified.Keys
                .Where(key => !reads.Any(read => read.path == key && read.source == RimRunnerWireKeys.Report.InputSpecified))
                .OrderBy(key => key, StringComparer.Ordinal)];
        }
    }

    private static string Hash(string text)
    {
        using SHA256 sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Take(6).Select(b => b.ToString("x2")));
    }
}

internal sealed class RimRunnerInputRead
{
    public required string path;
    public required string source;
    public required string hash;
    public List<string> cases = [];
}
