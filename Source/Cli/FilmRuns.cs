using System.Globalization;
using System.Text.RegularExpressions;
using Keys = RimRunner.RimRunnerLiveContract;

namespace RimRunnerCli;

internal static partial class FilmRuns
{
    public const string RunsDirName = "runs";
    public const string PathsDirName = "paths";
    public const string LatestSuffix = ".latest";
    public const string ManifestFileName = "film-run.json";
    public const int TagMaxLength = 32;
    public const string RefLatest = "latest";
    public const string RefPrev = "prev";
    private const string PartSuffix = ".part";
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    private static readonly string[] Formats =
        [Keys.FilmRoster.FormatPng, Keys.FilmRoster.FormatGif, Keys.FilmRoster.FormatMp4];

    internal static string DirName(DateTimeOffset startedAt, string runId, string? tag) =>
        startedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture) + "-" + runId + (tag != null ? "-" + tag : "");

    internal sealed record RunName(string Name, string Timestamp, string RunId, string? Tag);

    [GeneratedRegex(@"^(\d{8}-\d{6})-([0-9a-f]{8})(?:-(.+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex RunNamePattern();

    internal static RunName? ParseDirName(string name)
    {
        Match match = RunNamePattern().Match(name);
        return match.Success
            ? new RunName(name, match.Groups[1].Value, match.Groups[2].Value,
                match.Groups[3].Success ? match.Groups[3].Value : null)
            : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._\-\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex("^[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdShape();

    internal static string? TagError(string tag)
    {
        if (tag.Length is 0 or > TagMaxLength)
            return $"--tag 须是 1 到 {TagMaxLength} 个字符：{tag}";
        if (!TagPattern().IsMatch(tag))
            return $"--tag 只收英文字母、数字、. _ - 与汉字：{tag}";
        if (tag.EndsWith('.'))
            return $"--tag 不能以 . 结尾：{tag}";
        if (RunIdShape().IsMatch(tag))
            return $"--tag 不能写成 8 位十六进制，那是 runId 的写法：{tag}";
        if (tag is RefLatest or RefPrev)
            return $"--tag 不能是 {tag}：该名是 film compare 的保留引用";
        return null;
    }

    internal static string Stamp(string runId, RunSourceIdentity? source, string? tag)
    {
        string origin = source is { Error: null, Commit: { } commit }
            ? commit[..Math.Min(7, commit.Length)] + (source.ChangeCount > 0 ? "*" : "")
            : "来源不明";
        return $"run {runId} · {origin}" + (tag != null ? " · " + tag : "");
    }

    internal static string LatestName(string name, string extension) => name + LatestSuffix + extension;

    internal static void PromoteShot(string sceneDir, string shotName, IEnumerable<string> produced)
    {
        ClearShotLegacy(sceneDir, shotName);
        foreach (string format in Formats)
            DeleteFile(Path.Combine(sceneDir, LatestName(shotName, "." + format)));
        foreach (string file in produced)
            CopyFile(file, Path.Combine(sceneDir, LatestName(shotName, Path.GetExtension(file))));
    }

    internal static void ClearShotLegacy(string sceneDir, string shotName)
    {
        if (!Directory.Exists(sceneDir)) return;
        foreach (string format in Formats)
            DeleteFile(Path.Combine(sceneDir, shotName + "." + format));
        string frames = Path.Combine(sceneDir, shotName + RimRunner.RimRunnerDataLayout.FilmFramesSuffix);
        if (Directory.Exists(frames)) Directory.Delete(frames, recursive: true);
        ClearLegacyRolls(sceneDir, shotName);
    }

    internal static void ClearLegacyRolls(string sceneDir, string shotName)
    {
        if (!Directory.Exists(sceneDir)) return;
        string escaped = Regex.Escape(shotName);
        var rollFile = new Regex("^" + escaped + @"\.-?\d+\.(png|gif|mp4)(\.part)?$", RegexOptions.CultureInvariant);
        var rollFrames = new Regex("^" + escaped + @"\.-?\d+\.frames$", RegexOptions.CultureInvariant);
        foreach (string file in Directory.GetFiles(sceneDir))
            if (rollFile.IsMatch(Path.GetFileName(file))) File.Delete(file);
        foreach (string dir in Directory.GetDirectories(sceneDir))
            if (rollFrames.IsMatch(Path.GetFileName(dir))) Directory.Delete(dir, recursive: true);
    }

    internal static void PromotePaths(string sceneDir, string runPathsDir)
    {
        string latest = Path.Combine(sceneDir, PathsDirName);
        if (Directory.Exists(latest)) Directory.Delete(latest, recursive: true);
        if (!Directory.Exists(runPathsDir)) return;
        Directory.CreateDirectory(latest);
        foreach (string file in Directory.GetFiles(runPathsDir))
        {
            if (file.EndsWith(PartSuffix, StringComparison.Ordinal)) continue;
            CopyFile(file, Path.Combine(latest,
                LatestName(Path.GetFileNameWithoutExtension(file), Path.GetExtension(file))));
        }

        foreach (string dir in Directory.GetDirectories(runPathsDir))
        {
            string name = Path.GetFileName(dir);
            if (name.EndsWith(PartSuffix, StringComparison.Ordinal)) continue;
            string target = Path.Combine(latest, name + LatestSuffix);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(dir))
                CopyFile(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

    private static void CopyFile(string source, string target)
    {
        string part = target + PartSuffix;
        File.Copy(source, part, overwrite: true);
        File.Move(part, target, overwrite: true);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
