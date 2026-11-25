using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimRunner;

/// <summary>可分片用例契约：只许读数用例实现，多个分片实例从同一组队列认领格同时执行。</summary>
/// <remarks>Kind=Regression 登记期被拒。取格只经 ctx.TryTakeShardCell，格执行完调租约的 Complete。声明 QuietMachine 时命令行不分片。</remarks>
public interface IRimRunnerShardable
{
    /// <summary>枚举全部格：键在本用例内唯一、跨进程稳定，权重为非负的预估耗时。</summary>
    /// <remarks>在已进档的世界上调，只读：不推 tick、不生成。ctx 不带条目与清理栈，诊断等写条目的入口与 Defer、Track* 当场抛。键非空、不含换行。用例体内可再调它取全集。ctx.Map 在 World 档为当前地图，其余为 null。</remarks>
    IReadOnlyList<RimRunnerShardCell> ShardCells(RimRunnerContext ctx);

    /// <summary>每片权重下限：片数不超过总权重除以本值（至少 1 片）。</summary>
    /// <remarks>须为正有限数，单位与格权重相同。</remarks>
    double MinShardWeight { get; }
}

/// <summary>可分片用例的一格：键与权重。</summary>
/// <param name="key">格键：本用例内唯一、跨进程稳定。</param>
/// <param name="weight">该格大概要执行多久的预估，单位由用例定。</param>
public sealed class RimRunnerShardCell(string key, double weight)
{
    /// <summary>格键，在本用例内唯一、跨进程稳定。</summary>
    public string Key { get; } = key;

    /// <summary>权重：预估耗时的相对量，非负有限数，单位由用例定。</summary>
    /// <remarks>组队列按权重降序、同权按键序排，各片依此序认领。</remarks>
    public double Weight { get; } = weight;
}

/// <summary>认领到的一格：格键，与执行完时要调的 Complete。</summary>
/// <remarks>由 ctx.TryTakeShardCell 发出。用例体执行到头时仍未完成的租约记一条 SHARD-LEASE-OPEN 警告；不分片时游标之后的格未取完，或一次都未取，记 SHARD-CELLS-UNTAKEN。</remarks>
public sealed class RimRunnerShardLease
{
    private readonly int ordinal;

    internal RimRunnerShardLease(string key, int ordinal)
    {
        Key = key;
        this.ordinal = ordinal;
    }

    /// <summary>格键，与 ShardCells 枚举出的键相同。</summary>
    public string Key { get; }

    /// <summary>是否已调过 Complete。</summary>
    public bool Completed { get; private set; }

    /// <summary>宣告这一格执行完。</summary>
    /// <remarks>格的最后一项工作做完时调，每份租约恰调一次；第二次调即抛。认领后放弃不执行的格也调。</remarks>
    public void Complete()
    {
        if (Completed)
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 格 " + Key + " 的租约已完成过一次");
        Completed = true;
        if (ordinal >= 0)
            RimRunnerPipeLink.Emit(RimRunnerPipeProtocol.Events.ShardDone,
                writer => writer.WriteNumber(RimRunnerPipeProtocol.Keys.Ordinal, ordinal));
    }
}

internal sealed class RimRunnerShardRequest
{
    internal required string Group;
    internal int Index;
    internal int Count;
    internal required string QueuePath;
    internal required string Fingerprint;
}

internal sealed class RimRunnerShardRun
{
    internal RimRunnerShardRequest? Request;
    internal required string CaseId;
    internal required List<string> Queue;
    internal required Dictionary<string, double> Weights;
    internal RimRunnerShardInfo? Info;
    internal Queue<int>? TestOrdinals;

    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(30);

    internal bool TryTake([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RimRunnerShardLease? cell)
    {
        RimRunnerShardInfo info = Info!;
        int ordinal = TestOrdinals == null
            ? Claim()
            : TestOrdinals.Count > 0 ? TestOrdinals.Dequeue() : -1;
        if (ordinal < 0)
        {
            cell = null;
            return false;
        }

        string key = Queue[ordinal];
        info.cells++;
        info.weight += Weights[key];
        cell = new RimRunnerShardLease(key, ordinal);
        return true;
    }

    private int Claim()
    {
        string answer = RimRunnerPipeLink.Ask(RimRunnerPipeProtocol.Events.ShardClaim, null, ClaimTimeout);
        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty(RimRunnerPipeProtocol.Keys.Error, out JsonElement error)
            && error.ValueKind == JsonValueKind.String)
        {
            throw new IOException("编排进程拒绝认领：" + error.GetString());
        }

        if (!root.TryGetProperty(RimRunnerPipeProtocol.Keys.Ordinal, out JsonElement element)
            || !element.TryGetInt32(out int ordinal) || ordinal < -1 || ordinal >= Queue.Count)
        {
            throw new IOException("无法识别编排进程答复的认领序号：" + answer);
        }

        return ordinal;
    }
}

