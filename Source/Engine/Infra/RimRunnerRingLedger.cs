using System.IO;

namespace RimRunner;

internal sealed class RimRunnerRingLedger
{
    private readonly string? path;
    private readonly Action<IReadOnlyDictionary<string, List<string>>, IReadOnlyList<string>> persist;
    private readonly string label;
    private readonly int cap;
    private readonly int flushThreshold;
    private readonly Func<string, bool> eligible;
    private readonly HashSet<string> ineligible = new(StringComparer.Ordinal);

    private Dictionary<string, List<string>> rings;
    private Dictionary<string, List<string>> baseline;
    private readonly IReadOnlyList<KeyValuePair<string, string?>> baselinePaths;
    private readonly bool noteBorrow;

    private readonly Dictionary<string, List<string>> pending =
        new(StringComparer.Ordinal);
    private int pendingCount;

    private int persistCount;

    public RimRunnerRingLedger(string? path,
        Action<IReadOnlyDictionary<string, List<string>>, IReadOnlyList<string>> persist, string label, int cap,
        int flushThreshold, Func<string, bool> eligible,
        IReadOnlyList<KeyValuePair<string, string?>>? baselinePaths = null, bool noteBorrow = false)
    {
        this.path = path;
        this.persist = persist;
        this.label = label;
        this.cap = cap;
        this.flushThreshold = flushThreshold;
        this.eligible = eligible;
        this.baselinePaths = baselinePaths ?? [];
        this.noteBorrow = noteBorrow;
        rings = ReadFromDisk(path, label, ineligible);
        baseline = ReadBaseline();
    }

    private static readonly List<RimRunnerRingLedger> histories = [];

    public static RimRunnerRingLedger ForHistory(string stem, string? modList, string label, int cap,
        int flushThreshold, Func<string, bool> eligible)
    {
        List<KeyValuePair<string, string?>> layers = RimRunnerCompatOff.On
            ? []
            : RimRunnerPaths.BorrowLayers
                .Select(layer => new KeyValuePair<string, string?>(layer.Name, ResolvePath(layer.HistoryDir, stem, modList)))
                .ToList();
        string? fileName = RimRunnerCompatOff.On ? null : RimRunnerDataLayout.LedgerFileName(stem, modList);
        string? relative = fileName == null ? null : RimRunnerOutputPath.Join(RimRunnerPaths.HistoryPath, fileName);
        RimRunnerRingLedger ledger = new(relative == null ? null : RimRunnerOutput.Absolute(relative),
            (cells, drop) => RimRunnerOutput.Ledger(relative!, label, cap, cells, drop), label, cap, flushThreshold,
            eligible, layers, noteBorrow: true);
        histories.Add(ledger);
        return ledger;
    }

    internal static void ReloadAll()
    {
        foreach (RimRunnerRingLedger ledger in histories)
        {
            ledger.Reload();
        }
    }

    internal void Reload()
    {
        if (path != null)
        {
            Dictionary<string, List<string>> loaded = ReadFromDisk(path, label, ineligible);
            foreach (KeyValuePair<string, List<string>> pair in pending)
            {
                if (!loaded.TryGetValue(pair.Key, out List<string>? ring))
                {
                    ring = [];
                    loaded[pair.Key] = ring;
                }

                ring.AddRange(pair.Value);
                Trim(ring);
            }

            rings = loaded;
        }

        baseline = ReadBaseline();
    }

    private Dictionary<string, List<string>> ReadBaseline()
    {
        Dictionary<string, List<string>> merged = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string?> layer in baselinePaths.Reverse())
        {
            Dictionary<string, List<string>> loaded = ReadFromDisk(layer.Value, layer.Key + label, null);
            if (loaded.Count == 0)
            {
                continue;
            }

            if (noteBorrow)
            {
                RimRunnerBorrow.NoteLedger(layer.Key, label);
            }

            foreach (KeyValuePair<string, List<string>> pair in loaded)
            {
                if (!merged.TryGetValue(pair.Key, out List<string>? ring))
                {
                    ring = [];
                    merged[pair.Key] = ring;
                }

                ring.AddRange(pair.Value);
                Trim(ring);
            }
        }

        return merged;
    }

    public string? FileName => path == null ? null : Path.GetFileName(path);

    public static string? ResolvePath(string historyDir, string stem, string? modList)
    {
        string? fileName = RimRunnerDataLayout.LedgerFileName(stem, modList);
        return fileName == null ? null : Path.Combine(historyDir, fileName);
    }

    public IReadOnlyList<string> Ring(string key)
    {
        List<string> own = rings.TryGetValue(key, out List<string>? ring) ? ring : [];
        if (!baseline.TryGetValue(key, out List<string>? borrowed) || own.Count >= cap)
        {
            return own;
        }

        List<string> combined = [.. borrowed];
        combined.AddRange(own);
        Trim(combined);
        return combined;
    }

    public IEnumerable<string> Keys => baseline.Keys.Union(rings.Keys, StringComparer.Ordinal);

    public bool IsEligible(string key)
    {
        return eligible(key);
    }

    public IReadOnlyList<string>? Append(string key, string cell)
    {
        if (!eligible(key))
        {
            return null;
        }

        if (!rings.TryGetValue(key, out List<string>? ring))
        {
            ring = [];
            rings[key] = ring;
        }

        ring.Add(cell);
        Trim(ring);
        IReadOnlyList<string> current = Ring(key);

        if (!pending.TryGetValue(key, out List<string>? delta))
        {
            delta = [];
            pending[key] = delta;
        }

        delta.Add(cell);
        pendingCount++;

        if (pendingCount >= flushThreshold)
        {
            Persist();
        }

        return current;
    }

    public void Flush()
    {
        if (pendingCount > 0)
        {
            Persist();
        }

        RimRunnerEngineLog.Info(label + "：本轮写入文件 " + persistCount
                                + " 次（阈值 " + flushThreshold + " 条/次）");
        persistCount = 0;
    }

    private void Trim(List<string> ring)
    {
        if (ring.Count > cap)
        {
            ring.RemoveRange(0, ring.Count - cap);
        }
    }

    private Dictionary<string, List<string>> ReadFromDisk(string? source, string sourceLabel, HashSet<string>? dropped)
    {
        Dictionary<string, List<string>> loaded =
            new(StringComparer.Ordinal);
        if (source == null || !File.Exists(source))
        {
            return loaded;
        }

        try
        {
            if (RimRunnerJson.Parse(File.ReadAllText(source))
                    is not Dictionary<string, object> root)
            {
                return loaded;
            }

            foreach (KeyValuePair<string, object> pair in root)
            {
                if (!eligible(pair.Key))
                {
                    dropped?.Add(pair.Key);
                    continue;
                }

                List<string> ring = [];
                if (pair.Value is List<object> cells)
                {
                    foreach (object cell in cells)
                    {
                        if (cell is string text)
                        {
                            ring.Add(text);
                        }
                    }
                }

                Trim(ring);
                loaded[pair.Key] = ring;
            }
        }
        catch (Exception exception)
        {
            loaded.Clear();
            RimRunnerEngineLog.Warn(sourceLabel + "读取失败，重置历史：" + exception.Message);
        }

        return loaded;
    }

    private void Persist()
    {
        if (path != null)
        {
            persist(pending, [.. ineligible]);
            ineligible.Clear();
            persistCount++;
        }

        pending.Clear();
        pendingCount = 0;
    }
}
