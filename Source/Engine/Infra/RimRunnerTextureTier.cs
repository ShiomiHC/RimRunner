using System.IO;
using System.Threading;
using Waylith.Assets;
using Waylith.Rendering;
using WTexture = Waylith.Rendering.Texture2D;

namespace RimRunner;

/// <summary>贴图档位，按保真度升序。</summary>
public enum RimRunnerTextures
{
    /// <summary>贴图换成长边 8 像素、保长宽比的占位像素（含原版与 DLC，mod 的 .dds 除外）。</summary>
    /// <remarks>原版静态图集不烘焙。</remarks>
    Placeholder,
    /// <summary>原图。</summary>
    Native
}

public sealed class RimRunnerTextureTierMod : Mod
{
    public RimRunnerTextureTierMod(ModContentPack content) : base(content)
    {
        RimRunnerTextureTier.Install();
    }
}

internal static class RimRunnerTextureTier
{
    private const int PlaceholderLongSide = 8;

    internal static string Current { get; private set; } = RimRunnerEngineArgs.TextureTierNative;

    internal static bool IsPlaceholder =>
        Current == RimRunnerEngineArgs.TextureTierPlaceholder;

    internal static RimRunnerTextures CurrentLevel => Parse(Current) ?? RimRunnerTextures.Native;

    internal static string Name(RimRunnerTextures level) => level switch
    {
        RimRunnerTextures.Placeholder => RimRunnerEngineArgs.TextureTierPlaceholder,
        _ => RimRunnerEngineArgs.TextureTierNative,
    };

    private static RimRunnerTextures? Parse(string tier)
    {
        foreach (RimRunnerTextures level in Enum.GetValues(typeof(RimRunnerTextures)))
            if (Name(level) == tier)
                return level;
        return null;
    }

    internal static string? FloorRefusal(RimRunnerTestCase testCase) =>
        FloorRefusal(testCase.MinTextures, CurrentLevel, testCase.Id);

    internal static string? FloorRefusal(RimRunnerTextures floor, RimRunnerTextures current, string caseId)
    {
        if (floor <= current)
            return null;
        return "用例 " + caseId + " 申报贴图档位下限 " + Name(floor) + "，本实例是 " + Name(current);
    }

    private static readonly Lock Gate = new();
    private static int replaced;
    private static int passedThrough;
    private static string? firstFailure;
    private static bool atlasSkipped;

    private static HashSet<WTexture>? produced = new(ReferenceEqualityComparer.Instance);
    private static HashSet<string>? passedPaths = new(StringComparer.OrdinalIgnoreCase);

