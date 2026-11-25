using System.Globalization;
using System.Xml;
using RimTooling.RimWorld;

namespace RimRunnerCli;

public sealed class TestModSynthesizer(OrchestratorDescriptor descriptor, ClaimStore store)
{
    private readonly OrchestratorDescriptor descriptor = descriptor;
    private readonly ClaimStore store = store;

    public string PackageIdFor(int number) => PackageIdFor(descriptor, number);

    public static string PackageIdFor(OrchestratorDescriptor descriptor, int number) =>
        $"{descriptor.TestMod.IdPrefix}.{number}";

    public string DirFor(string modsFolder, int number) =>
        Path.Combine(modsFolder, PackageIdFor(number));

    public static string ContentDirFor(ClaimStore store, int number) =>
        Path.Combine(store.InstanceDir(number), ContentDirName);

    private const string ContentDirName = "testmod";

    private static readonly string[] ReservedTopLevelDirs = ["About", "Assemblies"];

    private const string EngineContentDefsRel = "Defs/RimRunner";

    public string Sync(
        string modsFolder, int number, string dllPath, string engineDll,
        IReadOnlyList<string> productPackageIds, string engineContentDir,
        Action<string>? log = null, string? contentDir = null, ArtifactGate.SelfTestParts? selfTests = null)
    {
        if (!File.Exists(dllPath))
        {
            throw new StoreException($"测试程序集产物不存在：{dllPath}");
        }

        if (!File.Exists(engineDll))
        {
            throw new StoreException($"引擎 DLL 不存在：{engineDll}");
        }

        if (selfTests != null && !File.Exists(selfTests.Dll))
        {
            throw new StoreException($"引擎自测 DLL 不存在：{selfTests.Dll}");
        }

        foreach (string engineFile in new[] { EngineArtifact.FileName, EngineArtifact.SelfTestFileName })
        {
            if (string.Equals(descriptor.TestMod.AssemblyName, engineFile, StringComparison.OrdinalIgnoreCase))
            {
                throw new StoreException(
                    $"testMod.assemblyName 与引擎侧 DLL 同名（{engineFile}），两份落进同一个 Assemblies/");
            }
        }

        if (!Directory.Exists(engineContentDir))
        {
            throw new StoreException($"引擎内容目录不存在：{engineContentDir}");
        }

        AssertCopyableContent(engineContentDir, "引擎内容");

        if (selfTests != null)
        {
            if (!Directory.Exists(selfTests.ContentDir))
            {
                throw new StoreException($"引擎自测内容目录不存在：{selfTests.ContentDir}");
            }

            AssertCopyableContent(selfTests.ContentDir, "引擎自测内容");
        }

        if (contentDir != null)
        {
            if (!Directory.Exists(contentDir))
            {
                throw new StoreException($"描述符声明的测试内容目录不存在：{contentDir}");
            }

            AssertCopyableContent(contentDir, "测试内容");
            if (Directory.Exists(Path.Combine(
                    contentDir, EngineContentDefsRel.Replace('/', Path.DirectorySeparatorChar))))
            {
                throw new StoreException(
                    $"测试内容目录不得含 {EngineContentDefsRel}/（由引擎内容提供）：{contentDir}");
            }
        }

        using (store.SweepLock())
        {
            SynthModLayout layout = LayoutFor(store, modsFolder);
            var ours = new HashSet<string>(
                Synth(() => SynthMod.Entries(layout)).Select(entry => entry.Path), StringComparer.OrdinalIgnoreCase);
            string prefix = descriptor.TestMod.IdPrefix + ".";
            foreach (string dirPath in Directory.EnumerateDirectories(modsFolder))
            {
                if (!Path.GetFileName(dirPath).StartsWith(prefix, StringComparison.Ordinal) || ours.Contains(dirPath))
                    continue;

                if (File.GetAttributes(dirPath).HasFlag(FileAttributes.ReparsePoint))
                    throw new StoreException($"Mods 目录里出现指向登记根以外的测试 mod 链接：{dirPath}");
                log?.Invoke($"[冷启动] Mods/ 下有同前缀的目录未清扫（不是本工具合成的测试 mod）：{dirPath}");
            }

            SynthMarker.SweepUnowned(modsFolder, store);

            SynthModInstall install = Synth(() => SynthMod.Install(layout, new SynthModSpec
            {
                Owner = OwnerOf(number),
                EntryName = PackageIdFor(number),
                AboutXml = BuildAboutXml(number, productPackageIds),
                Files = Manifest(dllPath, engineDll, engineContentDir, contentDir, selfTests),
            }));
            CliLog.Info(install.ContentReused
                ? $"[冷启动] 测试 mod 内容与本轮输入相同，沿用：{install.ContentDir}"
                : $"[冷启动] 已重建测试 mod 内容：{install.ContentDir}");
        }

        return PackageIdFor(number);
    }

    internal static SynthModLayout LayoutFor(ClaimStore store, string modsFolder) => new()
    {
        Tool = SynthTool,
        ModsDir = modsFolder,
        ContentRoot = store.Root,
        StagingRoot = LoadDirPool.StagingRoot(modsFolder),
        ContentName = ContentDirName,
    };

    internal const string SynthTool = "rimrunner";

