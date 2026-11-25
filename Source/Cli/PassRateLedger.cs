using System.Text.Json;

namespace RimRunnerCli;

public sealed class PassRateLedger(string path)
{
    public sealed class LedgerEntry
    {
        public string CaseId { get; set; } = "";
        public List<string> Outcomes { get; set; } = [];
    }

    private readonly string path = path;

    public List<LedgerEntry> Read()
    {
        if (!File.Exists(path))
        {
            throw new StoreException($"通过率台账不存在：{path}");
        }

        var entries = new List<LedgerEntry>();
        JsonDocument document;
        try
        {
            document = StrictJson.ParseObject(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new StoreException($"通过率台账不是合法 JSON：{path}\n  {exception.Message}");
        }

        using (document)
        {
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                var outcomes = new List<string>();
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    outcomes.AddRange(property.Value.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString() ?? ""));
                }

                entries.Add(new LedgerEntry { CaseId = property.Name, Outcomes = outcomes });
            }
        }

        entries.Sort((left, right) => string.CompareOrdinal(left.CaseId, right.CaseId));
        return entries;
    }

    public const string EpochMark = RimRunner.RimRunnerLedgerFold.EpochMark;
    public const string SaveMark = RimRunner.RimRunnerLedgerFold.SaveMark;

    public int MarkEpoch(string caseId) => MarkEpoch([caseId])[caseId];

    public Dictionary<string, int> MarkEpoch(IReadOnlyList<string> caseIds)
    {
        if (caseIds.Count == 0)
            throw new StoreException("用例 Id 名单为空");

        string[] duplicated =
        [
            .. caseIds.GroupBy(id => id, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key)
        ];
        if (duplicated.Length > 0)
        {
            throw new StoreException(
                $"名单里有重复的用例 Id：{string.Join("、", duplicated)}");
        }

        List<LedgerEntry> entries = Read();
        var targets = new List<LedgerEntry>();
        foreach (string caseId in caseIds)
        {
            LedgerEntry? target = entries.FirstOrDefault(entry =>
                string.Equals(entry.CaseId, caseId, StringComparison.Ordinal)) ?? throw new StoreException(
                    $"台账里没有用例 {caseId}：{path}");

            if (target.Outcomes.Count > 0
                && string.Equals(target.Outcomes[^1], EpochMark, StringComparison.Ordinal))
            {
                throw new StoreException(
                    $"{caseId} 的历史末尾已经是纪元标记：{path}");
            }

            targets.Add(target);
        }

        var behind = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (LedgerEntry target in targets)
        {
            behind[target.CaseId] = EpochCount(target.Outcomes);
            target.Outcomes.Add(EpochMark);
        }

        Write(entries);
        return behind;
    }

    public static int EpochCount(List<string> outcomes)
    {
        int last = outcomes.LastIndexOf(EpochMark);
        return outcomes.Skip(last + 1).Count(cell => !RimRunner.RimRunnerLedgerFold.IsMark(cell));
    }

    private void Write(List<LedgerEntry> entries)
    {
        var payload = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (LedgerEntry entry in entries)
        {
            payload[entry.CaseId] = entry.Outcomes;
        }

        RimTooling.IO.AtomicFile.Write(path, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