    internal static void Install()
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable)
            || !GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.TextureTier, out string tier))
        {
            return;
        }

        switch (tier)
        {
            case RimRunnerEngineArgs.TextureTierNative:
                Current = tier;
                return;
            case RimRunnerEngineArgs.TextureTierPlaceholder:
                break;
            default:
                RimRunnerStartupErrors.Raise("无法识别的贴图档位 -" + RimRunnerEngineArgs.TextureTier + "=" + tier
                          + "，本实例按 native 加载");
                return;
        }

        MethodInfo target = AccessTools.Method(typeof(WTexture), nameof(WTexture.LoadFromFile),
            [typeof(string), typeof(bool)]);
        if (target == null)
        {
            RimRunnerStartupErrors.Raise("无法解析 Waylith.Rendering.Texture2D.LoadFromFile(string, bool)，贴图档位 " + tier
                                         + " 不生效，本实例按 native 加载");
            return;
        }

        Current = tier;
        var harmony = new Harmony("rimrunner.textures");
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(RimRunnerTextureTier), nameof(Prefix)));
        AssetPrefetcher.Drain();
        MethodInfo bake = AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases));
        harmony.Patch(bake, prefix: new HarmonyMethod(typeof(RimRunnerTextureTier), nameof(SkipAtlasPrefix)));
    }

    internal static void AfterStartup()
    {
        if (Current == RimRunnerEngineArgs.TextureTierNative)
        {
            return;
        }

        LongEventHandler.ExecuteWhenFinished(() =>
        {
            Census census = TakeCensus();
            const string verb = "占位 ";
            string line = "贴图档位 " + Current + "：内容包贴图 " + census.Total
                          + "（" + verb + census.Ours + " · 原样 " + census.Passed + " · DDS 原样 " + census.Dds
                          + (census.Missed > 0 ? " · 未经补丁 " + census.Missed : "") + "）"
                          + "· 包外" + verb + census.OursOutside
                          + (atlasSkipped ? " · 静态图集未建" : "")
                          + (firstFailure != null ? " · 首个失败 " + firstFailure : "");
            if (census.Missed > 0)
            {
                RimRunnerEngineLog.Warn(line + " · 首张未经补丁 " + census.FirstMissed);
            }
            else
            {
                RimRunnerEngineLog.Info(line);
            }
        });
    }

    private sealed class Census
    {
        internal int Total;
        internal int Ours;
        internal int Passed;
        internal int Dds;
        internal int Missed;
        internal int OursOutside;
        internal string? FirstMissed;
    }

    private static Census TakeCensus()
    {
        var census = new Census();
        lock (Gate)
        {
            var roots = new List<string>();
            int unexplained = 0;
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                roots.Add(DirectoryPrefix(mod.RootDir));
                foreach (KeyValuePair<string, UnityEngine.Texture2D> item in mod.GetContentHolder<UnityEngine.Texture2D>().contentList)
                {
                    if (item.Value == null)
                    {
                        continue;
                    }

                    census.Total++;
                    if (item.Value.CoreTexture is WTexture core && produced!.Remove(core))
                    {
                        census.Ours++;
                    }
                    else if (HasDds(mod, item.Key))
                    {
                        census.Dds++;
                    }
                    else
                    {
                        unexplained++;
                        census.FirstMissed ??= mod.PackageIdPlayerFacing + " " + item.Key;
                    }
                }
            }

            census.OursOutside = produced!.Count;
            census.Passed = Math.Min(unexplained,
                passedPaths!.Count(path => roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase))));
            census.Missed = unexplained - census.Passed;
            if (census.Missed == 0)
            {
                census.FirstMissed = null;
            }

            produced = null;
            passedPaths = null;
        }

        return census;
    }

    private static string DirectoryPrefix(string dir)
    {
        string full = Path.GetFullPath(dir);
        return full.EndsWith(Path.DirectorySeparatorChar.ToString()) ? full : full + Path.DirectorySeparatorChar;
    }

    private static bool HasDds(ModContentPack mod, string key)
    {
        foreach (string folder in mod.foldersToLoadDescendingOrder)
        {
            if (File.Exists(Path.Combine(folder, "Textures", key + ".dds")))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Prefix(string filePath, bool premultiplyAlpha, ref WTexture __result)
    {
        string fullPath = filePath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
            WTexture texture = Placeholder(filePath, fullPath);
            lock (Gate)
            {
                replaced++;
                produced?.Add(texture);
            }

            __result = texture;
            return false;
        }
        catch (Exception exception)
        {
            Pass(fullPath, exception);
            return true;
        }
    }

    private static void Pass(string fullPath, Exception? exception)
    {
        lock (Gate)
        {
            passedThrough++;
            passedPaths?.Add(fullPath);
            if (exception != null)
            {
                firstFailure ??= fullPath + "：" + exception.GetType().Name + ": " + exception.Message;
            }
        }
    }

    private static bool SkipAtlasPrefix()
    {
        atlasSkipped = true;
        return false;
    }

    private static WTexture Placeholder(string filePath, string fullPath)
    {
        (int width, int height) = SourceSize(fullPath);
        float scale = Math.Min(1f, (float)PlaceholderLongSide / Math.Max(width, height));
        int w = Math.Max(1, (int)Math.Round(width * scale));
        int h = Math.Max(1, (int)Math.Round(height * scale));
        WTexture texture = NewTexture(filePath, fullPath, w, h, LoadedTextureFormat.Rgba32, 1, opaque: true, default);
        var pixels = new Waylith.Math.Color32[w * h];
        var grey = new Waylith.Math.Color32(128, 128, 128, 255);
        for (int i = 0; i < pixels.Length; i++) pixels[i] = grey;
        texture.CpuData!.SetPixels32(pixels);
        texture.Apply(updateMipmaps: false);
        return texture;
    }

    private static (int Width, int Height) SourceSize(string fullPath)
    {
        if (GpuTextureService.TryDescribeTexture(fullPath, premultiplied: false, out AssetPackTextureInfo.Info info)
            && info.Width > 0 && info.Height > 0)
        {
            return (info.Width, info.Height);
        }

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            if (ImageHeader.TryReadSize(stream, out int width, out int height))
            {
                return (width, height);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return (PlaceholderLongSide, PlaceholderLongSide);
    }

    private static WTexture NewTexture(string filePath, string fullPath, int width, int height, LoadedTextureFormat format,
        int mipCount, bool opaque, TextureSwizzle swizzle)
    {
        var texture = new WTexture(width, height, format, linear: false, mipCount, createUninitialized: true)
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            Opaque = opaque,
            TrackerName = fullPath,
            filterMode = Waylith.Rendering.FilterMode.Trilinear,
            anisoLevel = 2,
        };
        texture.CpuData!.Swizzle = swizzle;
        return texture;
    }
}

