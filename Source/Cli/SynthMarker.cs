using System.Text.Json;

namespace RimRunnerCli;

internal static class SynthMarker
{
    public const string FileName = ".rimrunner-synth.json";
    public const string KindWrapper = "wrapper";
    public const string KindLoadDir = "loadDir";

    private sealed record Marker(string Kind, int Number, string? ProductRoot = null, string? Unit = null,
        int Ordinal = 0, string? Build = null);

    public sealed record LoadDirMarker(string ProductRoot, string Unit, int Ordinal, string? Build);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Write(string dir, string kind, int number) =>
        File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(new Marker(kind, number), Options));

    public static void WriteLoadDir(string dir, string productRoot, string unit, int ordinal, string? build) =>
        File.WriteAllText(Path.Combine(dir, FileName),
            JsonSerializer.Serialize(new Marker(KindLoadDir, 0, productRoot, unit, ordinal, build), Options));

    public static LoadDirMarker? ReadLoadDir(string dir) =>
        Read(dir) is { Kind: KindLoadDir, ProductRoot: { } root, Unit: { } unit } marker
            ? new LoadDirMarker(root, unit, marker.Ordinal, marker.Build)
            : null;

    public static void RetireLoadDir(string dir)
    {
        if (ReadLoadDir(dir) is { } marker)
            WriteLoadDir(dir, marker.ProductRoot, marker.Unit, marker.Ordinal, null);
    }

    public static void DeleteWithMarkerLast(string dir, Action<string> removeContents)
    {
        removeContents(dir);
        File.Delete(Path.Combine(dir, FileName));
        Directory.Delete(dir);
    }

    public static void SweepUnownedIfFree(string modsFolder, ClaimStore store)
    {
        using IDisposable? held = store.TrySweepLock();
        if (held == null)
            return;

        try
        {
            SweepUnowned(modsFolder, store);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or StoreException)
        {
            CliLog.Warn($"无主合成目录未清扫完成：{exception.Message}");
        }
    }

    public static void SweepUnowned(string modsFolder, ClaimStore store)
    {
        if (!Directory.Exists(modsFolder))
            return;
        TestModSynthesizer.SweepUnowned(modsFolder, store);
        foreach (string dir in Directory.EnumerateDirectories(modsFolder))
        {
            if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                continue;

            if (Read(dir) is not { } marker || Owned(dir, marker, store))
                continue;

            string name = Path.GetFileName(dir);
            if (marker.Kind == KindLoadDir)
            {
                LoadDirPool.Remove(dir);
                CliLog.Info($"[冷启动] 已清扫无主载入目录：{name}");
            }
            else
            {
                LegacyWrappers.RemoveWrapper(dir);
                CliLog.Info($"[冷启动] 已清扫无主旧形态载入目录：{name}");
            }
        }
    }

    public static string Describe(string dir, IReadOnlyList<ClaimRecord>? claims = null)
    {
        if (RimTooling.IO.PathPresence.Of(dir) != RimTooling.IO.PathPresenceKind.Absent
            && File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
        {
            string? target = new DirectoryInfo(dir).LinkTarget;
            if (target != null)
                return Directory.Exists(target) ? $"链接，指向 {target}" : $"链接，目标 {target} 不存在";
        }

        if (Read(dir) is { Kind: KindLoadDir } loadDir)
            return $"带 RimRunner 合成标记（{loadDir.ProductRoot} 的载入目录）";
        if (Read(dir) is { } marker)
            return $"带 RimRunner 合成标记（实例 {NameOf(marker.Number, claims)}，旧形态载入目录）";
        if (RimTooling.RimWorld.SynthMod.ReadMarker(dir) is { Tool: TestModSynthesizer.SynthTool } synth)
            return int.TryParse(synth.Owner, out int owner)
                ? $"带 RimRunner 合成标记（实例 {NameOf(owner, claims)}，测试 mod）"
                : "带 RimRunner 合成标记（测试 mod）";
        return RimTooling.IO.PathPresence.Of(Path.Combine(dir, FileName)) == RimTooling.IO.PathPresenceKind.Absent
            ? "无合成标记"
            : "无法读取合成标记";
    }

    private static string NameOf(int number, IReadOnlyList<ClaimRecord>? claims) =>
        claims == null
            ? ClaimStore.Named(number)
            : claims.FirstOrDefault(claim => claim.Number == number)?.DisplayName ?? ClaimStore.GoneName;

    private static Marker? Read(string dir)
    {
        string path = Path.Combine(dir, FileName);
        if (RimTooling.IO.PathPresence.Of(path) == RimTooling.IO.PathPresenceKind.Absent)
            return null;
        try
        {
            Marker? marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), Options);
            return marker is { Kind: KindWrapper or KindLoadDir } ? marker : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Owned(string dir, Marker marker, ClaimStore store)
    {
        if (marker.Kind == KindLoadDir)
        {
            try
            {
                bool ClaimedAcrossRename()
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        if (attempt > 0) Thread.Sleep(2);
                        if (store.All().Any(claim => PathIdentity.Same(claim.ProductRoot, marker.ProductRoot!)))
                            return true;
                    }

                    return false;
                }

                return marker.ProductRoot == null || ClaimedAcrossRename();
            }
            catch (StoreException)
            {
                return true;
            }
        }

        ClaimRecord? claim;
        try
        {
            claim = store.Read(marker.Number);
        }
        catch (StoreException)
        {
            return true;
        }

        if (claim == null)
            return false;
        List<string>? recorded = claim.Wrappers;
        return recorded == null || recorded.Any(path => PathIdentity.Same(path, dir));
    }
}
