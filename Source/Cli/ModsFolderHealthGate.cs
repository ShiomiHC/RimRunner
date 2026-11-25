namespace RimRunnerCli;

public static class ModsFolderHealthGate
{
    public static string? Verdict(IReadOnlyList<ModsFolderScan.ModsEntry> entries,
        Func<string, string>? ownershipOf = null)
    {
        string[] broken =
        [
            .. entries
                .Where(entry => entry.PackageId == null)
                .Select(entry => entry.Path)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        ];
        if (broken.Length == 0)
        {
            return null;
        }

        return $"Mods 目录里有 {broken.Length} 项无法读取 packageId：" + Environment.NewLine
            + string.Join(Environment.NewLine, broken.Select(path =>
                "  " + path + (ownershipOf == null ? "" : $"（{ownershipOf(path)}）")));
    }

    internal static string Ownership(string path, IReadOnlyList<ClaimRecord> claims)
    {
        foreach (ClaimRecord claim in claims)
        {
            bool mine = (claim.TestMod is { } testMod && PathIdentity.Same(testMod, path))
                || (claim.LoadDirs?.Any(dir => PathIdentity.Same(dir, path)) ?? false)
                || (claim.Wrappers?.Any(wrapper => PathIdentity.Same(wrapper, path)) ?? false);
            if (mine) return $"被实例 {claim.DisplayName} 的登记认领";
        }

        return "无登记认领";
    }

    public static void Warn(IReadOnlyList<ModsFolderScan.ModsEntry> entries, string modsFolder,
        ClaimStore? store, Action<string>? log)
    {
        CliBatchWarnings.Checked(RimRunner.RimRunnerWarningKey.ModsUnreadable);
        if (entries.Count == 0 && !Directory.Exists(modsFolder))
        {
            string missing = "[WARN] Mods 目录不存在或无法访问：" + modsFolder;
            log?.Invoke(missing);
            CliLog.Info(missing);
            return;
        }

        string? complaint = Verdict(entries, OwnershipResolver(store));
        if (complaint != null)
        {
            log?.Invoke("[WARN] " + complaint);
            CliBatchWarnings.Add(RimRunner.RimRunnerWarningKey.ModsUnreadable, complaint);
        }
        else
        {
            CliLog.Info($"[Mods 目录] 体检通过：{entries.Count} 项均读出 packageId");
        }
    }

    internal static string Facts(string path, IReadOnlyList<ClaimRecord>? claims) =>
        SynthMarker.Describe(path, claims) + (claims == null ? "" : "，" + Ownership(path, claims));

    private static Func<string, string> OwnershipResolver(ClaimStore? store)
    {
        List<ClaimRecord>? claims = null;
        try
        {
            claims = store?.All();
        }
        catch (StoreException)
        {
        }

        return path => Facts(path, claims);
    }
}
