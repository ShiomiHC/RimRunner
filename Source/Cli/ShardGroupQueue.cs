namespace RimRunnerCli;

internal sealed class ShardGroupQueue(IReadOnlyList<double> weights)
{
    internal sealed record Claim(int Shard, DateTime At, double Weight, DateTime? DoneAt);

    private readonly object gate = new();
    private readonly List<(int Shard, DateTime At, DateTime? DoneAt)> claims = [];
    private readonly CancellationTokenSource drained = Drainable(weights.Count);

    internal int Count => weights.Count;

    internal IReadOnlyList<double> Weights => weights;

    internal int Claimed
    {
        get
        {
            lock (gate) return claims.Count;
        }
    }

    internal bool Drained => Claimed >= Count;

    internal CancellationToken DrainedToken => drained.Token;

    internal int Take(int shard)
    {
        int ordinal;
        lock (gate)
        {
            if (claims.Count >= weights.Count) return -1;
            claims.Add((shard, DateTime.UtcNow, null));
            ordinal = claims.Count - 1;
        }

        if (ordinal == weights.Count - 1) drained.Cancel();
        return ordinal;
    }

    private static CancellationTokenSource Drainable(int count)
    {
        var source = new CancellationTokenSource();
        if (count == 0) source.Cancel();
        return source;
    }

    internal bool Complete(int shard, int ordinal, DateTime at)
    {
        lock (gate)
        {
            if (ordinal < 0 || ordinal >= claims.Count) return false;
            (int owner, DateTime claimedAt, DateTime? doneAt) = claims[ordinal];
            if (owner != shard || doneAt != null) return false;
            claims[ordinal] = (owner, claimedAt, at);
            return true;
        }
    }

    internal List<int> OwnedBy(int shard)
    {
        lock (gate)
            return [.. Enumerable.Range(0, claims.Count).Where(ordinal => claims[ordinal].Shard == shard)];
    }

    internal Dictionary<int, (int Claimed, int Done)> Counts()
    {
        lock (gate)
            return claims.GroupBy(claim => claim.Shard)
                .ToDictionary(group => group.Key, group => (group.Count(), group.Count(claim => claim.DoneAt != null)));
    }

    internal List<Claim> Claims()
    {
        lock (gate)
            return [.. claims.Select((claim, ordinal) => new Claim(claim.Shard, claim.At, weights[ordinal], claim.DoneAt))];
    }
}
