using System.Text.Json;

namespace RimRunnerCli;

internal sealed class EnginePackage
{
    internal const string RootVariable = "RIMRUNNER_PACKAGE_ROOT";

    internal const string HomeProperty = "RimRunnerHome";
    internal const string TargetsFileName = "RimRunner.targets";
    internal const string AuthoringFileName = "authoring.json";
    internal const string ChecksFileName = "checks.json";
    internal const string SelfTestsFileName = "selftests.json";
    internal const string FixtureIndexExeName = "RimRunnerFixtureIndex.exe";
    internal const string ManifestFileName = "package.json";

    internal string Root { get; }

    private EnginePackage(string root) => Root = root;

    internal static EnginePackage Locate(Func<string, string?> getEnvironmentVariable)
    {
        string? overridden = getEnvironmentVariable(RootVariable);
        string root = string.IsNullOrWhiteSpace(overridden) ? AppContext.BaseDirectory : overridden.Trim();
        return new EnginePackage(CommandContext.FullPath(root).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    internal static EnginePackage Locate() => Locate(CommandContext.GetEnvironmentVariable);

    internal string EngineDir => Path.Combine(Root, "engine");
    internal string DllPath => Path.Combine(EngineDir, EngineArtifact.FileName);
    internal string SelfTestDllPath => Path.Combine(EngineDir, EngineArtifact.SelfTestFileName);
    internal string RefDllPath => Path.Combine(EngineDir, "ref", EngineArtifact.FileName);
    internal const string RefSourceFileName = "RimRunnerRef.cs";
    internal string RefSourcePath => Path.Combine(EngineDir, "ref", RefSourceFileName);
    internal string ContentDir => Path.Combine(EngineDir, "Content");
    internal string SelfTestContentDir => Path.Combine(EngineDir, "SelfTestContent");
    internal string AuthoringPath => Path.Combine(EngineDir, AuthoringFileName);
    internal string ChecksPath => Path.Combine(EngineDir, ChecksFileName);
    internal string SelfTestsPath => Path.Combine(EngineDir, SelfTestsFileName);
    internal string FixtureIndexPath => Path.Combine(Root, "tools", "FixtureIndex", FixtureIndexExeName);
    internal string TargetsPath => Path.Combine(Root, TargetsFileName);
    internal string ObservationsDir => Path.Combine(Root, "observations");
    internal string ManifestPath => Path.Combine(Root, ManifestFileName);

    internal EnginePackage AssertComplete()
    {
        var missing = new List<string>();
        foreach (string file in new[] { DllPath, RefDllPath, RefSourcePath, AuthoringPath, ChecksPath, TargetsPath, ManifestPath })
        {
            if (!File.Exists(file)) missing.Add(file);
        }

        if (!Directory.Exists(ContentDir)) missing.Add(ContentDir + Path.DirectorySeparatorChar);
        if (missing.Count == 0) return this;

        throw new StoreException(
            $"RimRunner 安装目录不完整：{Root}"
            + string.Concat(missing.Select(path => "\n  缺 " + path)));
    }

    internal PackageManifest ReadManifest()
    {
        try
        {
            return PackageManifest.Parse(File.ReadAllText(ManifestPath));
        }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException)
        {
            throw new StoreException($"无法读取 RimRunner 构建清单：{ManifestPath}：{exception.Message}");
        }
    }
}

internal sealed record PackageManifest(string Commit, string Built, string Edition, bool Observations, bool SelfTests, GameFingerprint Game,
    string? DevTree = null)
{
    internal DateTimeOffset? BuiltAt =>
        DateTimeOffset.TryParseExact(Built, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out DateTimeOffset at) ? at : null;

    internal static PackageManifest Parse(string json)
    {
        using JsonDocument document = StrictJson.ParseObject(json);
        JsonElement root = document.RootElement;
        string Text(string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"缺字符串字段 {name}");
        if (!root.TryGetProperty("includes", out JsonElement includes) || includes.ValueKind != JsonValueKind.Object)
            throw new FormatException("缺对象字段 includes");
        bool Flag(string name) => includes.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new FormatException($"includes 缺布尔字段 {name}");
        if (!root.TryGetProperty("game", out JsonElement game))
            throw new FormatException("缺对象字段 game");
        string edition = Text("edition");
        if (edition is not ("maintainer" or "release"))
            throw new FormatException($"edition 只支持 maintainer 与 release：{edition}");
        string? devTree = root.TryGetProperty("devTree", out JsonElement tree) && tree.ValueKind == JsonValueKind.String
            ? tree.GetString()
            : null;
        return new PackageManifest(Text("commit"), Text("built"), edition, Flag("observations"), Flag("selfTests"),
            GameFingerprint.Parse(game), devTree);
    }
}