internal static class RimRunnerShardEnumeration
{
    internal sealed class Outcome
    {
        internal required string CaseId;
        internal required Type CaseType;
        internal List<RimRunnerShardCell>? Cells;
        internal string? Fault;
        internal bool Claimed;
        internal Exception? Exception;
        internal readonly List<KeyValuePair<string, string>> Warnings = [];

        internal void PartitionWarnings(List<RimRunnerWarningEntry> open, List<RimRunnerWarningEntry> acked)
        {
            Dictionary<string, List<RimRunnerAck>> acks = RimRunnerWarnings.Acks(CaseType);
            foreach (KeyValuePair<string, string> warning in Warnings.Distinct())
            {
                string? reason = RimRunnerWarnings.Match(acks, warning.Key, null);
                (reason == null ? open : acked).Add(new RimRunnerWarningEntry
                    { subject = CaseId, key = warning.Key, text = warning.Value, reason = reason });
            }
        }
    }

    internal static Outcome Enumerate(RimRunnerTestCase testCase)
    {
        var outcome = new Outcome { CaseId = testCase.Id, CaseType = testCase.GetType() };
        if (testCase is not IRimRunnerShardable shardable)
        {
            outcome.Fault = "用例 " + testCase.Id + " 没有实现 IRimRunnerShardable";
            return outcome;
        }

        return Enumerate(shardable, testCase.Level, outcome);
    }

    internal static Outcome Enumerate(IRimRunnerShardable shardable, RimRunnerLevel level, Outcome outcome)
    {
        RimRunnerContext ctx = RimRunnerContext.CreateEnumeration(level);
        IReadOnlyList<RimRunnerShardCell>? raw = null;
        var faults = new List<string>();
        using (ctx.EnterEnumeration())
        {
            try
            {
                raw = shardable.ShardCells(ctx);
            }
            catch (RimRunnerClaimedFault claimed)
            {
                outcome.Claimed = true;
                faults.Add((claimed.Kind == RimRunnerClaimedFault.FaultKind.Precondition ? "前提不成立：" : "认领故障（Fail）：")
                           + claimed.Message);
            }
            catch (Exception exception)
            {
                outcome.Exception = exception;
                faults.Add("ShardCells 抛出 " + exception.GetType().Name + "：" + exception.Message);
            }
        }

        outcome.Warnings.AddRange(ctx.EnumerationWarnings);
        if (ctx.EnumerationFaults.Count > 0)
        {
            outcome.Claimed |= outcome.Exception == null;
            faults.AddRange(ctx.EnumerationFaults.Distinct().Select(text => "枚举期哨兵：" + text));
        }

        if (faults.Count > 0)
        {
            outcome.Fault = "用例 " + outcome.CaseId + " 枚举格不成立：" + string.Join("；", faults);
            return outcome;
        }

        outcome.Cells = Validate(outcome.CaseId, raw, out outcome.Fault);
        return outcome;
    }

