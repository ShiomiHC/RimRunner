using System.IO;

namespace RimRunner;

internal static class RimRunnerModAssemblies
{
    internal sealed class Ejection
    {
        public required string Assembly;
        public required IReadOnlyList<(string Type, RimRunnerAbsentReferences.Hit Hit)> Causes;
        public bool NotLoaded;
        public string? Error;
        public IReadOnlyList<string> Skipped = [];
        public IReadOnlyList<string> Missing = [];
        public IReadOnlyList<string> Names = [];
    }

    private static Dictionary<ModContentPack, IReadOnlyList<Ejection>>? all;

    internal static IReadOnlyDictionary<ModContentPack, IReadOnlyList<Ejection>> All => all ??= FindAll();

    internal static IReadOnlyList<Ejection> Of(ModContentPack pack) =>
        All.TryGetValue(pack, out IReadOnlyList<Ejection>? found) ? found : [];

    internal static IReadOnlyList<(string PackageId, string Text)> Shortfalls =>
        All.Where(pair => !string.IsNullOrEmpty(pair.Key.PackageId))
            .Select(pair => (pair.Key.PackageId, Text: string.Join("；", pair.Value.Select(ejection => Describe(ejection, "")))))
            .OrderBy(entry => entry.PackageId, StringComparer.Ordinal)
            .ToList();

