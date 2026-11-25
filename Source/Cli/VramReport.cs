namespace RimRunnerCli;

public static class VramReport
{
    private const int TopMods = 15;

    public static int ModLists(string claimRoot, string productRoot, OrchestratorDescriptor descriptor, IReadOnlyList<string> names)
    {
        string? gameVersion = VramModel.GameVersion(descriptor.GameExe)
            ?? throw new StoreException($"无法读取游戏版本（{descriptor.GameExe} 同目录的 Assembly-CSharp.dll 或 Version.txt）");
        GpuMemory.Snapshot? snapshot = GpuMemory.TryRead(out string? reason);
        long headroom = AdmissionGate.Headroom();
        if (snapshot == null)
            Console.WriteLine($"[显存] 无法读取（{reason}），只输出估算");
        else
            Console.WriteLine($"{snapshot.AdapterName}：总 {AdmissionGate.Gb(snapshot.Total)}，预留 {AdmissionGate.Gb(headroom)}");

        Dictionary<string, string> modDirs = ContentFingerprint.ModDirectories(descriptor);
        AdmissionLedger.History history = AdmissionLedger.ForClaimRoot(claimRoot).ReadHistory();
        if (history.Unreadable != null)
            Console.WriteLine($"[WARN] 准入历史无法读取（{history.Unreadable}），下面按无实测估算");
        string modListsDir = Path.Combine(productRoot, descriptor.ModListsDir);
        foreach (string name in names)
        {
            ModListDefinition definition = ModListDefinition.Load(modListsDir, name);
            Console.WriteLine();
            Console.WriteLine(definition.ModsFrom == null
                ? $"=== {name}（{definition.ModIds.Count} 项）==="
                : $"=== {name}（{definition.ModIds.Count} 项，清单取自 {definition.ModsFrom}）===");
            List<VramModel.ModTextures> mods = VramModel.ScanByMod(definition.ModIds, modDirs, gameVersion,
                out List<string> missing);
            if (missing.Count > 0)
                Console.WriteLine($"[WARN] {missing.Count} 项未找到 mod 目录，贴图未计入：{string.Join("、", missing)}");

            VramModel.TextureLoad total = mods.Aggregate(VramModel.TextureLoad.Empty, (sum, m) => sum.Add(m.Load));
            string tiers = string.Join(" · ", TextureTierPolicy.Ladder.Reverse().Select(tier =>
                $"{tier} {AdmissionGate.Gb(VramModel.Estimate(total, tier))}"));
            Console.WriteLine($"模型：{tiers}");
            Console.WriteLine($"贴图全部常驻：{AdmissionGate.Gb(VramModel.ResidentBytes(total))}（贴图 {mods.Sum(m => m.Files)} 个：{Composition(total)}）");
            string? declared = definition.DirectiveOrNull(TextureTierPolicy.DirectiveKey);
            try
            {
                string tier = TextureTierPolicy.Resolve(definition.DirectiveSource(TextureTierPolicy.DirectiveKey), declared, showcase: false, TextureTierPolicy.CaseDemand.None).Tier;
                string source = definition.DirectiveSource(TextureTierPolicy.DirectiveKey);
                Console.WriteLine($"名单档位：{tier}{(string.IsNullOrWhiteSpace(declared) ? "（缺省）" : source == name ? "（名单声明）" : $"（{source} 声明）")}");
            }
            catch (StoreException exception)
            {
                Console.WriteLine($"[WARN] {exception.Message}；run 会拒绝执行这份名单");
            }

            string prefix = $"{descriptor.Product}|{name}|";
            List<string> measured =
            [
                .. history.Peaks
                    .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Value.Count > 0)
                    .Select(pair =>
                        $"{pair.Key[prefix.Length..].Replace('|', ' ')} {AdmissionGate.Gb(pair.Value.Max(p => p.Bytes))}（{pair.Value.Count} 次实测）")
            ];
            Console.WriteLine(measured.Count == 0 ? "实测：无" : $"实测：{string.Join(" · ", measured)}");

            Console.WriteLine($"--- 贴图最重的 {Math.Min(TopMods, mods.Count)} 项 ---");
            foreach (VramModel.ModTextures mod in mods.OrderByDescending(m => NativeBytes(m.Load)).Take(TopMods))
            {
                if (mod.Files == 0) break;
                Console.WriteLine($"  {mod.PackageId,-44} {mod.Files,6} 个  {AdmissionGate.Gb(NativeBytes(mod.Load)),8}"
                                  + (mod.Load.NonMultipleOf4 >= 500_000 ? $"  非 4 倍数边长 {mod.Load.NonMultipleOf4 / 1e6:0} M px" : "")
                                  + (mod.Load.PackedBytes > 0 ? $"  内容包 {AdmissionGate.Gb(mod.Load.PackedBytes)}" : "")
                                  + (mod.Load.LooseDdsBytes > 0 ? $"  .dds {AdmissionGate.Gb(mod.Load.LooseDdsBytes)}" : ""));
            }
        }

        return 0;
    }

    private static long NativeBytes(VramModel.TextureLoad load) =>
        VramModel.ResidentBytes(load);

    private static string Composition(VramModel.TextureLoad load)
    {
        var parts = new List<string> { $"{load.Compressible / 1e9:0.00} G px" };
        if (load.NonMultipleOf4 > 0) parts.Add($"非 4 倍数边长 {load.NonMultipleOf4 / 1e9:0.00} G px");
        if (load.PackedBytes > 0) parts.Add($"内容包 {AdmissionGate.Gb(load.PackedBytes)}");
        if (load.LooseDdsBytes > 0) parts.Add($".dds {AdmissionGate.Gb(load.LooseDdsBytes)}");
        return string.Join(" + ", parts);
    }
}
