namespace RimRunnerCli;

public static class LoadedModsGate
{
    public static IReadOnlyList<string> Missing(
        IEnumerable<string> loaded, IEnumerable<string> required, IReadOnlyList<string> suffixedIds)
    {
        string[] present = [.. loaded];
        string[] suffixed = [.. suffixedIds];
        return
        [
            .. required
                .Where(id => !RimRunner.RimRunnerPackagePresence.Loaded(present, id, suffixed))
        ];
    }

    public static string? Verdict(
        InstanceProbe.LoadedPacks probe, IReadOnlyCollection<string> required, int number,
        IReadOnlyList<string> suffixedIds, IReadOnlyList<ModsFolderScan.ModsEntry>? modsEntries = null)
    {
        if (probe.Fault != null)
        {
            return $"无法读取实例 {ClaimStore.Named(number)} 的已装载 mod：{probe.Fault}";
        }

        IReadOnlyList<string> missing = Missing(Effective(probe), required, suffixedIds);
        if (missing.Count == 0)
        {
            return null;
        }

        return "必需 mod 在名单里但未装入进程：" + Annotated(missing, suffixedIds, modsEntries, probe.Shortfalls);
    }

    public static string? ModListShortfall(
        InstanceProbe.LoadedPacks probe, IReadOnlyList<string> modListIds, IReadOnlyCollection<string> required,
        IReadOnlyList<string> suffixedIds, string modListName, IReadOnlyList<ModsFolderScan.ModsEntry>? modsEntries = null)
    {
        string[] requiredIds = [.. required];
        string[] checkedIds = [.. modListIds.Where(id => !RimRunner.RimRunnerPackagePresence.Listed(requiredIds, id))];
        if (checkedIds.Length == 0)
        {
            CliLog.Info($"[装载] mod 名单 {modListName} 只列必需 mod，没有可核的项");
            return null;
        }

        IReadOnlyList<string> missing = Missing(Effective(probe), checkedIds, suffixedIds);
        if (missing.Count == 0)
        {
            CliLog.Info($"[装载] mod 名单 {modListName} 的 {checkedIds.Length} 项均已装进进程");
            return null;
        }

        return $"mod 名单 {modListName} 上的项未装入进程：" + Annotated(missing, suffixedIds, modsEntries, probe.Shortfalls);
    }

    private static IEnumerable<string> Effective(InstanceProbe.LoadedPacks probe) =>
        probe.Shortfalls is not { Count: > 0 } shortfalls
            ? probe.Ids!
            : probe.Ids!.Where(id => !shortfalls.Keys.Any(key => string.Equals(key, id, StringComparison.OrdinalIgnoreCase)));

    private static string Annotated(
        IReadOnlyList<string> missing, IReadOnlyList<string> suffixedIds, IReadOnlyList<ModsFolderScan.ModsEntry>? modsEntries,
        IReadOnlyDictionary<string, string>? shortfalls)
    {
        string[] suffixed = [.. suffixedIds];
        string[]? folderIds = modsEntries == null ? null : [.. modsEntries.Select(entry => entry.PackageId).OfType<string>()];
        return string.Join("、", missing.Select(id =>
        {
            string? reason = shortfalls?.FirstOrDefault(pair => RimRunner.RimRunnerPackagePresence.Loaded([pair.Key], id, suffixed)).Value;
            if (reason != null)
            {
                return id + "（packageId 已装载；" + reason + "）";
            }

            return folderIds == null
                ? id
                : id + (RimRunner.RimRunnerPackagePresence.Loaded(folderIds, id, suffixed)
                    ? "（Mods 目录里有该项，游戏未装入它）"
                    : "（Mods 目录里没有该项）");
        }));
    }
}