internal static class ImageHeader
{
    internal static bool TryReadSize(Stream stream, out int width, out int height)
    {
        width = height = 0;
        var head = new byte[26];
        int read = ReadUpTo(stream, head, 0, head.Length);
        if (read >= 24 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[12] == 0x49 && head[13] == 0x48 && head[14] == 0x44 && head[15] == 0x52)
        {
            width = BigEndian(head, 16);
            height = BigEndian(head, 20);
            return width > 0 && height > 0;
        }

        if (read >= 22 && head[0] == '8' && head[1] == 'B' && head[2] == 'P' && head[3] == 'S')
        {
            height = BigEndian(head, 14);
            width = BigEndian(head, 18);
            return width > 0 && height > 0;
        }

        if (read >= 4 && head[0] == 0xFF && head[1] == 0xD8)
        {
            return TryReadJpegSize(stream, head, read, out width, out height);
        }

        return false;
    }

    private static bool TryReadJpegSize(Stream stream, byte[] head, int buffered, out int width, out int height)
    {
        width = height = 0;
        long position = 2;
        var segment = new byte[9];
        while (true)
        {
            if (!ReadAt(stream, head, buffered, position, segment, 4)) return false;
            if (segment[0] != 0xFF) return false;
            byte marker = segment[1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            int length = (segment[2] << 8) | segment[3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (!ReadAt(stream, head, buffered, position, segment, 9)) return false;
                height = (segment[5] << 8) | segment[6];
                width = (segment[7] << 8) | segment[8];
                return width > 0 && height > 0;
            }

            if (marker == 0xD9 || marker == 0xDA || length < 2) return false;
            position += 2 + length;
        }
    }

    private static bool ReadAt(Stream stream, byte[] head, int buffered, long position, byte[] target, int count)
    {
        if (position + count <= buffered)
        {
            Array.Copy(head, position, target, 0, count);
            return true;
        }

        if (!stream.CanSeek) return false;
        stream.Seek(position, SeekOrigin.Begin);
        return ReadUpTo(stream, target, 0, count) == count;
    }

    private static int ReadUpTo(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int got = stream.Read(buffer, offset + total, count - total);
            if (got <= 0) break;
            total += got;
        }

        return total;
    }

    private static int BigEndian(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
}
