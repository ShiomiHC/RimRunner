namespace RimRunnerCli;

internal static class AutoInstances
{
    internal static HashSet<int> AliveNumbers()
    {
        HashSet<int> alive = [.. InstanceLinks.All.Select(link => link.Number)];
        foreach (MachineSnapshot.Instance instance in MachineSnapshot.Read()?.Instances ?? [])
            alive.Add(instance.Number);
        return alive;
    }

    internal static List<ClaimRecord> Live(ClaimStore store, string productRoot) =>
        Live(store.FindByProductRoot(productRoot), AliveNumbers());

    internal static List<ClaimRecord> Live(IEnumerable<ClaimRecord> claims, ISet<int> alive) =>
    [
        .. claims.Where(claim => PurposePolicies.IsAuto(claim.PurposeKind) && alive.Contains(claim.Number))
            .OrderBy(claim => claim.Policy.SortRank)
            .ThenBy(claim => claim.ShardOrdinal ?? 0),
    ];

    internal sealed record Pick(ClaimRecord? Claim, IReadOnlyList<string> Refusal);

    internal static Pick Resolve(IReadOnlyList<ClaimRecord> claims, IReadOnlyList<ClaimRecord> live, string? runId,
        Func<int, string?> currentRunId)
    {
        if (runId != null)
        {
            ClaimRecord? match = live.FirstOrDefault(claim => currentRunId(claim.Number) == runId);
            if (match != null) return new Pick(match, []);
            return new Pick(null,
            [
                $"[FAIL] 本树没有当前 runId 为 {runId} 的自动实例",
                .. live.Count == 0 ? ["  本树此刻无运行中的自动实例"] : Candidates(live, currentRunId),
            ]);
        }

        if (live.Count > 1)
            return new Pick(null,
                [$"[FAIL] 本树有 {live.Count} 个自动实例，用 --run-id 指定其中一个：", .. Candidates(live, currentRunId)]);
        return new Pick(claims.FirstOrDefault(claim => claim.PurposeKind == InstancePurpose.Batch), []);
    }

    internal static IEnumerable<string> Candidates(IEnumerable<ClaimRecord> live, Func<int, string?> currentRunId) =>
        live.Select(claim => currentRunId(claim.Number) is { } id
            ? $"  --run-id {id}  {claim.DisplayName}"
            : $"  {claim.DisplayName}（当前 runId 未知）");
}
