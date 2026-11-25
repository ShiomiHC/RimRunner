namespace RimRunnerCli;

internal static class LeaseConflicts
{
    private static readonly (string Prefix, Func<IReadOnlyList<Coordinator.Row>, Coordinator.Row, bool> Rule)[] Rules =
    [
        (MachineLock.TablePrefix, MachineLock.LateConflicts),
    ];

    internal static bool Conflicts(string table, IReadOnlyList<Coordinator.Row> rows, Coordinator.Row row)
    {
        foreach ((string prefix, Func<IReadOnlyList<Coordinator.Row>, Coordinator.Row, bool> rule) in Rules)
        {
            if (table.StartsWith(prefix, StringComparison.Ordinal)) return rule(rows, row);
        }

        return false;
    }
}
