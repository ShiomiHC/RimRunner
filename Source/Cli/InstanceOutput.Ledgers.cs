using System.Text.Json;
using System.Text.Json.Nodes;
using RimRunner;

namespace RimRunnerCli;

internal sealed partial class InstanceOutput
{
    private readonly Dictionary<string, LedgerWrite> pendingLedgers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> ledgerFailures = new(StringComparer.Ordinal);

    internal void AcceptLedger(JsonElement root)
    {
        string path = PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.Path) ?? "";
        var cells = new List<KeyValuePair<string, List<string>>>();
        if (root.TryGetProperty(RimRunnerPipeProtocol.Keys.Cells, out JsonElement map)
            && map.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in map.EnumerateObject())
                cells.Add(new(property.Name, Strings(property.Value)));
        }

        var write = new LedgerWrite(path, PipeJson.TextOrNull(root, RimRunnerPipeProtocol.Keys.Label) ?? path,
            root.TryGetProperty(RimRunnerPipeProtocol.Keys.Cap, out JsonElement cap) && cap.TryGetInt32(out int width)
                ? width
                : 0,
            cells,
            root.TryGetProperty(RimRunnerPipeProtocol.Keys.Drop, out JsonElement drop) ? Strings(drop) : []);
        if (!queue.Writer.TryWrite(write))
            Record(new Fault(path, "实例管道已关闭，台账追加未写入", Quiet: false));
    }

    private static List<string> Strings(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : [];

    private void ApplyLedger(LedgerWrite write)
    {
        if (pendingLedgers.Remove(write.Path, out LedgerWrite? earlier)) write = earlier.Then(write);
        try
        {
            string full = Resolve(write.Path);
            LedgerBook.ExclusiveForExecutor(full, () =>
            {
                Dictionary<string, List<string>> book;
                try
                {
                    book = LedgerBook.Read(full);
                }
                catch (InvalidDataException exception)
                {
                    FailuresOf(write.Label).Add(write.Label + "读取失败，已按空台账重建：" + exception.Message);
                    book = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                }

                foreach (string key in write.Drop) book.Remove(key);
                foreach ((string key, List<string> cells) in write.Cells) LedgerBook.Append(book, key, cells, write.Cap);
                LedgerBook.Write(full, book);
            }, ledgerLockTimeout);
        }
        catch (Exception exception) when (IsWriteFault(exception) || exception is StoreException)
        {
            pendingLedgers[write.Path] = write;
            FailuresOf(write.Label).Add(write.Label + "写入失败：" + exception.GetType().Name + "：" + exception.Message);
        }
    }

    private void DivertLedger(ShardTarget target, LedgerWrite write)
    {
        try
        {
            target.Sink.Add(target.Index, Resolve(write.Path), write.Label, write.Cells, write.Drop);
        }
        catch (InvalidDataException exception)
        {
            FailuresOf(write.Label).Add(write.Label + "路径不合法，本次追加未交给分片组：" + exception.Message);
        }
    }

    private List<string> FailuresOf(string label)
    {
        if (!ledgerFailures.TryGetValue(label, out List<string>? lines))
        {
            lines = [];
            ledgerFailures[label] = lines;
        }

        return lines;
    }

    private List<string> SettleLedgers()
    {
        foreach (string path in pendingLedgers.Keys.ToList())
        {
            pendingLedgers.Remove(path, out LedgerWrite? write);
            ApplyLedger(write!);
        }

        var lines = new List<string>();
        foreach ((string label, List<string> failures) in ledgerFailures)
        {
            int missing = pendingLedgers.Values.Where(write => write.Label == label)
                .Sum(write => write.Cells.Sum(pair => pair.Value.Count));
            lines.Add(missing > 0
                ? label + "：" + missing + " 格未写入台账文件（本轮报告不受影响）"
                : label + "：以下写入失败均已由后续重试补上，磁盘台账完整");
            lines.AddRange(failures);
        }

        ledgerFailures.Clear();
        return lines;
    }

    private static byte[] WithLedgerFaults(byte[] json, List<string> lines)
    {
        if (lines.Count == 0) return json;
        JsonNode root = JsonNode.Parse(json) ?? throw new InvalidDataException("报告不是 JSON 对象");
        if (root[RimRunnerWireKeys.Report.LedgerFaults] is not JsonArray faults)
        {
            faults = [];
            root[RimRunnerWireKeys.Report.LedgerFaults] = faults;
        }

        foreach (string line in lines) faults.Add(line);
        return JsonSerializer.SerializeToUtf8Bytes(root, ReportJsonOptions);
    }

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record LedgerWrite(string Path, string Label, int Cap,
        List<KeyValuePair<string, List<string>>> Cells, List<string> Drop) : Item
    {
        internal LedgerWrite Then(LedgerWrite later) => later with
        {
            Cells = [.. Cells.Where(pair => !later.Drop.Contains(pair.Key, StringComparer.Ordinal)), .. later.Cells],
            Drop = [.. Drop.Union(later.Drop, StringComparer.Ordinal)],
        };
    }
}
