namespace RimRunner;

internal static class RimRunnerUnownedErrors
{
    private const int MaxHeads = 16;
    private const int HeadChars = 240;

    private static readonly System.Threading.Lock SyncRoot = new();
    private static readonly Dictionary<string, int> heads = new(StringComparer.Ordinal);
    private static readonly List<string> order = [];
    private static int sinceTake;
    private static int total;
    private static string? sustained;

    internal static int Total
    {
        get
        {
            lock (SyncRoot)
            {
                return total;
            }
        }
    }

    internal static void Note(string raw)
    {
        string head = Head(raw);
        lock (SyncRoot)
        {
            total++;
            sinceTake++;
            if (heads.TryGetValue(head, out int count))
            {
                heads[head] = count + 1;
            }
            else if (heads.Count < MaxHeads)
            {
                heads[head] = 1;
                order.Add(head);
            }
        }
    }

    internal static void MarkSustained(string text)
    {
        lock (SyncRoot)
        {
            sustained = text;
        }
    }

    internal readonly record struct Snapshot(int Count, IReadOnlyList<(string Head, int Count)> Heads, string? Sustained);

    internal static Snapshot Take()
    {
        lock (SyncRoot)
        {
            List<(string, int)> rows = order
                .Select((head, index) => (head, count: heads[head], index))
                .OrderByDescending(row => row.count).ThenBy(row => row.index)
                .Select(row => (row.head, row.count)).ToList();
            Snapshot snapshot = new(sinceTake, rows, sustained);
            heads.Clear();
            order.Clear();
            sinceTake = 0;
            sustained = null;
            return snapshot;
        }
    }

    internal static string Head(string raw)
    {
        string[] lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string head = lines.Length > 0 ? lines[0].Trim() : raw.Trim();
        string? frame = lines.Skip(1).Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("at ", StringComparison.Ordinal));
        if (frame != null)
        {
            int location = frame.IndexOf(" in ", StringComparison.Ordinal);
            head += " ← " + (location > 0 ? frame[..location] : frame);
        }

        return head.Length > HeadChars ? head[..HeadChars] + "…" : head;
    }
}