    internal static List<RimRunnerShardCell>? Validate(string caseId, IReadOnlyList<RimRunnerShardCell>? raw, out string? fault)
    {
        fault = null;
        if (raw == null)
        {
            fault = "用例 " + caseId + " 的 ShardCells 返回 null";
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cells = new List<RimRunnerShardCell>(raw.Count);
        foreach (RimRunnerShardCell cell in raw)
        {
            if (cell == null || string.IsNullOrEmpty(cell.Key))
            {
                fault = "用例 " + caseId + " 的 ShardCells 含 null 格或空键";
                return null;
            }

            if (cell.Key.IndexOf('\n') >= 0 || cell.Key.IndexOf('\r') >= 0)
            {
                fault = "用例 " + caseId + " 的格键含换行：" + cell.Key.Replace("\r", "\\r").Replace("\n", "\\n");
                return null;
            }

            if (!seen.Add(cell.Key))
            {
                fault = "用例 " + caseId + " 的格键重复：" + cell.Key;
                return null;
            }

            if (double.IsNaN(cell.Weight) || double.IsInfinity(cell.Weight) || cell.Weight < 0)
            {
                fault = "用例 " + caseId + " 的格 " + cell.Key + " 权重须为非负有限数，实际 "
                        + cell.Weight.ToString("R", CultureInfo.InvariantCulture);
                return null;
            }

            cells.Add(cell);
        }

        return cells;
    }

    internal static string Fingerprint(IEnumerable<RimRunnerShardCell> cells)
    {
        StringBuilder text = new();
        foreach (RimRunnerShardCell cell in cells.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            text.Append(cell.Key).Append('\t').Append(cell.Weight.ToString("R", CultureInfo.InvariantCulture))
                .Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexStringLower(hash, 0, 8);
    }

    internal static List<string>? ReadQueue(string path, out string? fault)
    {
        fault = null;
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
        {
            fault = "组队列路径须为绝对路径，收到：" + (path ?? "<缺>");
            return null;
        }

        if (!File.Exists(path))
        {
            fault = "组队列文件不存在：" + path;
            return null;
        }

        object? parsed;
        try
        {
            parsed = RimRunnerJson.Parse(File.ReadAllText(path));
        }
        catch (Exception exception)
        {
            fault = "无法读取组队列：" + path + "：" + exception.Message;
            return null;
        }

        if (parsed is not List<object> items)
        {
            fault = "组队列须为字符串数组：" + path;
            return null;
        }

        var keys = new List<string>(items.Count);
        foreach (object item in items)
        {
            if (item is not string key || key.Length == 0)
            {
                fault = "组队列含非字符串或空键：" + path;
                return null;
            }

            keys.Add(key);
        }

        return keys;
    }

    internal static RimRunnerShardRun? Admit(RimRunnerShardRequest request, IReadOnlyList<RimRunnerTestCase> orderedCases,
        List<string> faults, List<RimRunnerWarningEntry> warnings, List<RimRunnerWarningEntry> warningsAcked)
    {
        if (request.Count < 1 || request.Index < 1 || request.Index > request.Count)
        {
            faults.Add("片号 " + request.Index + " / 片数 " + request.Count + " 不成立");
        }

        if (string.IsNullOrEmpty(request.Group))
        {
            faults.Add("缺组 id");
        }

        IReadOnlyList<RimRunnerTestCase> subjects = orderedCases;
        if (subjects.Count != 1 || subjects[0] is not IRimRunnerShardable)
        {
            faults.Add("本批作为一片执行时须恰是一条可分片用例，实际："
                       + (subjects.Count == 0 ? "没有" : string.Join("、", subjects.Select(c =>
                           c.Id + (c is IRimRunnerShardable ? "" : "（不可分片）")))));
        }

        if (faults.Count > 0)
        {
            return null;
        }

        RimRunnerTestCase subject = subjects[0];
        Outcome enumerated = Enumerate(subject);
        enumerated.PartitionWarnings(warnings, warningsAcked);
        List<RimRunnerShardCell>? cells = enumerated.Cells;
        if (cells == null)
        {
            faults.Add(enumerated.Fault!);
            return null;
        }

        List<string>? keys = ReadQueue(request.QueuePath, out string? listFault);
        if (keys == null)
        {
            faults.Add(listFault!);
            return null;
        }

        RimRunnerShardRun? run = Match(subject.Id, cells, request.Fingerprint, keys, faults);
        run?.Request = request;
        return run;
    }

    internal static RimRunnerShardRun? Match(string caseId, List<RimRunnerShardCell> cells, string expectedFingerprint,
        List<string> keys, List<string> faults)
    {
        string fingerprint = Fingerprint(cells);
        if (!string.Equals(fingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            faults.Add("本实例枚举的全集指纹 " + fingerprint + " 与下发的 " + (expectedFingerprint ?? "<缺>")
                       + " 不匹配：枚举之后格或权重已改变");
        }

        Dictionary<string, double> weights = cells.ToDictionary(c => c.Key, c => c.Weight, StringComparer.Ordinal);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        List<string> unknown = keys.Where(key => !weights.ContainsKey(key)).ToList();
        List<string> repeated = keys.Where(key => !unique.Add(key)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            faults.Add("组队列里有本实例全集之外的键 " + unknown.Count + " 个：" + string.Concat(unknown.Select(key => "\n  " + key)));
        }

        if (repeated.Count > 0)
        {
            faults.Add("组队列里有重复的键 " + repeated.Count + " 个：" + string.Concat(repeated.Select(key => "\n  " + key)));
        }

        if (keys.Count == 0)
        {
            faults.Add("组队列为空");
        }

        if (faults.Count > 0)
        {
            return null;
        }

        return new RimRunnerShardRun
        {
            CaseId = caseId,
            Queue = keys,
            Weights = weights
        };
    }
}
