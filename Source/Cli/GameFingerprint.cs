using System.Security.Cryptography;
using System.Text.Json;

namespace RimRunnerCli;

internal sealed record GameFingerprint(string Version, IReadOnlyDictionary<string, string> Files)
{
    internal const string VersionFile = "Version.txt";

    internal static GameFingerprint Parse(JsonElement game)
    {
        if (game.ValueKind != JsonValueKind.Object)
            throw new FormatException("game 不是对象");
        string version = game.TryGetProperty("version", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException("game 缺字符串字段 version");
        if (!game.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Object)
            throw new FormatException("game 缺对象字段 files");
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty file in files.EnumerateObject())
        {
            map[file.Name] = file.Value.ValueKind == JsonValueKind.String
                ? file.Value.GetString()!
                : throw new FormatException($"game.files.{file.Name} 不是字符串");
        }

        if (map.Count == 0) throw new FormatException("game.files 为空");
        return new GameFingerprint(version, map);
    }

    internal string? Mismatch(string gameDir)
    {
        string versionPath = Path.Combine(gameDir, VersionFile);
        string? installed = File.Exists(versionPath) ? File.ReadLines(versionPath).FirstOrDefault()?.Trim() : null;
        var changed = new List<string>();
        var missing = new List<string>();
        foreach ((string name, string hash) in Files)
        {
            string path = Path.Combine(gameDir, name);
            if (!File.Exists(path))
            {
                missing.Add(name);
                continue;
            }

            using FileStream stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), hash, StringComparison.OrdinalIgnoreCase))
                changed.Add(name);
        }

        if (installed == Version && changed.Count == 0 && missing.Count == 0) return null;
        string text = "[engine] RimRunner 构建时的游戏与现装不匹配："
            + (installed == null ? $"构建时 {Version}，无法读取现装版本（{versionPath} 不存在或为空）"
                : installed == Version ? $"版本同为 {Version}"
                : $"构建时 {Version}，现装 {installed}");
        if (changed.Count > 0) text += "；程序集已改变 " + string.Join("、", changed);
        if (missing.Count > 0) text += "；现装缺 " + string.Join("、", missing);
        return text + $"（游戏目录 {gameDir}）";
    }

    private static readonly Dictionary<string, string> Identities = new(StringComparer.OrdinalIgnoreCase);

    internal static string Identity(string gameDir)
    {
        lock (Identities)
        {
            if (Identities.TryGetValue(gameDir, out string? known)) return known;
            IReadOnlyCollection<string> names = EnginePackage.Locate().ReadManifest().Game.Files.Keys.ToList();
            string versionPath = Path.Combine(gameDir, VersionFile);
            string version = File.Exists(versionPath) ? File.ReadLines(versionPath).FirstOrDefault()?.Trim() ?? "" : "";
            var lines = new List<string> { version };
            foreach (string name in names.Order(StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.Combine(gameDir, name);
                string hash;
                try
                {
                    using FileStream stream = File.OpenRead(path);
                    hash = Convert.ToHexString(SHA256.HashData(stream));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    hash = "absent";
                }

                lines.Add(name.ToLowerInvariant() + "=" + hash);
            }

            string digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..8];
            string identity = (version.Length > 0 ? version : "版本未知") + " " + digest.ToLowerInvariant();
            Identities[gameDir] = identity;
            return identity;
        }
    }

    internal static void Warn(OrchestratorDescriptor descriptor, Action<string>? log)
    {
        EnginePackage package = EnginePackage.Locate();
        GameFingerprint built = package.ReadManifest().Game;
        string gameDir = Path.GetDirectoryName(CommandContext.FullPath(descriptor.GameExe)) ?? "";
        CliBatchWarnings.Checked(RimRunner.RimRunnerWarningKey.PackageGameMismatch);
        if (built.Mismatch(gameDir) is string mismatch)
        {
            log?.Invoke("[WARN] " + mismatch);
            CliBatchWarnings.Add(RimRunner.RimRunnerWarningKey.PackageGameMismatch, mismatch);
        }
        else
        {
            CliLog.Info($"[游戏] 现装与 RimRunner 构建时的游戏匹配：{built.Version}，{built.Files.Count} 份程序集（游戏目录 {gameDir}）");
        }
    }
}
