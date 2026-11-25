using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keys = RimRunner.RimRunnerLiveContract;

namespace RimRunnerCli;

internal static class FilmCompare
{
    public const string CompareDirName = "compare";
    public const string ManifestFileName = "compare.json";

    private static readonly string[] ClipFormats = [Keys.FilmRoster.FormatMp4, Keys.FilmRoster.FormatGif];

    internal sealed record Run(string Dir, FilmRuns.RunName Name, IReadOnlyList<string> Shots, bool HasPaths);

    internal static List<Run> ListRuns(string sceneDir)
    {
        string runsDir = Path.Combine(sceneDir, FilmRuns.RunsDirName);
        if (!Directory.Exists(runsDir)) return [];
        var runs = new List<Run>();
        foreach (string dir in Directory.GetDirectories(runsDir))
        {
            if (FilmRuns.ParseDirName(Path.GetFileName(dir)) is not { } name) continue;
            runs.Add(new Run(dir, name, ShotsIn(dir),
                Directory.Exists(Path.Combine(dir, FilmRuns.PathsDirName))));
        }

        runs.Sort((a, b) => string.CompareOrdinal(a.Name.Name, b.Name.Name));
        return runs;
    }

    private static List<string> ShotsIn(string runDir)
    {
        string manifest = Path.Combine(runDir, FilmRuns.ManifestFileName);
        var shots = new List<string>();
        if (!File.Exists(manifest)) return shots;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(manifest))?["records"] is not JsonArray records) return shots;
            foreach (JsonObject record in records.OfType<JsonObject>())
            {
                if (record["roll"] != null || Text(record, "shot") is not { } shot) continue;
                if (Output(runDir, shot) != null && !shots.Contains(shot)) shots.Add(shot);
            }
        }
        catch (JsonException)
        {
        }

        return shots;
    }

    internal static string? Output(string runDir, string shot)
    {
        foreach (string format in ClipFormats.Append(Keys.FilmRoster.FormatPng))
        {
            string path = Path.Combine(runDir, shot + "." + format);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    internal static Run? Resolve(string reference, IReadOnlyList<Run> all, IReadOnlyList<Run> eligible, out string? error)
    {
        error = null;
        Run? run = reference switch
        {
            FilmRuns.RefLatest => eligible.Count >= 1 ? eligible[^1] : null,
            FilmRuns.RefPrev => eligible.Count >= 2 ? eligible[^2] : null,
            _ => all.LastOrDefault(item => item.Name.RunId == reference)
                 ?? all.LastOrDefault(item => item.Name.Tag == reference),
        };
        if (run == null)
        {
            error = reference switch
            {
                FilmRuns.RefLatest => "尚无拍摄",
                FilmRuns.RefPrev => eligible.Count == 0 ? "尚无拍摄" : "仅拍摄过 1 次，无上一次",
                _ => $"没有 runId 或 tag 为 {reference} 的拍摄",
            };
        }

        return run;
    }

    internal sealed record Pair(string Name, string A, string B, bool Clip, string Output);

    internal static List<Pair> Pairs(Run a, Run b, string? shot, bool paths, List<string> onlyA, List<string> onlyB)
    {
        var pairs = new List<Pair>();
        if (paths)
        {
            string pathsA = Path.Combine(a.Dir, FilmRuns.PathsDirName);
            string pathsB = Path.Combine(b.Dir, FilmRuns.PathsDirName);
            foreach ((string name, bool inA, bool inB) in Union(Names(pathsA, "*.mp4", files: true), Names(pathsB, "*.mp4", files: true)))
            {
                if (inA && inB) pairs.Add(new Pair(name, Path.Combine(pathsA, name), Path.Combine(pathsB, name), true, name));
                else (inA ? onlyA : onlyB).Add(name);
            }

            foreach ((string dir, bool inA, bool inB) in Union(StillDirs(pathsA), StillDirs(pathsB)))
            {
                if (!(inA && inB))
                {
                    (inA ? onlyA : onlyB).Add(dir + "/");
                    continue;
                }

                foreach ((string png, bool pngA, bool pngB) in Union(Names(Path.Combine(pathsA, dir), "*.png", files: true),
                             Names(Path.Combine(pathsB, dir), "*.png", files: true)))
                {
                    string name = dir + "/" + png;
                    if (pngA && pngB)
                        pairs.Add(new Pair(name, Path.Combine(pathsA, dir, png), Path.Combine(pathsB, dir, png), false, name));
                    else (pngA ? onlyA : onlyB).Add(name);
                }
            }

            return pairs;
        }

        foreach ((string name, bool inA, bool inB) in Union(a.Shots, b.Shots))
        {
            if (shot != null && name != shot) continue;
            if (!(inA && inB))
            {
                (inA ? onlyA : onlyB).Add(name);
                continue;
            }

            string fileA = Output(a.Dir, name)!;
            string fileB = Output(b.Dir, name)!;
            bool clip = !fileA.EndsWith("." + Keys.FilmRoster.FormatPng, StringComparison.Ordinal)
                        || !fileB.EndsWith("." + Keys.FilmRoster.FormatPng, StringComparison.Ordinal);
            pairs.Add(new Pair(name, fileA, fileB, clip,
                name + "." + (clip ? Keys.FilmRoster.FormatMp4 : Keys.FilmRoster.FormatPng)));
        }

        return pairs;
    }

    private static IEnumerable<string> Names(string dir, string pattern, bool files) =>
        !Directory.Exists(dir) ? []
        : (files ? Directory.GetFiles(dir, pattern) : Directory.GetDirectories(dir, pattern))
            .Select(Path.GetFileName).OfType<string>();

    private static IEnumerable<string> StillDirs(string pathsDir) =>
        Names(pathsDir, "*", files: false).Where(name => !name.EndsWith(".part", StringComparison.Ordinal));

    private static IEnumerable<(string Name, bool InA, bool InB)> Union(IEnumerable<string> a, IEnumerable<string> b)
    {
        var setA = new HashSet<string>(a, StringComparer.Ordinal);
        var setB = new HashSet<string>(b, StringComparer.Ordinal);
        return setA.Union(setB).Order(StringComparer.Ordinal).Select(name => (name, setA.Contains(name), setB.Contains(name)));
    }

    internal static List<string> Arguments(Pair pair, string output) => pair.Clip
        ?
        [
            "-y", "-v", "error", "-i", pair.A, "-i", pair.B, "-filter_complex",
            "[0:v][1:v]hstack=inputs=2,pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p[v]",
            "-map", "[v]", "-c:v", "libx264", "-crf", "18", "-preset", "medium", "-movflags", "+faststart", output,
        ]
        :
        [
            "-y", "-v", "error", "-i", pair.A, "-i", pair.B, "-filter_complex", "[0:v][1:v]hstack=inputs=2[v]",
            "-map", "[v]", "-frames:v", "1", output,
        ];

    internal static int Execute(string filmRoot, FilmFlow.Target target, string a, string b, bool paths, string ffmpeg,
        FilmRetention.Quota quota, Action<string> log)
    {
        string sceneDir = Path.Combine(filmRoot, target.Id);
        List<Run> all = ListRuns(sceneDir);
        List<Run> eligible =
        [
            .. all.Where(run => paths ? run.HasPaths
                : target.Shot != null ? run.Shots.Contains(target.Shot) : run.Shots.Count > 0),
        ];
        string what = paths ? "路径录像" : target.Shot != null ? $"镜头 {target.Shot}" : "镜头";
        Run? runA = Resolve(a, all, eligible, out string? errorA);
        Run? runB = Resolve(b, all, eligible, out string? errorB);
        if (runA == null || runB == null)
        {
            log($"[FAIL] {target.Id} 的{what}：" + (runA == null ? $"--a {a}：{errorA}" : $"--b {b}：{errorB}"));
            ListAll(sceneDir, all, log);
            return ExitCodes.Incomplete;
        }

        if (runA.Dir == runB.Dir)
        {
            log($"[FAIL] --a {a} 与 --b {b} 是同一次拍摄：{runA.Name.Name}");
            return ExitCodes.Incomplete;
        }

        var onlyA = new List<string>();
        var onlyB = new List<string>();
        List<Pair> pairs = Pairs(runA, runB, target.Shot, paths, onlyA, onlyB);
        if (pairs.Count == 0 || (target.Shot != null && (onlyA.Count > 0 || onlyB.Count > 0)))
        {
            Run? bare = eligible.Contains(runA) ? eligible.Contains(runB) ? null : runB : runA;
            log(bare != null
                ? $"[FAIL] {(bare == runA ? "A" : "B")} = {bare.Name.Name} 里没有{what}"
                : $"[FAIL] 两次拍摄没有共同的{what}：A = {runA.Name.Name}，B = {runB.Name.Name}");
            ListAll(sceneDir, all, log);
            return ExitCodes.Incomplete;
        }

        string outputDir = Path.Combine(sceneDir, CompareDirName, $"{runA.Name.RunId}-vs-{runB.Name.RunId}");
        if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);
        log($"[film] 左 A = {runA.Name.Name}，右 B = {runB.Name.Name}");
        int failed = 0;
        foreach (Pair pair in pairs)
        {
            string output = Path.Combine(outputDir, pair.Output.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            string? failure = RunFfmpeg(ffmpeg, Arguments(pair, output));
            if (failure != null)
            {
                failed++;
                log($"[FAIL] {pair.Name}：{failure}");
                continue;
            }

            log($"[film] {output}（{FilmFlow.FormatBytes(new FileInfo(output).Length)}）");
        }

        foreach (string name in onlyA) log($"  只在 A：{name}");
        foreach (string name in onlyB) log($"  只在 B：{name}");
        WriteManifest(Path.Combine(outputDir, ManifestFileName), runA, runB, pairs);
        Prune(Path.Combine(sceneDir, CompareDirName), quota.KeepRecent, outputDir, log);
        log(failed == 0
            ? $"[结果] FILM COMPARE：{pairs.Count} 项 → {outputDir}"
            : $"[结果] FILM COMPARE：{pairs.Count} 项里 {failed} 项拼接失败 → {outputDir}");
        return failed == 0 ? 0 : 1;
    }

    private static void ListAll(string sceneDir, IReadOnlyList<Run> all, Action<string> log)
    {
        if (all.Count == 0)
        {
            log($"  {Path.Combine(sceneDir, FilmRuns.RunsDirName)} 下没有拍摄目录");
            return;
        }

        log($"  {Path.Combine(sceneDir, FilmRuns.RunsDirName)} 下的拍摄目录（旧到新）：");
        foreach (Run run in all)
        {
            var parts = new List<string> { run.Shots.Count > 0 ? "镜头 " + string.Join("、", run.Shots) : "无镜头" };
            if (run.HasPaths) parts.Add("有路径录像");
            log($"  {run.Name.Name}  {string.Join("；", parts)}");
        }
    }

    private static void WriteManifest(string path, Run a, Run b, IReadOnlyList<Pair> pairs)
    {
        var root = new JsonObject
        {
            ["a"] = new JsonObject { ["runDir"] = a.Name.Name, ["runId"] = a.Name.RunId, ["tag"] = a.Name.Tag },
            ["b"] = new JsonObject { ["runDir"] = b.Name.Name, ["runId"] = b.Name.RunId, ["tag"] = b.Name.Tag },
            ["items"] = new JsonArray([.. pairs.Select(pair => (JsonNode)pair.Name)]),
        };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    private static void Prune(string compareDir, int keepRecent, string current, Action<string> log)
    {
        string[] stale =
        [
            .. new DirectoryInfo(compareDir).GetDirectories()
                .OrderByDescending(dir => dir.LastWriteTimeUtc)
                .Where(dir => !PathIdentity.Same(dir.FullName, current))
                .Skip(Math.Max(keepRecent - 1, 0))
                .Select(dir => dir.FullName),
        ];
        foreach (string dir in stale)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log($"[WARN] 未能删除 {dir}：{exception.Message}");
            }
        }
    }

    private static readonly TimeSpan FfmpegLimit = TimeSpan.FromMinutes(10);

    private static string? RunFfmpeg(string ffmpeg, IReadOnlyList<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            CommandContext.Prepare(start, Path.GetDirectoryName(ffmpeg)!);
            using Process process = Process.Start(start) ?? throw new Win32Exception($"无法启动 {ffmpeg}");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (ChildProcess.WaitOrStop(process, FfmpegLimit, "ffmpeg 比对") is string timedOut) return timedOut;
            stdout.GetAwaiter().GetResult();
            string log = stderr.GetAwaiter().GetResult();
            if (process.ExitCode == 0) return null;
            string[] tail = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return $"ffmpeg 退出码 {process.ExitCode}" + (tail.Length > 0 ? "：" + string.Join(" / ", tail.TakeLast(3)) : "");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            return exception.Message;
        }
    }

    private static string? Text(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
