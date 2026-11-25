namespace RimRunnerCli;

internal static class ShardGroupsLive
{
    internal sealed record Entry(string ProductRoot, ShardGroupManifest Manifest, ShardGroupQueue Queue);

    private static readonly object Gate = new();
    private static readonly List<Entry> Groups = [];

    internal static Entry Add(string productRoot, ShardGroupManifest manifest, ShardGroupQueue queue)
    {
        var entry = new Entry(productRoot, manifest, queue);
        lock (Gate) Groups.Add(entry);
        return entry;
    }

    internal static void Remove(Entry entry)
    {
        lock (Gate) Groups.Remove(entry);
    }

    internal static ShardStatusView.Group? Of(string productRoot)
    {
        Entry? entry;
        lock (Gate) entry = Groups.LastOrDefault(group => PathIdentity.Same(group.ProductRoot, productRoot));
        if (entry == null) return null;
        lock (entry.Manifest)
        {
            ShardGroupManifest manifest = entry.Manifest;
            return new ShardStatusView.Group(manifest.Group, manifest.CaseId, manifest.CreatedAt, manifest.QueueCells,
                [.. manifest.Shards.Select(shard => new ShardStatusView.Dispatched(shard.Index, shard.Number, shard.ExitCode))],
                [.. manifest.Undispatched.Select(entry => (entry.Number, entry.Reason))],
                entry.Queue.Counts());
        }
    }
}

internal static class ShardStatusView
{
    internal sealed record Slot(ClaimRecord Claim, string Liveness, string? Note);

    internal sealed record Dispatched(int Index, int Number, int? ExitCode);

    internal sealed record Group(string Id, string CaseId, DateTimeOffset CreatedAt, int Cells,
        IReadOnlyList<Dispatched> Shards, IReadOnlyList<(int Number, string Reason)> Undispatched,
        IReadOnlyDictionary<int, (int Claimed, int Done)> Counts);

    internal static List<string> Format(string treeName, IReadOnlyList<Slot> slots, Group? group, Func<int, bool> held,
        DateTimeOffset now)
    {
        var lines = new List<string>();
        if (group == null)
            lines.Add(slots.Any(slot => held(slot.Claim.Number))
                ? "分片组在别的编排进程里执行，本进程无法取得组队列的格数"
                : "分片实例：无正在执行的分片组");
        else
        {
            int claimed = group.Counts.Values.Sum(count => count.Claimed);
            int done = group.Counts.Values.Sum(count => count.Done);
            lines.Add($"分片组 {group.Id}（{group.CaseId}，{group.CreatedAt.ToLocalTime():HH:mm:ss} 起，"
                      + $"{ReportPresentation.Elapsed((long)Math.Max(0, (now - group.CreatedAt).TotalMilliseconds))}）："
                      + $"组队列 {group.Cells} 格 · 已认领 {claimed} · 已完成 {done} · 未认领 {Math.Max(0, group.Cells - claimed)}");
        }

        int width = slots.Max(slot => slot.Claim.PurposeSegment.Length);
        foreach (Slot slot in slots)
        {
            string text = $"  {slot.Claim.PurposeSegment.PadRight(width)}  {slot.Liveness}";
            if (group != null && Role(slot.Claim.Number, group, held) is { } role)
                text += " · " + role;
            if (slot.Note != null) text += " · " + slot.Note;
            lines.Add(text);
        }

        lines.Add($"  单个分片实例的明细见 rimrunner status --tree {treeName}/shardN");
        return lines;
    }

    private static string? Role(int number, Group group, Func<int, bool> held)
    {
        if (group.Shards.LastOrDefault(shard => shard.Number == number) is { } shard)
        {
            (int claimed, int done) = group.Counts.TryGetValue(shard.Index, out (int, int) count) ? count : (0, 0);
            string text = $"第 {shard.Index} 片 · 认领 {claimed} 格 · 完成 {done}";
            return shard.ExitCode is int exit ? text + $" · 已返回，退出码 {exit}" : text;
        }

        if (group.Undispatched.LastOrDefault(entry => entry.Number == number) is { Reason: { } reason })
            return "未发片：" + reason;
        return held(number) ? "组持有，未发片" : null;
    }
}
