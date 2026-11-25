namespace RimRunnerCli;

internal sealed class TreeView
{
    private TreeView(List<ClaimRecord> slots)
    {
        Slots = slots;
        ClaimRecord first = slots[0];
        ProductRoot = first.ProductRoot;
        TreeName = first.TreeName;
        Product = first.Product;
        MainProductRoot = first.MainProductRoot;
        ModsFolder = first.ModsFolder;
    }

    internal string ProductRoot { get; }
    internal string TreeName { get; }
    internal string Product { get; }
    internal string? MainProductRoot { get; }
    internal string ModsFolder { get; }

    internal IReadOnlyList<ClaimRecord> Slots { get; }

    internal bool OnWorktree => MainProductRoot != null;

    internal static List<TreeView> Of(IEnumerable<ClaimRecord> records) =>
    [
        .. records
            .GroupBy(record => Key(record.ProductRoot), StringComparer.Ordinal)
            .Select(group => new TreeView([.. group.OrderBy(record => record, SlotOrder.Instance)]))
            .OrderBy(tree => tree.TreeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tree => Key(tree.ProductRoot), StringComparer.Ordinal),
    ];

    internal static TreeView? For(string productRoot, IEnumerable<ClaimRecord> records)
    {
        List<ClaimRecord> slots = [.. records.Where(record => PathIdentity.Same(record.ProductRoot, productRoot))];
        return slots.Count == 0 ? null : new TreeView([.. slots.OrderBy(record => record, SlotOrder.Instance)]);
    }

    private static string Key(string productRoot) => PathIdentity.Normalize(productRoot).ToUpperInvariant();

    internal static int PurposeRank(string purpose) =>
        PurposePolicies.ForEffective(purpose).SortRank;

    internal sealed class SlotOrder : IComparer<ClaimRecord>
    {
        public static readonly SlotOrder Instance = new();

        public int Compare(ClaimRecord? x, ClaimRecord? y)
        {
            if (x == null || y == null) return x == null ? (y == null ? 0 : -1) : 1;
            int byPurpose = PurposeRank(x.EffectivePurpose).CompareTo(PurposeRank(y.EffectivePurpose));
            if (byPurpose != 0) return byPurpose;
            int byOrdinal = (x.ShardOrdinal ?? 0).CompareTo(y.ShardOrdinal ?? 0);
            return byOrdinal != 0 ? byOrdinal : x.Number.CompareTo(y.Number);
        }
    }
}