    private static string OwnerOf(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static T Synth<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (SynthModException exception)
        {
            throw new StoreException(exception.Message, exception);
        }
    }

    internal static void SweepUnowned(string modsFolder, ClaimStore store)
    {
        SynthModLayout layout = LayoutFor(store, modsFolder);
        foreach (SynthModEntry entry in Synth(() => SynthMod.Entries(layout)))
        {
            if (!int.TryParse(entry.Owner, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                || Owned(entry.Path, number, store))
                continue;

            Synth(() => SynthMod.Remove(layout, entry.Path));
            CliLog.Info($"[冷启动] 已摘除无主测试 mod {FormName(entry.Form)}：{Path.GetFileName(entry.Path)}");
        }
    }

    private static bool Owned(string path, int number, ClaimStore store)
    {
        try
        {
            return store.Read(number) is { } claim && (claim.TestMod == null || PathIdentity.Same(claim.TestMod, path));
        }
        catch (StoreException)
        {
            return true;
        }
    }

    private static string FormName(SynthModForm form) => form == SynthModForm.Link ? "链接" : "副本";

    private IReadOnlyList<SynthModFile> Manifest(string dllPath, string engineDll, string engineContentDir,
        string? contentDir, ArtifactGate.SelfTestParts? selfTests)
    {
        List<SynthModFile> assemblies =
        [
            new("Assemblies/" + descriptor.TestMod.AssemblyName, dllPath),
            new("Assemblies/" + EngineArtifact.FileName, engineDll),
        ];
        List<IEnumerable<SynthModFile>> groups = [assemblies, SynthMod.FilesUnder(engineContentDir)];
        if (selfTests != null)
        {
            assemblies.Add(new SynthModFile("Assemblies/" + EngineArtifact.SelfTestFileName, selfTests.Dll));
            groups.Add(SynthMod.FilesUnder(selfTests.ContentDir));
        }

        if (contentDir != null)
            groups.Add(SynthMod.FilesUnder(contentDir));
        return SynthMod.Merge([.. groups]);
    }

    private static void AssertCopyableContent(string contentDir, string what)
    {
        FileSystemSafety.AssertNoReparseInside(contentDir, "同步" + what);
        foreach (string reserved in ReservedTopLevelDirs)
        {
            if (Directory.Exists(Path.Combine(contentDir, reserved)))
            {
                throw new StoreException($"{what}目录不得含顶层 {reserved}/：{contentDir}");
            }
        }
    }

    public void Remove(string modsFolder, int number) => Unlink(store, DirFor(modsFolder, number));

    public static string? Unlink(ClaimStore store, string path)
    {
        if (RimTooling.IO.PathPresence.Of(path) == RimTooling.IO.PathPresenceKind.Absent)
            return null;
        string form = FormName(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            ? SynthModForm.Link
            : SynthModForm.Copy);
        string modsFolder = Path.GetDirectoryName(CommandContext.FullPath(path))!;
        return Synth(() => SynthMod.Remove(LayoutFor(store, modsFolder), path)) ? form : null;
    }

    public static string? ReleaseUnheld(ClaimStore store, int number, Func<ClaimRecord, bool> isLive)
    {
        using (store.SweepLock())
        {
            ClaimRecord? claim = store.Read(number);
            if (claim?.TestMod is not { } path
                || RimTooling.IO.PathPresence.Of(path) == RimTooling.IO.PathPresenceKind.Absent
                || isLive(claim))
                return null;
            return Unlink(store, path) is { } form ? $"已摘除测试 mod {form}：{path}" : null;
        }
    }

    public static void DeleteInstanceDir(ClaimStore store, int number)
    {
        string dir = store.InstanceDir(number);
        if (!Directory.Exists(dir))
            return;
        FileSystemSafety.AssertNoReparseInside(dir);
        Directory.Delete(dir, recursive: true);
    }

    public string BuildAboutXml(int number, IReadOnlyList<string> productPackageIds)
    {
        string loadAfter = string.Join("\r\n",
            productPackageIds.Select(id => $"    <li>{id}</li>"));
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<ModMetaData>
  <name>{descriptor.TestMod.DisplayName} ({number})</name>
  <author>{descriptor.TestMod.DisplayName}</author>
  <packageId>{PackageIdFor(number)}</packageId>
  <supportedVersions>
    <li>{descriptor.ResolveSupportedVersion()}</li>
  </supportedVersions>
  <description>RimRunner 冷启动合成的临时测试 mod（实例号 {number}）；测试程序集的载体。</description>
  <loadAfter>
{loadAfter}
  </loadAfter>
</ModMetaData>
";
    }

    public static void AddPackageIdToActiveMods(string modsConfigPath, string packageId)
    {
        var doc = new XmlDocument();
        doc.Load(modsConfigPath);
        XmlElement? activeMods = (XmlElement?)doc.SelectSingleNode("//activeMods") ?? throw new StoreException($"ModsConfig.xml 没有 activeMods 节点：{modsConfigPath}");
        string lower = packageId.ToLowerInvariant();
        foreach (XmlNode node in activeMods.SelectNodes("li")!)
        {
            if (node.InnerText!.Trim() == lower)
            {
                return;
            }
        }

        XmlElement li = doc.CreateElement("li");
        li.InnerText = lower;
        activeMods.AppendChild(li);
        doc.Save(modsConfigPath);
    }
}
