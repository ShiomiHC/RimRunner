namespace RimRunner;

internal sealed class RimRunnerProbeHits
{
    private sealed class Hit
    {
        internal required string Key;
        internal string? CaseId;
        internal required string Text;
        internal string? Tail;
        internal int Count;
        internal string? Origin;
    }

    private readonly Dictionary<string, Hit> hits = new(StringComparer.Ordinal);
    private readonly List<Hit> order = [];

    internal void Clear()
    {
        hits.Clear();
        order.Clear();
    }

    internal bool Bump(string key)
    {
        if (!hits.TryGetValue(key, out Hit? seen))
        {
            return false;
        }

        seen.Count++;
        return true;
    }

    internal void Add(string key, string? caseId, string text, string? tail = null, string? origin = null)
    {
        Hit hit = new() { Key = key, CaseId = caseId, Text = text, Tail = tail, Count = 1, Origin = origin };
        hits[key] = hit;
        order.Add(hit);
    }

    internal List<string> Consume(string caseId, Func<string, bool> match) =>
        Take(hit => hit.CaseId == caseId && match(hit.Text)).Select(hit => hit.Text + hit.Tail).ToList();

    internal List<(string Key, string Text, string? Origin, int Count)> ConsumeHits(string caseId, Func<string, bool> match) =>
        Take(hit => hit.CaseId == caseId && match(hit.Text)).Select(hit => (hit.Key, hit.Text, hit.Origin, hit.Count)).ToList();

    internal List<(string Text, int Count)> ConsumeKey(string key) =>
        Take(hit => hit.Key == key).Select(hit => (hit.Text, hit.Count)).ToList();

    internal List<string> Drain(string caseId) =>
        Take(hit => hit.CaseId == caseId).Select(Describe).ToList();

    internal List<(string Text, string? Origin)> DrainWithOrigin(string caseId) =>
        Take(hit => hit.CaseId == caseId).Select(hit => (Describe(hit), hit.Origin)).ToList();

    internal List<string> DrainAll() =>
        Take(_ => true).Select(hit => hit.CaseId == null ? Describe(hit) : hit.CaseId + " " + Describe(hit)).ToList();

    internal List<(string? CaseId, string Text, string? Origin, int Count)> DrainAllMeta() =>
        Take(_ => true).Select(hit => (hit.CaseId, Describe(hit), hit.Origin, hit.Count)).ToList();

    private static string Describe(Hit hit) => (hit.Count > 1 ? hit.Text + " ×" + hit.Count : hit.Text) + hit.Tail;

    private List<Hit> Take(Func<Hit, bool> match)
    {
        List<Hit> taken = order.Where(match).ToList();
        foreach (Hit hit in taken)
        {
            order.Remove(hit);
        }

        foreach (string key in hits.Where(pair => taken.Contains(pair.Value)).Select(pair => pair.Key).ToList())
        {
            hits.Remove(key);
        }

        return taken;
    }
}
