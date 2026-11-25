namespace RimRunnerCli;

internal static class WorktreeOutputLink
{
    internal const string TreesDirName = "worktrees";
    internal const string ProductRootMarkerFileName = "product-root.txt";

    public static string CellOf(string mainOutput, string productRoot)
    {
        string trees = Path.Combine(mainOutput, TreesDirName);
        string suffix = "-" + MachineLock.ProductRootKey(productRoot);
        string? existing = Directory.Exists(trees)
            ? Directory.GetDirectories(trees)
                .FirstOrDefault(dir => Path.GetFileName(dir).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            : null;
        return existing ?? Path.Combine(trees, TreeScope.TreeName(productRoot) + suffix);
    }

    public static string? MarkedProductRoot(string cell)
    {
        string marker = Path.Combine(cell, ProductRootMarkerFileName);
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
    }

    public static bool TreeGone(string productRoot) =>
        !File.Exists(Path.Combine(productRoot, ".git"))
        && !Directory.Exists(Path.Combine(productRoot, ".git"))
        && !File.Exists(Path.Combine(productRoot, OrchestratorDescriptor.FileName));

    public static void Ensure(OrchestratorDescriptor own, string productRoot, Action<string>? log)
    {
        string? mainProductRoot = TreeScope.Of(productRoot).MainProductRoot;
        if (mainProductRoot == null)
            return;

        string mainOutput = OrchestratorDescriptor.Load(mainProductRoot).Descriptor.ResolveOutputDir(mainProductRoot);
        string link = own.ResolveOutputDir(productRoot);
        string cell = CellOf(mainOutput, productRoot);
        if (Directory.Exists(link) && Junction.GetLinkInfo(link).Kind != RimTooling.Windows.ReparseLinkKind.None)
            return;

        if (Directory.Exists(link))
        {
            if (Directory.EnumerateFileSystemEntries(link).Any())
            {
                log?.Invoke($"[产出根] 本树产出根是含内容的普通目录，产出留在本树：{link}");
                return;
            }

            Directory.Delete(link);
        }

        Directory.CreateDirectory(cell);
        File.WriteAllText(Path.Combine(cell, ProductRootMarkerFileName), CommandContext.FullPath(productRoot));
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Junction.Create(link, cell);
        CliLog.Info($"[产出根] {link} → {cell}");
    }

    public static bool RemoveLink(OrchestratorDescriptor own, string productRoot)
    {
        string link = own.ResolveOutputDir(productRoot);
        return Directory.Exists(link)
            && Junction.GetLinkInfo(link).Kind != RimTooling.Windows.ReparseLinkKind.None
            && Junction.Remove(link);
    }

    public static void RemoveShell(OrchestratorDescriptor descriptor, string productRoot)
    {
        if (!Directory.Exists(productRoot))
            return;
        RemoveLink(descriptor, productRoot);
        string? dir = Path.GetDirectoryName(descriptor.ResolveOutputDir(productRoot));
        string root = PathIdentity.Normalize(productRoot);
        while (dir != null && dir.Length >= root.Length && Directory.Exists(dir)
               && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            if (PathIdentity.Same(dir, root))
            {
                CliLog.Info($"[产出根] 空壳目录已删除：{productRoot}");
                break;
            }

            dir = Path.GetDirectoryName(dir);
        }
    }
}
