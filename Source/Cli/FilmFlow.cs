using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keys = RimRunner.RimRunnerLiveContract;

namespace RimRunnerCli;

public static class FilmFlow
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(70);
    public const string ResultFileName = RimRunner.RimRunnerDataLayout.FilmResultFileName;

    public sealed record Options(IReadOnlyList<Target> Targets, bool KeepFrames, FilmMode Mode, int? TimeoutMinutes,
        string? Ffmpeg, int PathsFps = DefaultPathsFps, int? Roll = null, string? Tag = null, bool Clean = false,
        FilmRetention.Quota? Quota = null, bool Mute = false);

    internal sealed record Provenance(string RunId, DateTimeOffset StartedAt, RunSourceIdentity? Source, string? ModList);

    public const int DefaultPathsFps = 60;

    public enum FilmMode
    {
        Shots,
        Test,
        Paths,
    }

    public sealed record Target(string Id, string? Shot);

    public sealed record ShotSpec(string Id, string Name, bool Clip, IReadOnlyList<string> Formats,
        int Width, int Height, long? MaxBytes, int Fps, int? Seed = null);

    public sealed record PathSpec(string Id, int Index, string? Leaf, IReadOnlyList<string> Path,
        IReadOnlyList<int> Speeds, IReadOnlyList<int> Dwells, int Width, int Height);

    public sealed record ShowcaseSpec(string Id, string Title, IReadOnlyList<string> Leaves, IReadOnlyList<ShotSpec> Shots,
        IReadOnlyList<PathSpec> Paths);

    public sealed record Roster(IReadOnlyList<string> RegistryFaults, IReadOnlyList<ShowcaseSpec> Showcases);

    public sealed record WorkItem(string Id, ShotSpec? Shot, string? Step, PathSpec? Path = null, int? Roll = null,
        int? Seed = null)
    {
        public string Label => Path != null ? PathLabel(Path)
            : Shot != null ? Roll != null ? $"{Id}/{Shot.Name} 第 {Roll} 掷" : $"{Id}/{Shot.Name}"
            : Step != null ? $"{Id} → {Step}" : Id;
    }

    internal static IReadOnlyList<int> RollSeeds(int current, int count)
    {
        var seeds = new List<int> { current };
        for (int roll = 2; seeds.Count < count; roll++)
        {
            uint hash = 2166136261;
            foreach (char character in current + "#" + roll)
                hash = (hash ^ character) * 16777619;
            int seed = (int)(hash & 0x7FFFFFFF);
            if (!seeds.Contains(seed)) seeds.Add(seed);
        }

        return seeds;
    }

    internal static string RollName(string shotName, int seed) => shotName + "." + seed;

    public static List<Target>? ParseTargets(IReadOnlyList<string> raw, FilmMode mode, out string? error)
    {
        error = null;
        var targets = new List<Target>();
        foreach (string token in raw)
        {
            int slash = token.IndexOf('/');
            string id = slash < 0 ? token : token[..slash];
            string? shot = slash < 0 ? null : token[(slash + 1)..];
            if (id.Length == 0 || shot is { Length: 0 } || (shot != null && shot.Contains('/')))
            {
                error = $"选择器写法是 <场面 Id> 或 <场面 Id>/<镜头名>：{token}";
                return null;
            }

            if (mode != FilmMode.Shots && shot != null)
            {
                error = $"{(mode == FilmMode.Paths ? "--paths" : "--test")} 只收场面 Id，不收镜头：{token}";
                return null;
            }

            if (!targets.Contains(new Target(id, shot)))
                targets.Add(new Target(id, shot));
        }

        return targets;
    }

    public static string? LocateFfmpeg(out string where)
    {
        string declared = RimRunnerConfig.Machine().FfmpegPath.Value;
        if (!string.IsNullOrWhiteSpace(declared))
        {
            where = $"机器级 {RimRunnerConfig.MachineFileName} 的 {RimRunnerConfig.FfmpegPathKey}（{declared}）";
            return File.Exists(declared) ? declared : null;
        }

        where = $"PATH（机器级 {RimRunnerConfig.MachineFileName} 未指定 {RimRunnerConfig.FfmpegPathKey}）";
        string[] names = OperatingSystem.IsWindows() ? ["ffmpeg.exe"] : ["ffmpeg"];
        foreach (string directory in (CommandContext.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), name);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    public static string MissingFfmpegLine(string where) =>
        $"[FAIL] 无法编码：已查 {where}，未找到 ffmpeg";

    public static Roster ParseRoster(string body)
    {
        JsonObject root = JsonNode.Parse(body) as JsonObject ?? throw new JsonException("名册根不是对象");
        var showcases = new List<ShowcaseSpec>();
        foreach (JsonNode? node in Array(root, Keys.FilmRoster.Showcases))
        {
            if (node is not JsonObject showcase) continue;
            string id = Text(showcase, Keys.FilmRoster.Id) ?? throw new JsonException("名册条目缺 id");
            var shots = new List<ShotSpec>();
            foreach (JsonNode? shotNode in Array(showcase, Keys.FilmRoster.Shots))
            {
                if (shotNode is not JsonObject shot) continue;
                shots.Add(new ShotSpec(id,
                    Text(shot, Keys.FilmRoster.Name) ?? throw new JsonException($"场面 {id} 的镜头缺 name"),
                    shot[Keys.FilmRoster.Clip]?.GetValue<bool>() ?? false,
                    [.. Array(shot, Keys.FilmRoster.Formats).Select(item => item?.GetValue<string>() ?? "")],
                    Int(shot, Keys.FilmRoster.Width), Int(shot, Keys.FilmRoster.Height),
                    shot[Keys.FilmRoster.MaxBytes]?.GetValue<long>(),
                    Int(shot, Keys.FilmRoster.Fps),
                    shot[Keys.FilmRoster.Seed]?.GetValue<int>()));
            }

            var paths = new List<PathSpec>();
            foreach (JsonNode? pathNode in Array(showcase, Keys.FilmRoster.Paths))
            {
                if (pathNode is not JsonObject path) continue;
                paths.Add(new PathSpec(id,
                    Int(path, Keys.FilmRoster.Index),
                    Text(path, Keys.FilmRoster.Leaf),
                    [.. Array(path, Keys.FilmRoster.Path).Select(item => item?.GetValue<string>() ?? "")],
                    [.. Array(path, Keys.FilmRoster.Speeds).Select(item => item?.GetValue<int>() ?? 0)],
                    [.. Array(path, Keys.FilmRoster.Dwells).Select(item => item?.GetValue<int>() ?? 0)],
                    Int(path, Keys.FilmRoster.Width), Int(path, Keys.FilmRoster.Height)));
            }

            showcases.Add(new ShowcaseSpec(id, Text(showcase, Keys.FilmRoster.Title) ?? id,
                [.. Array(showcase, Keys.FilmRoster.Leaves).Select(item => item?.GetValue<string>() ?? "")], shots, paths));
        }

        return new Roster([.. Array(root, Keys.FilmRoster.RegistryFaults).Select(item => item?.GetValue<string>() ?? "")],
            showcases);
    }

    public static List<WorkItem> Resolve(Roster roster, IReadOnlyList<Target> targets, FilmMode mode, out string? error)
    {
        error = null;
        var items = new List<WorkItem>();
        IEnumerable<ShowcaseSpec> chosen = targets.Count == 0
            ? mode == FilmMode.Shots
                ? roster.Showcases.Where(showcase => showcase.Shots.Count > 0)
                : roster.Showcases
            : [];

        if (targets.Count > 0)
        {
            var picked = new List<ShowcaseSpec>();
            foreach (Target target in targets)
            {
                ShowcaseSpec? showcase = roster.Showcases.FirstOrDefault(item => item.Id == target.Id);
                if (showcase == null)
                {
                    error = $"没有场面 {target.Id}；可用：{Names(roster.Showcases.Select(item => item.Id))}";
                    return [];
                }

                if (target.Shot == null)
                {
                    if (mode == FilmMode.Shots && showcase.Shots.Count == 0)
                    {
                        error = $"场面 {target.Id} 没有镜头";
                        return [];
                    }

                    picked.Add(showcase);
                    continue;
                }

                ShotSpec? shot = showcase.Shots.FirstOrDefault(item => item.Name == target.Shot);
                if (shot == null)
                {
                    error = $"场面 {target.Id} 没有镜头 {target.Shot}；可用：{Names(showcase.Shots.Select(item => item.Name))}";
                    return [];
                }

                WorkItem item = new(showcase.Id, shot, null);
                if (!items.Contains(item)) items.Add(item);
            }

            chosen = picked;
        }

        foreach (ShowcaseSpec showcase in chosen)
        {
            switch (mode)
            {
                case FilmMode.Test:
                    if (showcase.Leaves.Count == 0)
                        items.Add(new WorkItem(showcase.Id, null, null));
                    foreach (string leaf in showcase.Leaves)
                        items.Add(new WorkItem(showcase.Id, null, leaf));
                    break;
                case FilmMode.Paths:
                    foreach (PathSpec path in showcase.Paths)
                    {
                        WorkItem item = new(showcase.Id, null, null, path);
                        if (!items.Contains(item)) items.Add(item);
                    }

                    break;
                default:
                    foreach (ShotSpec shot in showcase.Shots)
                    {
                        WorkItem item = new(showcase.Id, shot, null);
                        if (!items.Contains(item)) items.Add(item);
                    }

                    break;
            }
        }

        if (items.Count == 0)
            error = mode == FilmMode.Shots ? "没有声明镜头的场面" : "名册里没有场面";
        return items;
    }

    public static List<WorkItem> ExpandRoll(List<WorkItem> items, int count, out string? error)
    {
        error = null;
        if (items is not [{ Shot: { } shot } item])
        {
            error = "--roll 只收一个 <场面Id>/<镜头名>";
            return [];
        }

        if (!shot.Clip)
        {
            error = $"{item.Label} 是静帧：镜头种子不改变静帧，--roll 只收片段";
            return [];
        }

        if (shot.Seed is not int current)
        {
            error = $"名册未给出 {item.Label} 的镜头种子";
            return [];
        }

        return [.. RollSeeds(current, count).Select((seed, index) => item with { Roll = index + 1, Seed = seed })];
    }

    private static string PathLabel(PathSpec path) => path.Leaf == null ? $"{path.Id} → 立场" : $"{path.Id} → {path.Leaf}";

    private static ShotSpec PathShot(PathSpec path, int fps) => new(path.Id, PathFileName(path), true,
        path.Path.Count == 0 ? [] : [Keys.FilmRoster.FormatMp4], path.Width, path.Height, null, fps);

    public static bool PathsFpsValid(int fps) => fps > 0 && 60 % fps == 0;

    internal static string PathFileName(PathSpec path) =>
        $"{path.Index:D2}-{FileNamePart(path.Leaf ?? "立场")}";

    internal static IReadOnlyList<string> StillNames(PathSpec path) =>
    [
        "00-立场.png",
        .. path.Path.Select((name, index) => $"{index + 1:D2}-{FileNamePart(name)}.png"),
    ];

    internal static string FileNamePart(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. text.Select(character => invalid.Contains(character) ? '_' : character)]);
    }

    private static string Names(IEnumerable<string> names)
    {
        List<string> list = [.. names];
        return list.Count == 0 ? "无" : string.Join("、", list);
    }

    internal static async Task<int> RunAsync(InstanceLink link, Options options, string filmRoot, Provenance provenance,
        IReadOnlyDictionary<string, string>? mvids, Action<string> log, CancellationToken token)
    {
        ActivityLog.Touch(ActivityLog.Film, link.Number,
            detail: options.Mode == FilmMode.Test ? "test" : options.Mode == FilmMode.Paths ? "paths" : null);
        (int status, string body) = await Get(link, RimRunner.RimRunnerPipeProtocol.Ops.FilmRoster, null, token);
        if (status != 200)
        {
            log($"[FAIL] 取拍摄名册失败：{Describe(status, body)}");
            return ExitCodes.Incomplete;
        }

        Roster roster = ParseRoster(body);
        if (roster.RegistryFaults.Count > 0)
        {
            log("[FAIL] 无法立场：场面名册有登记期错误");
            foreach (string fault in roster.RegistryFaults) log("  " + fault);
            return 1;
        }

        List<WorkItem> items = Resolve(roster, options.Targets, options.Mode, out string? resolveError);
        if (resolveError == null && options.Roll is int rollCount)
            items = ExpandRoll(items, rollCount, out resolveError);
        if (resolveError != null)
        {
            log("[FAIL] " + resolveError);
            return ExitCodes.Incomplete;
        }

        string runName = FilmRuns.DirName(provenance.StartedAt, provenance.RunId, options.Tag);
        string RunDir(string id) => Path.Combine(filmRoot, id, FilmRuns.RunsDirName, runName);
        List<string> scenes = [.. items.Select(item => item.Id).Distinct()];
        string destination = scenes.Count == 1
            ? RunDir(scenes[0])
            : Path.Combine(filmRoot, "<场面Id>", FilmRuns.RunsDirName, runName);
        log(options.Mode switch
        {
            FilmMode.Test => $"[film] --test：{scenes.Count} 个场面、{items.Count} 次立场",
            FilmMode.Paths => $"[film] --paths：{scenes.Count} 个场面、{items.Count} 条路径录像 → {destination}",
            _ when options.Roll != null => $"[film] --roll：{items[0].Id}/{items[0].Shot!.Name} 指定 {items.Count} 个镜头种子各拍一条 → "
                + destination,
            _ => $"[film] {items.Count} 个镜头 → {destination}",
        });
        if (options.Roll != null)
            FilmRuns.ClearLegacyRolls(Path.Combine(filmRoot, items[0].Id), items[0].Shot!.Name);
        string? stamp = options.Clean ? null : FilmRuns.Stamp(provenance.RunId, provenance.Source, options.Tag);
        int timeout = TimeoutPolicy.Resolve(options.TimeoutMinutes, log);
        string resultPath = Path.Combine(filmRoot, ResultFileName);
        var pathReset = new HashSet<string>(StringComparer.Ordinal);

        var encodes = new List<(WorkItem Item, JsonObject Result, Task<EncodeOutcome> Task)>();
        int red = 0;
        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        bool broken = false;
        foreach (WorkItem item in items)
        {
            if (item.Path != null) pathReset.Add(item.Id);

            ShotSpec shot = item.Path != null ? PathShot(item.Path, options.PathsFps)
                : item.Seed is int rollSeed ? item.Shot! with { Name = RollName(item.Shot!.Name, rollSeed) }
                : item.Shot!;
            string? audioDir = !options.Mute && item.Shot != null && shot.Clip
                               && shot.Formats.Contains(Keys.FilmRoster.FormatMp4, StringComparer.Ordinal)
                ? Path.Combine(RunDir(shot.Id), shot.Name + RimRunner.RimRunnerDataLayout.FilmAudioSuffix)
                : null;
            FilmEncoder? encoder = null;
            if (item.Shot != null || item.Path != null)
            {
                string outputDir = item.Path != null
                    ? Path.Combine(RunDir(item.Id), FilmRuns.PathsDirName)
                    : RunDir(shot.Id);
                FilmEncoder.Stills? stills = item.Path == null
                    ? null
                    : new FilmEncoder.Stills(Path.Combine(outputDir, PathFileName(item.Path)),
                        StillNames(item.Path));
                try
                {
                    encoder = FilmEncoder.Start(shot, outputDir, options.Ffmpeg!, options.KeepFrames,
                        link.Film.Acknowledge, stills, audioDir);
                }
                catch (Exception exception) when (exception is Win32Exception or IOException
                                                      or UnauthorizedAccessException)
                {
                    log($"[FAIL] {item.Label}：无法启动 ffmpeg：{exception.Message}");
                    broken = true;
                    break;
                }
            }

            string op = item.Path != null
                ? RimRunner.RimRunnerPipeProtocol.Ops.FilmPaths
                : item.Shot != null
                    ? RimRunner.RimRunnerPipeProtocol.Ops.FilmShot
                    : RimRunner.RimRunnerPipeProtocol.Ops.FilmStage;
            JsonObject? result;
            link.Film.Attach(encoder);
            try
            {
                result = await SendAndWait(link, op, writer =>
                {
                    writer.WriteString("id", item.Id);
                    if (item.Path != null)
                    {
                        if (item.Path.Leaf != null) writer.WriteString("leaf", item.Path.Leaf);
                        writer.WriteNumber("fps", options.PathsFps);
                    }
                    else if (item.Shot != null)
                    {
                        writer.WriteString("shot", item.Shot.Name);
                        if (item.Seed is int seed) writer.WriteNumber("seed", seed);
                        if (audioDir != null) writer.WriteString("audioDir", audioDir);
                    }
                    else if (item.Step != null) writer.WriteString("step", item.Step);

                    if (item.Path != null || item.Shot != null)
                    {
                        if (stamp != null) writer.WriteString("stamp", stamp);
                        else writer.WriteBoolean("clean", true);
                    }
                }, item.Label, timeout, log, token);
            }
            catch
            {
                encoder?.Abort();
                throw;
            }
            finally
            {
                link.Film.Attach(null);
            }

            if (result == null)
            {
                encoder?.Abort();
                broken = true;
                break;
            }

            bool ok = PrintResult(item, result, log, shown);
            if (!ok) red++;
            if (encoder == null) continue;

            int frames = result[Keys.Film.Frames]?.GetValue<int>() ?? 0;
            encodes.Add((item, result, encoder.FinishAsync(frames)));
        }

        if (options.Mode == FilmMode.Test)
        {
            if (broken) return ExitCodes.Incomplete;
            log(red == 0
                ? $"[结果] FILM TEST：{items.Count} 次立场均通过"
                : $"[结果] FILM TEST：{items.Count} 次立场里 {red} 次未通过");
            return red == 0 ? 0 : 1;
        }

        var records = new List<JsonObject>();
        int encodeRed = 0;
        int promoteRed = 0;
        var promoted = new List<string>();
        foreach ((WorkItem item, JsonObject result, Task<EncodeOutcome> task) in encodes)
        {
            EncodeOutcome outcome = await task;
            foreach (string line in outcome.Lines) log(line);
            if (!outcome.Ok) encodeRed++;
            records.Add(Record(item, result, outcome, mvids, provenance, options, runName));
            if (options.Roll != null || item.Shot == null || outcome.Outputs.Count == 0) continue;
            string sceneDir = Path.Combine(filmRoot, item.Id);
            string shotName = item.Shot.Name;
            if (Promote(item.Label, RunDir(item.Id), log,
                    () => FilmRuns.PromoteShot(sceneDir, shotName, outcome.Outputs.Select(output => output.Path))))
                promoted.Add(item.Id);
            else
                promoteRed++;
        }

        foreach (string id in pathReset)
        {
            string sceneDir = Path.Combine(filmRoot, id);
            if (Promote($"{id} 的路径录像", RunDir(id), log,
                    () => FilmRuns.PromotePaths(sceneDir, Path.Combine(RunDir(id), FilmRuns.PathsDirName))))
                promoted.Add(id);
            else
                promoteRed++;
        }

        foreach (string id in scenes.Where(id => Directory.Exists(RunDir(id))))
            WriteManifest(Path.Combine(RunDir(id), FilmRuns.ManifestFileName), provenance, options, mvids,
                [.. records.Where(record => Text(record, "id") == id)]);
        if (options.Roll != null)
            WriteRollsFile(resultPath, items[0].Id, items[0].Shot!.Name, records);
        else if (records.Count > 0 || pathReset.Count > 0)
            WriteResultFile(resultPath, records, pathReset);
        foreach (string id in promoted.Distinct())
            log($"[film] 最新副本 → {Path.Combine(filmRoot, id)}");
        Prune(filmRoot, scenes, runName, options.Quota ?? FilmRetention.Quota.Default, log);
        if (broken) return ExitCodes.Incomplete;
        int failed = red + encodeRed + promoteRed;
        string Breakdown(string unit) => string.Join("、", new[]
        {
            red > 0 ? $"{red} {unit}拍摄失败" : null,
            encodeRed > 0 ? $"{encodeRed} {unit}产物未通过" : null,
            promoteRed > 0 ? $"{promoteRed} 处最新副本未更新" : null,
        }.OfType<string>());
        if (options.Roll != null)
        {
            log(failed == 0
                ? $"[结果] FILM：{items.Count} 掷已产出"
                : $"[结果] FILM：{items.Count} 掷里 {Breakdown("掷")}");
            return failed == 0 ? 0 : 1;
        }

        log(failed == 0
            ? options.Mode == FilmMode.Paths
                ? $"[结果] FILM：{items.Count} 条路径录像已产出"
                : $"[结果] FILM：{items.Count} 个镜头已产出"
            : options.Mode == FilmMode.Paths
                ? $"[结果] FILM：{items.Count} 条路径录像里 {Breakdown("条")}"
                : $"[结果] FILM：{items.Count} 个镜头里 {Breakdown("个")}");
        return failed == 0 ? 0 : 1;
    }

    internal static async Task<string?> EndAsync(InstanceLink link, Options options, Action<string> log, CancellationToken token)
    {
        var failures = new List<string>();
        JsonObject? result = await SendAndWait(link, RimRunner.RimRunnerPipeProtocol.Ops.FilmEnd, _ => { }, "收尾",
            TimeoutPolicy.Resolve(options.TimeoutMinutes), line => failures.Add(line.StartsWith("[FAIL] ", StringComparison.Ordinal) ? line[7..] : line), token);
        if (result == null) return failures.Count > 0 ? string.Join("；", failures) : "收尾无结果";
        foreach (string line in Lines(result, Keys.Film.StartupErrors)) log("  启动错误级日志：" + line);
        foreach (string line in Lines(result, Keys.Film.LoadErrors)) log("  载档错误级日志：" + line);
        return Text(result, Keys.Film.Failure);
    }

    private static async Task<JsonObject?> SendAndWait(InstanceLink link, string op,
        Action<Utf8JsonWriter> parameters, string label, int timeout, Action<string> log, CancellationToken token)
    {
        (int status, string body) = await Get(link, op, parameters, token);
        if (status != 202)
        {
            log($"[FAIL] {label}：{Describe(status, body)}");
            return null;
        }

        int seq = (JsonNode.Parse(body) as JsonObject)?[Keys.Film.Seq]?.GetValue<int>()
                  ?? throw new JsonException("受理回包缺少 seq");
        Task<JsonObject> result = link.Film.Result(seq);
        WaitWatch? watch = WaitWatch.For(timeout, DateTime.UtcNow);
        try
        {
            while (true)
            {
                await Task.WhenAny(result, link.Film.Closed, Task.Delay(1000, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result.IsCompletedSuccessfully) return result.Result;
                if (link.Closed)
                {
                    log($"[FAIL] 拍摄实例管道断开：{await link.ExitReasonAsync().ConfigureAwait(false)}");
                    return null;
                }

                if (watch?.Observe(DateTime.UtcNow, watch.NextPulse(link), link.Film.Frames.ToString(),
                        $"{label}；实例 {ClaimStore.Named(link.Number)}") is string failure)
                {
                    log($"[FAIL] {failure}");
                    return null;
                }
            }
        }
        finally
        {
            link.Film.Forget(seq);
        }
    }

    internal static bool PrintResult(WorkItem item, JsonObject result, Action<string> log,
        Dictionary<string, string>? shown = null)
    {
        bool ok = result[Keys.Film.Ok]?.GetValue<bool>() ?? false;
        List<JsonObject> warnings = [.. Array(result, Keys.Film.Warnings).OfType<JsonObject>()];
        bool green = ok && (item.Shot != null || item.Path != null
                            || warnings.All(warning => Text(warning, Keys.SceneFinding.Kind) == Keys.SceneFinding.FilmNote));
        double seconds = (result[Keys.Film.ElapsedMs]?.GetValue<long>() ?? 0) / 1000.0;
        string head = (item.Shot != null || item.Path != null) && ok
            ? $"{result[Keys.Film.Frames]} 帧 · {result[Keys.Film.Ticks]} tick · "
              + $"{result[Keys.Film.Width]}×{result[Keys.Film.Height]} · 场面种子 {result[Keys.Film.SceneSeed]} · 镜头种子 {result[Keys.Film.Seed]}"
            : green ? "立场完成，无中止" : "未通过";
        log($"[{(green ? "PASS" : "FAIL")}] {item.Label}：{head} · {seconds:0.0}s"
            + (result[Keys.Film.Reloaded]?.GetValue<bool>() == true ? " · 回载" : ""));
        if (Text(result, Keys.Film.Failure) is { } failure) log("  拍摄失败：" + failure);
        if (Text(result, Keys.Film.TimeUnpinned) is { } unpinned) log("  时间未钉住：" + unpinned);
        if (Text(result, Keys.Film.AbortReason) is { } abort) PrintOnce("立场中止", [abort], whole: true);
        PrintOnce("步骤中止", Lines(result, Keys.Film.StepAborts), whole: true);
        PrintOnce("立场错误级日志", Lines(result, Keys.Film.StagingErrors), whole: false);
        PrintOnce("拍摄期错误级日志", Lines(result, Keys.Film.Errors), whole: false);
        PrintOnce("启动错误级日志", Lines(result, Keys.Film.StartupErrors), whole: false);
        PrintOnce("载档错误级日志", Lines(result, Keys.Film.LoadErrors), whole: false);
        foreach (JsonObject warning in warnings)
        {
            string? label = Text(warning, Keys.Film.WarningLabel);
            string tag = Keys.SceneFinding.Tag(Text(warning, Keys.SceneFinding.Kind));
            string handle = Text(warning, Keys.SceneFinding.Kind) == Keys.SceneFinding.Assert
                && warning[Keys.SceneFinding.Ordinal] is JsonValue ordinal && ordinal.TryGetValue(out int number)
                && FindingPresentation.AssertHandle(number) is string name ? name + " " : "";
            log($"  {tag}：{handle}{label}：{Text(warning, Keys.Film.WarningDetail)}");
            List<string> screen = [];
            List<string> logOnly = [];
            FindingPresentation.Items([.. Lines(warning, Keys.Film.WarningOffending)], FindingPresentation.OffendingPerAssert,
                "    ", "项", $"[{tag}违例] {item.Label} {label}：", screen, logOnly);
            screen.ForEach(log);
            logOnly.ForEach(CliLog.Info);
        }
        PrintOnce("消息", Lines(result, Keys.Film.Messages), whole: true);
        return green;

        void PrintOnce(string category, IEnumerable<string> lines, bool whole)
        {
            var repeated = new List<string>();
            foreach (string line in lines)
            {
                string key = category + "\n" + (whole ? line : line.Split('\n')[0].TrimEnd('\r'));
                if (shown != null && shown.TryGetValue(key, out string? first))
                {
                    repeated.Add(first);
                    if (!whole) CliLog.Info($"[{category}] {item.Label}：{line}");
                    continue;
                }
                shown?.Add(key, item.Label);
                log($"  {category}：{line}");
            }
            foreach (IGrouping<string, string> group in repeated.GroupBy(first => first, StringComparer.Ordinal))
                log($"  {category}：{group.Count()} 条与 {group.Key} 的相同");
        }
    }

    internal sealed record EncodeOutcome(bool Ok, IReadOnlyList<string> Lines, IReadOnlyList<(string Path, long Bytes)> Outputs,
        FilmAudio? Audio = null);

    internal sealed record FilmAudio(string? Silent, int Voices, int Clips, IReadOnlyList<string> Unreadable,
        IReadOnlyList<string> UnknownRows);

    internal static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:0.0} MiB"
        : $"{bytes / 1024.0:0} KiB";

    private static bool Promote(string label, string runDir, Action<string> log, Action promote)
    {
        try
        {
            promote();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"[FAIL] {label}：最新副本未更新：{exception.Message}；本次产物在 {runDir}");
            return false;
        }
    }

    private static void Prune(string filmRoot, IEnumerable<string> scenes, string current, FilmRetention.Quota quota,
        Action<string> log)
    {
        foreach (string id in scenes)
        {
            string runsDir = Path.Combine(filmRoot, id, FilmRuns.RunsDirName);
            FilmRetention.Outcome outcome = FilmRetention.Prune(runsDir, quota, current);
            if (outcome.RunsDeleted > 0 || outcome.FramesDeleted > 0)
                log($"[film] {runsDir}：删去 {outcome.RunsDeleted} 份旧拍摄目录、{outcome.FramesDeleted} 份逐帧目录（{quota.Describe()}）");
            foreach (string failure in outcome.Failures) log($"[WARN] 未能删除 {failure}");
        }
    }

    private static JsonObject Record(WorkItem item, JsonObject result, EncodeOutcome outcome,
        IReadOnlyDictionary<string, string>? mvids, Provenance provenance, Options options, string runName)
    {
        var record = new JsonObject { ["id"] = item.Id };
        if (item.Path is { } path)
        {
            record["leaf"] = path.Leaf ?? "";
            record["file"] = PathFileName(path);
            record["path"] = new JsonArray([.. path.Path.Select(name => (JsonNode)name)]);
            record["speeds"] = new JsonArray([.. path.Speeds.Select(speed => (JsonNode)speed)]);
            record["dwells"] = new JsonArray([.. path.Dwells.Select(dwell => (JsonNode)dwell)]);
            record["steps"] = result[Keys.Film.Steps]?.DeepClone() ?? new JsonArray();
            record["stills"] = new JsonArray([.. StillNames(path).Select(name => (JsonNode)name)]);
        }
        else
        {
            record["shot"] = item.Shot!.Name;
            if (item.Roll is int roll) record["roll"] = roll;
        }

        record["ok"] = (result[Keys.Film.Ok]?.GetValue<bool>() ?? false) && outcome.Ok;
        record["frames"] = result[Keys.Film.Frames]?.GetValue<int>();
        record["ticks"] = result[Keys.Film.Ticks]?.GetValue<int>();
        record["width"] = result[Keys.Film.Width]?.GetValue<int>();
        record["height"] = result[Keys.Film.Height]?.GetValue<int>();
        record["sceneSeed"] = result[Keys.Film.SceneSeed]?.GetValue<int>();
        record["seed"] = result[Keys.Film.Seed]?.GetValue<int>();
        record["filmedAt"] = DateTimeOffset.Now.ToString("O");
        record["outputs"] = new JsonArray([
            .. outcome.Outputs.Select(output => (JsonNode)new JsonObject
            {
                ["file"] = Path.GetFileName(output.Path),
                ["bytes"] = output.Bytes,
            }),
        ]);
        if (Text(result, Keys.Film.TimeUnpinned) is { } unpinned)
        {
            record["timeUnpinned"] = unpinned;
        }

        if (outcome.Audio is { } audio)
        {
            var track = new JsonObject { ["voices"] = audio.Voices, ["clips"] = audio.Clips };
            if (audio.Silent != null) track["silent"] = audio.Silent;
            if (audio.Unreadable.Count > 0)
                track["unreadable"] = new JsonArray([.. audio.Unreadable.Select(line => (JsonNode)line)]);
            if (audio.UnknownRows.Count > 0)
                track["unknownRows"] = new JsonArray([.. audio.UnknownRows.Select(name => (JsonNode)name)]);
            record["audio"] = track;
        }

        if (mvids != null) record["assemblyMvids"] = MvidMap(mvids);
        AppendProvenance(record, provenance, options);
        record["runDir"] = $"{item.Id}/{FilmRuns.RunsDirName}/{runName}";
        return record;
    }

    private static JsonObject MvidMap(IReadOnlyDictionary<string, string> mvids)
    {
        var map = new JsonObject();
        foreach ((string assembly, string mvid) in mvids.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            map[assembly] = mvid;
        return map;
    }

    private static void AppendProvenance(JsonObject json, Provenance provenance, Options options)
    {
        json["runId"] = provenance.RunId;
        if (options.Tag != null) json["tag"] = options.Tag;
        if (options.Clean) json["clean"] = true;
        if (provenance.Source is { } source)
        {
            json["source"] = source.Error != null
                ? new JsonObject { ["error"] = source.Error }
                : new JsonObject
                {
                    ["commit"] = source.Commit, ["worktree"] = source.Worktree,
                    ["changes"] = new JsonArray([.. source.Changes.Select(change => JsonValue.Create(change))]),
                    ["changeCount"] = source.ChangeCount,
                };
        }
    }

    private static void WriteManifest(string path, Provenance provenance, Options options,
        IReadOnlyDictionary<string, string>? mvids, IReadOnlyList<JsonObject> records)
    {
        var manifest = new JsonObject();
        AppendProvenance(manifest, provenance, options);
        manifest["filmedAt"] = provenance.StartedAt.ToString("O");
        if (provenance.ModList != null) manifest["modList"] = provenance.ModList;
        manifest["mode"] = options.Roll != null ? "roll" : options.Mode == FilmMode.Paths ? "paths" : "shots";
        if (options.Mode == FilmMode.Paths) manifest["fps"] = options.PathsFps;
        if (mvids != null) manifest["assemblyMvids"] = MvidMap(mvids);
        manifest["records"] = new JsonArray([.. records.Select(record => (JsonNode)record.DeepClone())]);
        SaveJson(path, manifest);
    }

    internal static void WriteResultFile(string path, IReadOnlyList<JsonObject> records,
        IReadOnlyCollection<string> pathResetIds)
    {
        (List<JsonObject> merged, List<JsonObject> rolls) = ReadResultFile(path);
        merged.RemoveAll(old => old["leaf"] != null
                                && Text(old, "id") is { } id && pathResetIds.Contains(id));
        foreach (JsonObject record in records)
        {
            merged.RemoveAll(old => Text(old, "id") == Text(record, "id")
                                    && Text(old, "shot") == Text(record, "shot")
                                    && Text(old, "leaf") == Text(record, "leaf"));
            merged.Add(record);
        }

        merged.Sort((a, b) =>
        {
            int id = string.CompareOrdinal(Text(a, "id"), Text(b, "id"));
            if (id != 0) return id;
            int shot = string.CompareOrdinal(Text(a, "shot") ?? "", Text(b, "shot") ?? "");
            return shot != 0 ? shot : string.CompareOrdinal(Text(a, "leaf") ?? "", Text(b, "leaf") ?? "");
        });
        SaveResultFile(path, merged, rolls);
    }

    internal static void WriteRollsFile(string path, string id, string shot, IReadOnlyList<JsonObject> records)
    {
        (List<JsonObject> shots, List<JsonObject> rolls) = ReadResultFile(path);
        rolls.RemoveAll(old => Text(old, "id") == id && Text(old, "shot") == shot);
        rolls.AddRange(records);
        rolls.Sort((a, b) =>
        {
            int byId = string.CompareOrdinal(Text(a, "id"), Text(b, "id"));
            if (byId != 0) return byId;
            int byShot = string.CompareOrdinal(Text(a, "shot") ?? "", Text(b, "shot") ?? "");
            return byShot != 0 ? byShot : (a["roll"]?.GetValue<int>() ?? 0).CompareTo(b["roll"]?.GetValue<int>() ?? 0);
        });
        SaveResultFile(path, shots, rolls);
    }

    private static (List<JsonObject> Shots, List<JsonObject> Rolls) ReadResultFile(string path)
    {
        var shots = new List<JsonObject>();
        var rolls = new List<JsonObject>();
        if (!File.Exists(path)) return (shots, rolls);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing)
            {
                if (existing["shots"] is JsonArray shotArray)
                    shots.AddRange(shotArray.OfType<JsonObject>().Select(item => (JsonObject)item.DeepClone()));
                if (existing["rolls"] is JsonArray rollArray)
                    rolls.AddRange(rollArray.OfType<JsonObject>().Select(item => (JsonObject)item.DeepClone()));
            }
        }
        catch (JsonException)
        {
        }

        return (shots, rolls);
    }

    private static void SaveResultFile(string path, List<JsonObject> shots, List<JsonObject> rolls)
    {
        var root = new JsonObject { ["shots"] = new JsonArray([.. shots.Cast<JsonNode>()]) };
        if (rolls.Count > 0) root["rolls"] = new JsonArray([.. rolls.Cast<JsonNode>()]);
        SaveJson(path, root);
    }

    private static void SaveJson(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        File.Move(temp, path, overwrite: true);
    }

    private static async Task<(int Status, string Body)> Get(InstanceLink link, string op,
        Action<Utf8JsonWriter>? parameters, CancellationToken token)
    {
        InstanceLink.Reply reply = await link.RequestAsync(op, parameters, RequestTimeout, token);
        return reply.Status <= InstanceLink.Reply.NoLinkStatus
            ? (0, reply.ErrorText())
            : (reply.Status, reply.Body ?? "");
    }

    private static string Describe(int status, string body)
    {
        if (status == 0) return "无应答（" + body + "）";
        try
        {
            if (JsonNode.Parse(body) is JsonObject json && Text(json, Keys.Error.Message) is { } message)
                return $"状态码 {status} {message}";
        }
        catch (JsonException)
        {
        }

        return $"状态码 {status} {body}";
    }

    private static IEnumerable<JsonNode?> Array(JsonObject json, string key) =>
        json[key] as JsonArray ?? [];

    private static IEnumerable<string> Lines(JsonObject json, string key) =>
        Array(json, key).Select(item => item?.GetValue<string>() ?? "");

    private static string? Text(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static int Int(JsonObject json, string key) => json[key]?.GetValue<int>() ?? 0;
}
