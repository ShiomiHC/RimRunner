namespace RimRunnerCli;

internal sealed class ShardGroupSink(string groupDir, TimeSpan? ledgerLockTimeout = null)
{
    private readonly Lock gate = new();
    private readonly TimeSpan lockTimeout = ledgerLockTimeout ?? LedgerBook.LockTimeout;
    private readonly Dictionary<string, Book> books = new(StringComparer.OrdinalIgnoreCase);

    internal string GroupDir { get; } = CommandContext.FullPath(groupDir);

    internal void Add(int index, string fullPath, string label,
        IReadOnlyList<KeyValuePair<string, List<string>>> cells, IReadOnlyList<string> drop)
    {
        lock (gate)
        {
            if (!books.TryGetValue(fullPath, out Book? book))
            {
                book = new Book(label);
                books[fullPath] = book;
            }

            book.Pushes.Add(new Push(index, [.. cells], [.. drop]));
        }
    }

    internal string ReportTarget(string relativeArchive)
    {
        string prefix = RimRunner.RimRunnerDataLayout.ReportsDirName + RimRunner.RimRunnerOutputPath.Separator;
        if (!relativeArchive.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("分片的报告归档不在 reports/ 下：" + relativeArchive);
        string full = CommandContext.FullPath(Path.Combine(GroupDir,
            relativeArchive[prefix.Length..].Replace(RimRunner.RimRunnerOutputPath.Separator, Path.DirectorySeparatorChar)));
        if (!full.StartsWith(GroupDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("分片的报告归档落在组目录之外：" + relativeArchive);
        return full;
    }

    internal static string? StemOf(string fileName)
    {
        if (!fileName.EndsWith(".json", StringComparison.Ordinal)) return null;
        string name = fileName[..^".json".Length];
        return RimRunner.RimRunnerLedgerFold.KnownStems
            .Where(stem => name == stem || name.StartsWith(stem + "-", StringComparison.Ordinal))
            .OrderByDescending(stem => stem.Length)
            .FirstOrDefault();
    }

    internal List<string> Fold()
    {
        var faults = new List<string>();
        List<KeyValuePair<string, Book>> snapshot;
        lock (gate) snapshot = [.. books];
        foreach ((string path, Book book) in snapshot.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (StemOf(Path.GetFileName(path)) is not string stem
                || !RimRunner.RimRunnerLedgerFold.TryRuleOf(stem, out RimRunner.RimRunnerLedgerFold.Rule rule, out int cap))
            {
                faults.Add(book.Label + "：无分片折法，本组这本台账的格未并进本树");
                continue;
            }

            if (rule == RimRunner.RimRunnerLedgerFold.Rule.Drop) continue;

            var cells = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var order = new List<string>();
            var drop = new HashSet<string>(StringComparer.Ordinal);
            foreach (Push push in book.Pushes.OrderBy(push => push.Index))
            {
                drop.UnionWith(push.Drop);
                foreach ((string key, List<string> ring) in push.Cells)
                {
                    if (ring.Count == 0) continue;
                    if (!cells.TryGetValue(key, out List<string>? list))
                    {
                        list = [];
                        cells[key] = list;
                        order.Add(key);
                    }

                    list.AddRange(ring);
                }
            }

            if (order.Count == 0 && drop.Count == 0) continue;
            try
            {
                LedgerBook.Exclusive(path, () =>
                {
                    Dictionary<string, List<string>> tree;
                    try
                    {
                        tree = LedgerBook.Read(path);
                    }
                    catch (InvalidDataException exception)
                    {
                        faults.Add(book.Label + "读取失败，已按空台账重建：" + exception.Message);
                        tree = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    }

                    foreach (string key in drop) tree.Remove(key);
                    RimRunner.RimRunnerLedgerFold.Merge(tree,
                        order.Select(key => new KeyValuePair<string, List<string>>(key,
                            RimRunner.RimRunnerLedgerFold.Fold(rule, cells[key]))),
                        rule, cap);
                    LedgerBook.Write(path, tree);
                }, lockTimeout);
            }
            catch (Exception exception) when (InstanceOutput.IsWriteFault(exception) || exception is StoreException)
            {
                faults.Add(book.Label + "：本组的 " + order.Count + " 个键未并进本树：" + exception.GetType().Name + "："
                           + exception.Message);
            }
        }

        return faults;
    }

    private sealed record Book(string Label)
    {
        internal List<Push> Pushes { get; } = [];
    }

    private sealed record Push(int Index, List<KeyValuePair<string, List<string>>> Cells, List<string> Drop);
}
