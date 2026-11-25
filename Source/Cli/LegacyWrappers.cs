using System.Xml;

namespace RimRunnerCli;

public static class LegacyWrappers
{
    public static void SweepOrphans(OrchestratorDescriptor descriptor, string modsFolder, ClaimStore store)
    {
        var claimed = new HashSet<int>(store.All().Select(record => record.Number));
        if (!Directory.Exists(modsFolder))
        {
            return;
        }

        foreach (string dirPath in Directory.EnumerateDirectories(modsFolder))
        {
            string name = Path.GetFileName(dirPath);
            OrchestratorDescriptor.UnitDescriptor? matched = null;
            int asNumber = 0;
            foreach (OrchestratorDescriptor.UnitDescriptor unit in descriptor.Units)
            {
                string prefix = unit.Name + "_";
                if (name.StartsWith(prefix, StringComparison.Ordinal)
                    && int.TryParse(name[prefix.Length..], out asNumber))
                {
                    matched = unit;
                    break;
                }
            }

            if (matched == null || claimed.Contains(asNumber) || store.Has(asNumber))
            {
                continue;
            }

            string? packageId = TryReadPackageId(Path.Combine(dirPath, "About", "About.xml"));
            if (packageId == null
                || !string.Equals(packageId.Trim(), $"{matched.PackageId}.{asNumber}",
                    StringComparison.OrdinalIgnoreCase))
            {
                CliLog.Warn($"[冷启动] Mods/ 下名字形如旧形态载入目录的目录未清扫（"
                    + (packageId == null
                        ? "无法读取 About/About.xml 的 packageId"
                        : $"About packageId 为 {packageId.Trim()}，不是 {matched.PackageId}.{asNumber}")
                    + $"）：{dirPath}");
                continue;
            }

            RemoveWrapper(dirPath);
            CliLog.Info($"[冷启动] 已清扫无主旧形态载入目录：{name}");
        }
    }

    internal static string? TryReadPackageId(string aboutPath)
    {
        if (!File.Exists(aboutPath))
        {
            return null;
        }

        try
        {
            var doc = new XmlDocument();
            doc.Load(aboutPath);
            return doc.SelectSingleNode("/ModMetaData/packageId")?.InnerText;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static void RemoveWrapper(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        SynthMarker.DeleteWithMarkerLast(path, DeleteWrapperContents);
    }

    private static void DeleteWrapperContents(string path)
    {
        var dirInfo = new DirectoryInfo(path);
        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new StoreException($"拒绝删除：旧形态载入目录本身带 ReparsePoint：{path}");
        }

        foreach (FileSystemInfo child in dirInfo.EnumerateFileSystemInfos())
        {
            if (child is FileInfo && child.Name == SynthMarker.FileName)
            {
                continue;
            }

            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (Junction.GetLinkInfo(child.FullName).Kind != RimTooling.Windows.ReparseLinkKind.Junction)
                {
                    throw new StoreException(
                        $"拒绝删除：旧形态载入目录内出现非 junction 的 reparse point：{child.FullName}");
                }

                Junction.Remove(child.FullName);
                continue;
            }

            if (child is DirectoryInfo childDir)
            {
                FileSystemSafety.AssertNoReparseInside(childDir.FullName);
                childDir.Delete(recursive: true);
            }
            else
            {
                child.Delete();
            }
        }
    }
}
