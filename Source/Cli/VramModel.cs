using System.IO.Compression;

namespace RimRunnerCli;

public static class VramModel
{
    public const int Revision = 3;

    public const long FixedOverheadBytes = 850_000_000;

    public const long NativeWorkingSetBytes = 500_000_000;

    public const double Safety = 1.15;

    public sealed record TextureLoad(long Compressible, long NonMultipleOf4, long PackedBytes, long LooseDdsBytes)
    {
        public static readonly TextureLoad Empty = new(0, 0, 0, 0);

        public TextureLoad Add(TextureLoad other) =>
            new(Compressible + other.Compressible, NonMultipleOf4 + other.NonMultipleOf4,
                PackedBytes + other.PackedBytes, LooseDdsBytes + other.LooseDdsBytes);
    }

    public static long Estimate(TextureLoad load) =>
        Estimate(load, RimRunner.RimRunnerEngineArgs.TextureTierNative);

    public static long Estimate(TextureLoad load, string tier) =>
        FixedOverheadBytes
        + (tier == RimRunner.RimRunnerEngineArgs.TextureTierPlaceholder ? 0 : NativeWorkingSetBytes)
        + (long)(load.LooseDdsBytes * Safety);

    public static long ResidentBytes(TextureLoad load) =>
        (long)((load.Compressible * 1.0 + load.NonMultipleOf4 * 4.0) * 4 / 3 + load.PackedBytes) + load.LooseDdsBytes;

    public sealed record ModTextures(string PackageId, int Files, TextureLoad Load);

    public static TextureLoad Scan(IReadOnlyList<string> activeIds, IReadOnlyDictionary<string, string> modDirs,
        string gameVersion) =>
        ScanByMod(activeIds, modDirs, gameVersion, out _)
            .Aggregate(TextureLoad.Empty, (sum, mod) => sum.Add(mod.Load));

    public static List<ModTextures> ScanByMod(IReadOnlyList<string> activeIds,
        IReadOnlyDictionary<string, string> modDirs, string gameVersion, out List<string> missing)
    {
        var active = new HashSet<string>(activeIds, StringComparer.OrdinalIgnoreCase);
        var result = new List<ModTextures>();
        missing = [];
        foreach (string id in activeIds)
        {
            if (!modDirs.TryGetValue(id, out string? dir))
            {
                missing.Add(id);
                continue;
            }

            Dictionary<string, TextureLoad> files = TextureFiles(LoadFolders(dir, gameVersion, active));
            result.Add(new ModTextures(id, files.Count,
                files.Values.Aggregate(TextureLoad.Empty, (sum, file) => sum.Add(file))));
        }

        return result;
    }

    internal static Dictionary<string, TextureLoad> TextureFiles(IEnumerable<string> foldersDescending)
    {
        var files = new Dictionary<string, TextureLoad>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in foldersDescending)
        {
            foreach ((string key, long bytes) in PackedTextures(folder))
                files.TryAdd(key, new TextureLoad(0, 0, bytes, 0));
            string textures = Path.Combine(folder, "Textures");
            if (!Directory.Exists(textures)) continue;
            foreach (string path in Directory.EnumerateFiles(textures, "*", SearchOption.AllDirectories))
            {
                string key = Path.GetRelativePath(folder, path).Replace('\\', '/');
                if (files.ContainsKey(key) || LooseTexture(path) is not TextureLoad load) continue;
                files.Add(key, load);
            }
        }

        var dds = new HashSet<string>(files.Keys.Where(key => key.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        foreach (string key in files.Keys.ToList())
        {
            if (key.Length > 4 && !key.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                               && dds.Contains(key[..^4] + ".dds"))
                files.Remove(key);
        }

        return files;
    }

    private static IEnumerable<(string Key, long Bytes)> PackedTextures(string folder)
    {
        string pack = Path.Combine(folder, "Assets.zip");
        if (!File.Exists(pack)) return [];
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(pack);
            if (archive.GetEntry("bake.manifest") == null) return [];
            var result = new List<(string, long)>();
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)) continue;
                string? source = name.EndsWith("-premul.dds", StringComparison.OrdinalIgnoreCase) ? name[..^"-premul.dds".Length]
                    : name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? name[..^".dds".Length]
                    : null;
                if (source != null) result.Add((source, entry.Length));
            }

            return result;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static TextureLoad? LooseTexture(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (extension == ".dds") return new TextureLoad(0, 0, 0, new FileInfo(path).Length);
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".psd")) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            (long width, long height) = extension switch
            {
                ".png" => PngSize(stream),
                ".psd" => PsdSize(stream),
                _ => JpegSize(stream),
            };
            if (width <= 0 || height <= 0) return null;
            return width % 4 != 0 || height % 4 != 0
                ? new TextureLoad(0, width * height, 0, 0)
                : new TextureLoad(width * height, 0, 0, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (long, long) PngSize(Stream stream)
    {
        var header = new byte[24];
        if (stream.ReadAtLeast(header, 24, throwOnEndOfStream: false) < 24 || header[0] != 0x89 || header[1] != 0x50)
            return (0, 0);
        return (BigEndian(header, 16, 4), BigEndian(header, 20, 4));
    }

    private static (long, long) PsdSize(Stream stream)
    {
        var header = new byte[26];
        if (stream.ReadAtLeast(header, 26, throwOnEndOfStream: false) < 26 || header[0] != '8' || header[1] != 'B'
            || header[2] != 'P' || header[3] != 'S')
            return (0, 0);
        return (BigEndian(header, 18, 4), BigEndian(header, 14, 4));
    }

    private static (long, long) JpegSize(Stream stream)
    {
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8) return (0, 0);
        var segment = new byte[7];
        while (true)
        {
            int marker = stream.ReadByte();
            while (marker == 0xFF) marker = stream.ReadByte();
            if (marker < 0 || marker == 0xD9 || marker == 0xDA) return (0, 0);
            if (stream.ReadAtLeast(segment, 2, throwOnEndOfStream: false) < 2) return (0, 0);
            long length = BigEndian(segment, 0, 2);
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (stream.ReadAtLeast(segment, 5, throwOnEndOfStream: false) < 5) return (0, 0);
                return (BigEndian(segment, 3, 2), BigEndian(segment, 1, 2));
            }

            if (length < 2) return (0, 0);
            stream.Seek(length - 2, SeekOrigin.Current);
            if (stream.ReadByte() != 0xFF) return (0, 0);
        }
    }

    private static long BigEndian(byte[] bytes, int offset, int count)
    {
        long value = 0;
        for (int i = 0; i < count; i++) value = (value << 8) | bytes[offset + i];
        return value;
    }

    internal static List<string> LoadFolders(string modDir, string gameVersion, ISet<string> active) =>
        [
            .. RimTooling.RimWorld.ModLoadFolders.Resolve(
                CommandContext.FullPath(modDir),
                gameVersion,
                active as IReadOnlySet<string> ?? new HashSet<string>(active, StringComparer.OrdinalIgnoreCase)),
        ];

    public static string? GameVersion(string gameExe)
    {
        if (string.IsNullOrWhiteSpace(gameExe)) return null;
        try
        {
            return RimTooling.RimWorld.GameLayout.FromExe(CommandContext.FullPath(gameExe)).ReadVersion()?.MajorMinor;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