    internal static bool Short(string packageId) =>
        All.Keys.Any(pack => string.Equals(pack.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    internal static string? TextOf(string packageId) =>
        Shortfalls.Where(entry => string.Equals(entry.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Text)
            .FirstOrDefault();

    internal static string? OwnerText(string assemblyName)
    {
        foreach ((ModContentPack pack, IReadOnlyList<Ejection> ejections) in All)
        {
            foreach (Ejection ejection in ejections)
            {
                if (ejection.Names.Contains(assemblyName, StringComparer.OrdinalIgnoreCase))
                {
                    return "所属 mod：" + pack.PackageIdPlayerFacing + "（packageId 已装载；" + Describe(ejection, "") + "）";
                }
            }
        }

        return null;
    }

    private static Dictionary<ModContentPack, IReadOnlyList<Ejection>> FindAll()
    {
        Assembly[] domain = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic).ToArray();
        Func<FileInfo, bool> isLoaded = LoadedIn(domain);
        HashSet<string> locations = Locations(domain);
        Dictionary<string, string> loadErrors = new(StringComparer.OrdinalIgnoreCase);
        foreach (LogMessage message in Log.Messages.Where(message => message.type == LogMessageType.Error))
        {
            const string head = "Exception loading ";
            string text = message.text ?? "";
            int colon = text.IndexOf(':');
            if (text.StartsWith(head, StringComparison.Ordinal) && colon > head.Length
                && text.Substring(head.Length, colon - head.Length).EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                loadErrors.TryAdd(text.Substring(head.Length, colon - head.Length), FirstLine(text));
            }
        }

        Dictionary<ModContentPack, IReadOnlyList<Ejection>> found = [];
        foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
        {
            if (pack?.assemblies?.loadedAssemblies == null || string.IsNullOrEmpty(pack.RootDir))
            {
                continue;
            }

            List<Ejection> ejections = Find(pack, domain, isLoaded, locations, loadErrors);
            if (ejections.Count > 0)
            {
                found[pack] = ejections;
            }
        }

        return found;
    }

    private static List<Ejection> Find(ModContentPack pack, Assembly[] domain, Func<FileInfo, bool> isLoaded,
        HashSet<string> locations, IReadOnlyDictionary<string, string> loadErrors)
    {
        string root = Path.GetFullPath(pack.RootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;
        HashSet<Assembly> loaded = [.. pack.assemblies.loadedAssemblies];
        bool causes = pack.assemblies.loadedAssemblies.Contains(RimRunnerDefScope.EngineAssembly);
        List<Ejection> found = domain
            .Where(assembly => !loaded.Contains(assembly) && Under(assembly.Location, root))
            .Select(assembly => new Ejection
            {
                Assembly = assembly.GetName().Name!,
                Causes = causes ? RimRunnerAbsentReferences.Of(assembly).Ejecting : [],
                Names = [assembly.GetName().Name!],
            })
            .OrderBy(ejection => ejection.Assembly, StringComparer.Ordinal)
            .ToList();

        List<FileInfo> files = ModContentPack.GetAllFilesForModPreserveOrder(pack, "Assemblies/",
                extension => extension.ToLower() == ".dll")
            .Select(entry => entry.Item2)
            .ToList();
        Ejection? notLoaded = FindNotLoaded(files
            .Select(file => (file.Name, isLoaded(file),
                locations.Contains(file.FullName) ? null : loadErrors.GetValueOrDefault(file.Name)))
            .ToList());
        if (notLoaded != null)
        {
            notLoaded.Names = files
                .Where(file => !isLoaded(file))
                .Select(AssemblyNameOf)
                .ToList();
            found.Add(notLoaded);
        }

        return found;
    }

    private static HashSet<string> Locations(IReadOnlyList<Assembly> domain) =>
        new(domain.Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => Path.GetFullPath(assembly.Location)), StringComparer.OrdinalIgnoreCase);

    internal static Func<FileInfo, bool> LoadedIn(IReadOnlyList<Assembly> domain) =>
        LoadedIn(domain, RimRunnerAssemblyRedirects.Active);

    internal static Func<FileInfo, bool> LoadedIn(IReadOnlyList<Assembly> domain, IReadOnlyList<RimRunnerAssemblyRedirects.Rule> redirects)
    {
        HashSet<string> locations = Locations(domain);
        HashSet<string> names = new(domain.Select(assembly => assembly.GetName().Name!), StringComparer.OrdinalIgnoreCase);
        return file =>
        {
            if (locations.Contains(file.FullName))
            {
                return true;
            }

            try
            {
                return AssemblyName.GetAssemblyName(file.FullName).Name is { } name
                       && (names.Contains(name) || RimRunnerAssemblyRedirects.TargetOf(redirects, name) is { } target && names.Contains(target));
            }
            catch (Exception exception) when (exception is BadImageFormatException or IOException)
            {
                return false;
            }
        };
    }

    private static string AssemblyNameOf(FileInfo file)
    {
        try
        {
            return AssemblyName.GetAssemblyName(file.FullName).Name ?? Path.GetFileNameWithoutExtension(file.Name);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException)
        {
            return Path.GetFileNameWithoutExtension(file.Name);
        }
    }

    internal static Ejection? FindNotLoaded(IReadOnlyList<(string Name, bool Loaded, string? Error)> filesInOrder)
    {
        int first = -1;
        for (int i = 0; i < filesInOrder.Count; i++)
        {
            if (!filesInOrder[i].Loaded)
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return null;
        }

        int thrower = first;
        for (int i = first; i >= 0; i--)
        {
            if (filesInOrder[i].Error != null)
            {
                thrower = i;
                break;
            }
        }

        return new Ejection
        {
            Assembly = filesInOrder[thrower].Name,
            Causes = [],
            NotLoaded = true,
            Error = filesInOrder[thrower].Error,
            Skipped = filesInOrder.Skip(thrower + 1).Where(file => !file.Loaded).Select(file => file.Name).ToList(),
            Missing = filesInOrder.Where(file => !file.Loaded).Select(file => file.Name).ToList(),
            Names = filesInOrder.Where(file => !file.Loaded).Select(file => Path.GetFileNameWithoutExtension(file.Name)).ToList(),
        };
    }

    internal static string Describe(Ejection ejection, string who)
    {
        if (ejection.NotLoaded)
        {
            return who + "未能装入：" + string.Join("、", ejection.Missing)
                   + (ejection.Error == null ? "；无法获取装载错误级日志" : "；装载错误级日志：" + ejection.Error);
        }

        return who + "程序集 " + ejection.Assembly + " 被剔除：在进程内，不在该 mod 的 loadedAssemblies";
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline];
    }

    private static bool Under(string location, string root) =>
        !string.IsNullOrEmpty(location) && Path.GetFullPath(location).StartsWith(root, StringComparison.OrdinalIgnoreCase);
}
