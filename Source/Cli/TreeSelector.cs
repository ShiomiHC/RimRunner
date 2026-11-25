namespace RimRunnerCli;

internal static class TreeSelector
{
    internal sealed record Selection(string ProductRoot, string Label, string? Purpose, ClaimRecord? Claim,
        IReadOnlyList<ClaimRecord>? Group = null);

    internal static Selection Resolve(string spec, ClaimStore store, string? product = null)
    {
        if (string.IsNullOrWhiteSpace(spec)) throw new StoreException("--tree 取值为空");
        (string tree, string? purpose, int? ordinal) = SplitPurpose(spec.Trim());
        List<ClaimRecord> everything = store.All();
        List<ClaimRecord> all = product == null
            ? everything
            : [.. everything.Where(record => string.Equals(record.Product, product, StringComparison.OrdinalIgnoreCase))];
        string productRoot = LooksLikePath(tree) ? RootOfPath(tree, everything, product) : RootOfName(tree, all, product);
        if (purpose == null)
            return new Selection(productRoot, tree, null, null);

        PurposePolicy policy = PurposePolicies.ForWire(purpose);
        if (policy.AllowsMultiplePerTree && ordinal == null)
        {
            List<ClaimRecord> shards = [.. all
                .Where(record => PathIdentity.Same(record.ProductRoot, productRoot) && record.PurposeKind == policy.Purpose)
                .OrderBy(record => record.ShardOrdinal)];
            if (shards.Count == 0)
                throw new StoreException($"树 {tree} 没有{policy.ReapRecordNoun ?? policy.Noun + "的登记"}（产品根 {productRoot}）");
            return new Selection(productRoot, spec.Trim(), purpose, null, shards);
        }

        ClaimRecord? claim = all.FirstOrDefault(record =>
            PathIdentity.Same(record.ProductRoot, productRoot)
            && record.PurposeKind == policy.Purpose
            && (!policy.AllowsMultiplePerTree || record.ShardOrdinal == ordinal));
        if (claim == null)
            throw new StoreException($"实例 {spec} 没有登记（产品根 {productRoot}）");
        return new Selection(productRoot, claim.DisplayName, purpose, claim);
    }

    internal static (string Tree, string? Purpose, int? Ordinal) SplitPurpose(string spec)
    {
        int slash = spec.LastIndexOfAny(['/', '\\']);
        if (slash <= 0) return (spec, null, null);
        string head = spec[..slash];
        string last = spec[(slash + 1)..];
        if (PurposePolicies.TryParse(last, out _))
            return (head, last, null);
        foreach (PurposePolicy multi in PurposePolicies.All)
        {
            if (multi.AllowsMultiplePerTree
                && last.StartsWith(multi.WireName, StringComparison.Ordinal)
                && int.TryParse(last[multi.WireName.Length..], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int ordinal)
                && ordinal >= 1)
                return (head, multi.WireName, ordinal);
        }

        return (spec, null, null);
    }

    private static bool LooksLikePath(string tree) =>
        Path.IsPathRooted(tree) || tree.StartsWith('.') || tree.Contains('\\');

    private static string RootOfPath(string tree, List<ClaimRecord> all, string? product)
    {
        string full = CommandContext.FullPath(tree);
        string root = OrchestratorDescriptor.FindProductRoot(full)
                      ?? all.FirstOrDefault(record => PathIdentity.Same(record.ProductRoot, full))?.ProductRoot
                      ?? throw new StoreException($"--tree {tree}：这个路径向上没有编排描述符，清单里也没有这个产品根");
        List<ClaimRecord> claims = [.. all.Where(record => PathIdentity.Same(record.ProductRoot, root))];
        if (product != null && claims.Count > 0
            && !claims.Any(record => string.Equals(record.Product, product, StringComparison.OrdinalIgnoreCase)))
            throw new StoreException($"--tree {tree} 登记的产品是 {claims[0].Product}，不是 {product}");
        return root;
    }

    private static string RootOfName(string tree, List<ClaimRecord> all, string? product)
    {
        List<TreeView> trees = TreeView.Of(all);
        List<TreeView> roots = [.. trees.Where(view => string.Equals(view.TreeName, tree, StringComparison.OrdinalIgnoreCase)
                                                       || (product != null && string.Equals(view.TreeName, product + "/" + tree,
                                                           StringComparison.OrdinalIgnoreCase)))];
        if (roots.Count == 0)
            roots = [.. trees.Where(view => string.Equals(FolderName(view.ProductRoot), tree, StringComparison.OrdinalIgnoreCase))];
        if (roots.Count == 1) return roots[0].ProductRoot;
        if (roots.Count == 0)
            throw new StoreException(product == null
                ? $"清单里没有名为 {tree} 的树；rimrunner status --all 列出全部"
                : $"产品 {product} 的清单里没有名为 {tree} 的树；rimrunner status --product {product} 列出全部");
        throw new StoreException($"--tree {tree} 匹配到 {roots.Count} 棵树，用清单里的树名或路径指定其中一棵："
                                 + string.Concat(roots.Select(view => $"{Environment.NewLine}  {view.TreeName}  {view.ProductRoot}")));
    }

    private static string FolderName(string productRoot) =>
        Path.GetFileName(CommandContext.FullPath(productRoot).TrimEnd('\\', '/'));
}
