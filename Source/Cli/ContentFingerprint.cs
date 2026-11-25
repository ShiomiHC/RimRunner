using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimRunnerCli;

public static class ContentFingerprint
{
    private static readonly string[] OutsideFingerprint =
    [
        nameof(OrchestratorDescriptor.DefaultExcluded), nameof(OrchestratorDescriptor.ColdPreflightCommands),
        nameof(OrchestratorDescriptor.MountPreflightCommands), nameof(OrchestratorDescriptor.Trace),
        nameof(OrchestratorDescriptor.ForceMainMap),
    ];

    internal static byte[] DescriptorBytes(OrchestratorDescriptor descriptor)
    {
        var node = (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(descriptor)!;
        foreach (string name in OutsideFingerprint) node.Remove(name);
        return System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    public const string MismatchNote =
        "内容指纹不匹配";

    private static readonly string[] SkippedDirNames = ["Assemblies", "bin", "obj", ".git"];

    public static string Compute(OrchestratorDescriptor descriptor, string productRoot, string? modList,
        Func<string, string?> getEnvironmentVariable, string? baselineSavePath = null)
    {
        var roots = new List<string>();
        roots.AddRange(descriptor.Units.Select(unit => CommandContext.FullPath(Path.Combine(
            productRoot, unit.SourceRel.Replace('/', Path.DirectorySeparatorChar)))));
        string? content = descriptor.ResolveTestModContentDir(productRoot);
        if (content != null) roots.Add(content);
        EnginePackage package = EnginePackage.Locate(getEnvironmentVariable);
        roots.Add(package.ContentDir);
        if (descriptor.EngineSelfTests) roots.Add(package.SelfTestContentDir);

        var builder = new RimTooling.RimWorld.ContentFingerprintBuilder();
        var unitOptions = new RimTooling.RimWorld.FingerprintWalkOptions
        {
            SkipDirectoryNames = SkippedDirNames,
        };
        var foreignOptions = new RimTooling.RimWorld.FingerprintWalkOptions
        {
            SkipDirectoryNames = [".git"],
        };

        string normalizedProductRoot = PathIdentity.Normalize(productRoot);
        string KeyFor(string path)
        {
            string normalizedFull = PathIdentity.Normalize(path);
            if (PathIdentity.Same(normalizedFull, normalizedProductRoot))
            {
                return "product-root";
            }

            if (PathIdentity.IsUnder(normalizedFull, normalizedProductRoot))
            {
                return "product/" + Path.GetRelativePath(normalizedProductRoot, normalizedFull).Replace('\\', '/');
            }

            return "ext/" + normalizedFull.Replace(':', '_').Replace('\\', '/');
        }

        foreach (string root in roots)
        {
            if (Directory.Exists(root))
            {
                builder.AddDirectory(root, KeyFor(root), unitOptions);
            }
        }

        ModListDefinition? definition = LoadModList(descriptor, productRoot, modList);
        foreach (string modDir in ForeignModDirs(descriptor, productRoot, definition))
        {
            builder.AddDirectory(modDir, KeyFor(modDir), foreignOptions);
        }

        string savePath = baselineSavePath ?? Path.Combine(descriptor.ResolveSavesDir(productRoot),
            descriptor.BaselineSaveNameFor(definition?.SaveOwner ?? modList) + ".rws");
        builder.AddFile(savePath, KeyFor(savePath), trackMissing: true);

        if (!string.IsNullOrWhiteSpace(modList))
        {
            string modListPath = Path.Combine(productRoot, descriptor.ModListsDir,
                modList + RimTooling.RimWorld.RimWorldModList.Extension);
            builder.AddFile(modListPath, KeyFor(modListPath), trackMissing: true);
        }

        if (definition != null)
        {
            foreach (string sourcePath in definition.SourcePaths)
            {
                builder.AddFile(sourcePath, KeyFor(sourcePath), trackMissing: true);
            }
        }

        builder.AddExtra("descriptor", Convert.ToHexString(SHA256.HashData(DescriptorBytes(descriptor))));

        builder.AddExtra("absentable", string.Join(",", AbsentablePackages.Derive(descriptor, productRoot)));

        return builder.ComputeDigestHex();
    }

    private static ModListDefinition? LoadModList(
        OrchestratorDescriptor descriptor, string productRoot, string? modList)
    {
        if (string.IsNullOrWhiteSpace(modList)) return null;
        try
        {
            return ModListDefinition.Load(Path.Combine(productRoot, descriptor.ModListsDir), modList);
        }
        catch (StoreException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ForeignModDirs(
        OrchestratorDescriptor descriptor, string productRoot, ModListDefinition? definition)
    {
        if (definition == null || string.IsNullOrWhiteSpace(descriptor.GameExe))
            return [];
        var units = new HashSet<string>(descriptor.Units.Select(unit => unit.PackageId), StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> ids = definition.ModIds.Where(id => !units.Contains(id));

        Dictionary<string, string> byId = ModDirectories(descriptor);

        return ids
            .Select(id => byId.TryGetValue(id, out string? dir) ? dir : null)
            .OfType<string>()
            .Where(dir => !PathIdentity.IsUnder(dir, productRoot, includeSelf: true))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal static Dictionary<string, string> ModDirectories(OrchestratorDescriptor descriptor)
    {
        string gameDir = Path.GetDirectoryName(CommandContext.FullPath(descriptor.GameExe)) ?? "";
        IReadOnlyList<string> searchRoots = string.IsNullOrWhiteSpace(gameDir)
            ? [CommandContext.FullPath(descriptor.ModsFolder)]
            : RimTooling.RimWorld.SteamWorkshop.BuildModSearchRoots(
                gameDir,
                RimTooling.RimWorld.ModSearchOrder.ModsFirst,
                includeData: true,
                includeWorkshop: true,
                customModsDir: CommandContext.FullPath(descriptor.ModsFolder));

        return RimTooling.RimWorld.ModDirectoryScanner.IndexByPackageId(searchRoots)
            .ToDictionary(pair => pair.Key, pair => pair.Value.RealPath, StringComparer.OrdinalIgnoreCase);
    }
}
