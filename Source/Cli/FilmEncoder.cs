using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimRunner;
using Keys = RimRunner.RimRunnerLiveContract;

namespace RimRunnerCli;

internal sealed class FilmEncoder
{
    private const string PartSuffix = ".part";
    private static readonly TimeSpan StillLimit = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan EncodeLimit = TimeSpan.FromMinutes(10);

    private readonly FilmFlow.ShotSpec shot;
    private readonly Process? process;
    private readonly Stream? stdin;
    private readonly Task<string>? stderr;
    private readonly string ffmpeg;
    private readonly IReadOnlyList<(string Target, string Part)> outputs;
    private readonly string? framesDir;
    private readonly string? audioDir;
    private readonly Stills? stills;
    private readonly Action<string?> acknowledge;
    private readonly BlockingCollection<Write> queue = [];
    private readonly Task feeding;
    private readonly int frameBytes;
    private int fed;
    private volatile string? fault;
    private bool saturated;
    private volatile Process? stillProcess;

    internal sealed record Stills(string Dir, IReadOnlyList<string> Names);

    private readonly record struct Write(byte[] Rgba, int Still);

    private FilmEncoder(FilmFlow.ShotSpec shot, string ffmpeg, Process? process, IReadOnlyList<(string, string)> outputs,
        string? framesDir, string? audioDir, Stills? stills, Action<string?> acknowledge)
    {
        this.shot = shot;
        this.audioDir = audioDir;
        this.ffmpeg = ffmpeg;
        this.process = process;
        this.outputs = outputs;
        this.framesDir = framesDir;
        this.stills = stills;
        this.acknowledge = acknowledge;
        frameBytes = shot.Width * shot.Height * 4;
        stdin = process?.StandardInput.BaseStream;
        stderr = process?.StandardError.ReadToEndAsync();
        if (process != null) _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        feeding = Task.Factory.StartNew(Feed, TaskCreationOptions.LongRunning);
    }

    private string Label => $"{shot.Id}/{shot.Name}";

    internal static FilmEncoder Start(FilmFlow.ShotSpec shot, string outputDir, string ffmpeg, bool keepFrames,
        Action<string?> acknowledge, Stills? stills = null, string? audioDir = null)
    {
        Directory.CreateDirectory(outputDir);
        if (audioDir != null && Directory.Exists(audioDir)) Directory.Delete(audioDir, recursive: true);
        if (stills != null)
        {
            string part = stills.Dir + PartSuffix;
            if (Directory.Exists(part)) Directory.Delete(part, recursive: true);
            Directory.CreateDirectory(part);
        }

        var outputs = new List<(string Format, string Target, string Part)>();
        foreach (string format in shot.Formats)
        {
            string target = Path.Combine(outputDir, shot.Name + "." + format);
            outputs.Add((format, target, target + PartSuffix));
        }

        string? framesDir = null;
        if (keepFrames && shot.Formats.Count > 0)
        {
            framesDir = Path.Combine(outputDir, shot.Name + RimRunnerDataLayout.FilmFramesSuffix);
            if (Directory.Exists(framesDir)) Directory.Delete(framesDir, recursive: true);
            Directory.CreateDirectory(framesDir);
        }

        Process? process = null;
        if (shot.Formats.Count > 0)
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in Arguments(shot, [.. outputs.Select(output => (output.Format, output.Part))],
                         framesDir == null ? null : Path.Combine(framesDir, "%05d.png")))
            {
                start.ArgumentList.Add(argument);
            }

            CommandContext.Prepare(start, Path.GetDirectoryName(ffmpeg)!);
            process = Process.Start(start) ?? throw new Win32Exception($"无法启动 {ffmpeg}");
        }

        return new FilmEncoder(shot, ffmpeg, process, [.. outputs.Select(output => (output.Target, output.Part))], framesDir,
            audioDir, stills, acknowledge);
    }

    internal static List<string> Arguments(FilmFlow.ShotSpec shot, IReadOnlyList<(string Format, string Path)> outputs,
        string? framesPattern)
    {
        int branches = outputs.Count + (framesPattern != null ? 1 : 0);
        var graph = new List<string>
        {
            "[0:v]format=rgb24" + (branches == 1
                ? "[s0]"
                : $",split={branches}" + string.Concat(Enumerable.Range(0, branches).Select(index => $"[s{index}]"))),
        };
        var mapped = new List<string>();
        for (int index = 0; index < outputs.Count; index++)
        {
            (string format, string path) = outputs[index];
            switch (format)
            {
                case Keys.FilmRoster.FormatPng:
                    mapped.AddRange(["-map", $"[s{index}]", "-frames:v", "1", "-c:v", "png", "-f", "image2", "-update", "1", path]);
                    break;
                case Keys.FilmRoster.FormatGif:
                    graph.Add($"[s{index}]split[ga{index}][gb{index}];[ga{index}]palettegen=stats_mode=diff[gp{index}];"
                              + $"[gb{index}][gp{index}]paletteuse=dither=sierra2_4a[o{index}]");
                    mapped.AddRange(["-map", $"[o{index}]", "-f", "gif", path]);
                    break;
                case Keys.FilmRoster.FormatMp4:
                    mapped.AddRange(["-map", $"[s{index}]", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18",
                        "-preset", "slow", "-movflags", "+faststart", "-f", "mp4", path]);
                    break;
                default:
                    throw new ArgumentException($"引擎报出无法识别的格式 {format}");
            }
        }

        if (framesPattern != null)
        {
            mapped.AddRange(["-map", $"[s{outputs.Count}]", "-c:v", "png", "-start_number", "0", "-f", "image2",
                framesPattern]);
        }

        return
        [
            "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgba", "-video_size", $"{shot.Width}x{shot.Height}",
            "-framerate", Math.Max(shot.Fps, 1).ToString(), "-i", "pipe:0", "-filter_complex", string.Join(";", graph), .. mapped,
        ];
    }

    internal static List<string> StillArguments(FilmFlow.ShotSpec shot, string path) =>
    [
        "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgba", "-video_size", $"{shot.Width}x{shot.Height}",
        "-i", "pipe:0", "-frames:v", "1", path,
    ];

    internal void Enqueue(ReadOnlyMemory<byte> rgba)
    {
        try
        {
            queue.Add(new Write(rgba.ToArray(), -1));
        }
        catch (InvalidOperationException)
        {
            acknowledge(Label + " 的编码器已停止接收帧");
        }
    }

    internal void WriteStill(int index, ReadOnlyMemory<byte> rgba)
    {
        if (stills == null) return;
        try
        {
            queue.Add(new Write(rgba.ToArray(), index));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Feed()
    {
        foreach (Write write in queue.GetConsumingEnumerable())
        {
            if (write.Still >= 0)
            {
                WriteStillFile(write);
                continue;
            }

            string? error = fault;
            if (error == null && write.Rgba.Length != frameBytes)
            {
                error = fault = $"第 {fed} 帧 {write.Rgba.Length} 字节，不是 {shot.Width}×{shot.Height} 的 RGBA";
            }

            if (error == null && !saturated && stdin != null)
            {
                try
                {
                    stdin.Write(write.Rgba);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    if (process!.WaitForExit(5000) && process.ExitCode == 0)
                        saturated = true;
                    else
                        error = fault = "ffmpeg 接收帧失败：" + ExitDetail(exception.Message);
                }
            }

            if (error == null) fed++;
            acknowledge(error);
        }
    }

    private void WriteStillFile(Write write)
    {
        if (fault != null) return;
        if (write.Still >= stills!.Names.Count)
        {
            fault = $"静帧标记 {write.Still} 超出路径录像的 {stills.Names.Count} 张";
            return;
        }

        string path = Path.Combine(stills.Dir + PartSuffix, $"{write.Still:D3}.png");
        try
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in StillArguments(shot, path)) start.ArgumentList.Add(argument);
            CommandContext.Prepare(start, Path.GetDirectoryName(ffmpeg)!);
            using Process oneShot = Process.Start(start) ?? throw new Win32Exception($"无法启动 {ffmpeg}");
            stillProcess = oneShot;
            _ = oneShot.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            Task<string> stillLog = oneShot.StandardError.ReadToEndAsync();
            oneShot.StandardInput.BaseStream.Write(write.Rgba);
            oneShot.StandardInput.BaseStream.Close();
            if (ChildProcess.WaitOrStop(oneShot, StillLimit, $"静帧 {write.Still} 的 ffmpeg") is string timedOut)
            {
                fault = timedOut;
                return;
            }

            string log = stillLog.GetAwaiter().GetResult();
            if (oneShot.ExitCode != 0)
                fault = $"静帧 {write.Still} 写入失败：ffmpeg 退出码 {oneShot.ExitCode}" + StderrTail(log);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            fault = $"静帧 {write.Still} 写入失败：{exception.Message}";
        }
        finally
        {
            stillProcess = null;
        }
    }

    internal async Task<FilmFlow.EncodeOutcome> FinishAsync(int expectedFrames)
    {
        queue.CompleteAdding();
        await feeding.ConfigureAwait(false);
        if (fault == null && fed != expectedFrames) fault = $"送入 {fed} 帧，镜头是 {expectedFrames} 帧";
        if (fault == null && process != null && expectedFrames == 0) fault = "拍摄结果为 0 帧";
        if (fault != null)
        {
            Discard();
            return Failed(fault);
        }

        if (process != null && stdin != null)
        {
            try
            {
                stdin.Close();
            }
            catch (IOException)
            {
            }

            if (await ChildProcess.WaitOrStopAsync(process, EncodeLimit, $"{Label} 的 ffmpeg 收尾编码").ConfigureAwait(false)
                is string timedOut)
            {
                DeleteParts();
                return Failed(timedOut);
            }

            string log = await stderr!.ConfigureAwait(false);
            int exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                DeleteParts();
                return Failed($"ffmpeg 退出码 {exitCode}" + StderrTail(log));
            }
        }

        process?.Dispose();
        var lines = new List<string>();
        var produced = new List<(string, long)>();
        bool ok = true;
        FilmFlow.FilmAudio? audio = null;
        foreach ((string target, string part) in outputs)
        {
            if (audioDir != null && target.EndsWith("." + Keys.FilmRoster.FormatMp4, StringComparison.Ordinal))
                audio = await AddAudioAsync(part, expectedFrames, lines).ConfigureAwait(false);
            File.Move(part, target, overwrite: true);
            long bytes = new FileInfo(target).Length;
            produced.Add((target, bytes));
            if (shot.MaxBytes is long max && bytes > max)
            {
                ok = false;
                lines.Add($"[FAIL] {target}：{FilmFlow.FormatBytes(bytes)}，超出声明上限 {FilmFlow.FormatBytes(max)}");
                continue;
            }

            lines.Add($"[film] {target}（{FilmFlow.FormatBytes(bytes)}）");
        }

        if (framesDir != null) lines.Add($"[film] 帧序列在 {framesDir}");
        if (audioDir != null)
        {
            if (framesDir != null) lines.Add($"[film] 音轨原料在 {audioDir}");
            else DeleteAudioDir();
        }

        if (stills != null)
        {
            string part = stills.Dir + PartSuffix;
            string[] files = Directory.Exists(part)
                ? [.. Directory.GetFiles(part, "*.png").OrderBy(Path.GetFileName, StringComparer.Ordinal)]
                : [];
            bool complete = files.Length == stills.Names.Count;
            for (int index = 0; complete && index < stills.Names.Count; index++)
                complete = File.Exists(Path.Combine(part, $"{index:D3}.png"));
            if (!complete)
            {
                ok = false;
                lines.Add($"[FAIL] {stills.Dir}：静帧 {files.Length} 张，路径录像须 {stills.Names.Count} 张");
                DeleteStillsPart();
            }
            else
            {
                for (int index = 0; index < stills.Names.Count; index++)
                {
                    File.Move(Path.Combine(part, $"{index:D3}.png"), Path.Combine(part, stills.Names[index]));
                }

                if (Directory.Exists(stills.Dir)) Directory.Delete(stills.Dir, recursive: true);
                Directory.Move(part, stills.Dir);
                foreach (string name in stills.Names)
                {
                    string path = Path.Combine(stills.Dir, name);
                    produced.Add((path, new FileInfo(path).Length));
                }

                lines.Add($"[film] 静帧 {stills.Names.Count} 张 → {stills.Dir}");
            }
        }

        return new FilmFlow.EncodeOutcome(ok, lines, produced, audio);
    }

    private const string AudioWavFileName = "audio.wav";

    private async Task<FilmFlow.FilmAudio> AddAudioAsync(string part, int frames, List<string> lines)
    {
        string wav = Path.Combine(audioDir!, AudioWavFileName);
        FilmMixer.Outcome mixed;
        try
        {
            mixed = await Task.Run(() => FilmMixer.MixToWav(audioDir!, frames, shot.Fps, wav)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                              or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            mixed = new FilmMixer.Outcome("无法读取事件表：" + exception.Message, 0, 0, [], []);
        }

        foreach (string clip in mixed.Unreadable) lines.Add($"[WARN] {Label} 音轨缺一个 clip：{clip}");
        if (mixed.UnknownRows.Count > 0) lines.Add($"[WARN] {Label} 音轨未重放事件表里的这些行：{string.Join("、", mixed.UnknownRows)}");
        if (mixed.Skipped != null) return Silent(mixed, mixed.Skipped, lines);

        string muxed = part + ".audio";
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in MuxArguments(part, wav, muxed)) start.ArgumentList.Add(argument);
        CommandContext.Prepare(start, Path.GetDirectoryName(ffmpeg)!);
        try
        {
            using Process mux = Process.Start(start) ?? throw new Win32Exception($"无法启动 {ffmpeg}");
            _ = mux.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            Task<string> muxLog = mux.StandardError.ReadToEndAsync();
            if (await ChildProcess.WaitOrStopAsync(mux, EncodeLimit, $"{Label} 的 ffmpeg 音轨合成").ConfigureAwait(false)
                is string timedOut)
            {
                File.Delete(muxed);
                return Silent(mixed, timedOut, lines);
            }

            string log = await muxLog.ConfigureAwait(false);
            if (mux.ExitCode != 0)
            {
                File.Delete(muxed);
                return Silent(mixed, $"ffmpeg 合成退出码 {mux.ExitCode}" + StderrTail(log), lines);
            }

            File.Move(muxed, part, overwrite: true);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return Silent(mixed, "合成失败：" + exception.Message, lines);
        }

        lines.Add($"[film] {Label} 音轨：{mixed.Voices} 次播放 · {mixed.Clips} 个 clip");
        return new FilmFlow.FilmAudio(null, mixed.Voices, mixed.Clips, mixed.Unreadable, mixed.UnknownRows);
    }

    private FilmFlow.FilmAudio Silent(FilmMixer.Outcome mixed, string reason, List<string> lines)
    {
        lines.Add($"[WARN] {Label} mp4 不带音轨：{reason}");
        return new FilmFlow.FilmAudio(reason, mixed.Voices, mixed.Clips, mixed.Unreadable, mixed.UnknownRows);
    }

    internal static List<string> MuxArguments(string video, string wav, string output) =>
    [
        "-y", "-v", "error", "-i", video, "-i", wav, "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy",
        "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-f", "mp4", output,
    ];

    private void DeleteAudioDir()
    {
        try
        {
            if (Directory.Exists(audioDir)) Directory.Delete(audioDir, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CliLog.Info($"[film] 未能删除音轨原料 {audioDir}：{exception.Message}");
        }
    }

    private string ExitDetail(string writeFailure)
    {
        if (process == null || !process.HasExited) return writeFailure;
        string log = stderr!.Wait(TimeSpan.FromSeconds(5)) ? stderr.Result : "";
        return $"ffmpeg 退出码 {process.ExitCode}" + StderrTail(log);
    }

    private static string StderrTail(string log)
    {
        string[] tail = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tail.Length > 0 ? "：" + string.Join(" / ", tail.TakeLast(3)) : "";
    }

    internal void Abort()
    {
        queue.CompleteAdding();
        Discard();
    }

    private void Discard()
    {
        var running = stillProcess;
        if (running != null)
        {
            try
            {
                running.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
            }
        }

        if (process != null)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
            }
        }

        DeleteParts();
    }

    private void DeleteParts()
    {
        foreach ((_, string part) in outputs)
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CliLog.Info($"[film] 未能删除未完成的产物 {part}：{exception.Message}");
            }
        }

        DeleteStillsPart();
    }

    private void DeleteStillsPart()
    {
        if (stills == null) return;
        string part = stills.Dir + PartSuffix;
        try
        {
            if (Directory.Exists(part)) Directory.Delete(part, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CliLog.Info($"[film] 未能删除未完成的静帧目录 {part}：{exception.Message}");
        }
    }

    private FilmFlow.EncodeOutcome Failed(string reason) =>
        new(false, [$"[FAIL] {Label} 编码失败：{reason}" + (framesDir != null ? $"；帧序列在 {framesDir}" : "")], []);
}

internal sealed class FilmReceiver(Action<string?> acknowledge)
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> results = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FilmEncoder? encoder;
    private long frames;

    internal long Frames => Interlocked.Read(ref frames);

    internal Task Closed => closed.Task;

    internal void Acknowledge(string? error) => acknowledge(error);

    internal void Attach(FilmEncoder? next) => Volatile.Write(ref encoder, next);

    internal void AcceptFrame(ReadOnlyMemory<byte> rgba, JsonElement root)
    {
        Interlocked.Increment(ref frames);
        if (Volatile.Read(ref encoder) is FilmEncoder current)
        {
            current.Enqueue(rgba);
            if (root.TryGetProperty(RimRunnerPipeProtocol.Keys.Still, out JsonElement still)
                && still.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement mark in still.EnumerateArray())
                    current.WriteStill(mark.GetInt32(), rgba);
            }
        }
        else
        {
            acknowledge("编排进程此刻没有正在接收帧的编码器");
        }
    }

    internal void AcceptResult(JsonElement root)
    {
        if (root.TryGetProperty(RimRunnerPipeProtocol.Keys.Result, out JsonElement result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty(Keys.Film.Seq, out JsonElement seq) && seq.TryGetInt32(out int value)
            && JsonNode.Parse(result.GetRawText()) is JsonObject parsed)
        {
            Slot(value).TrySetResult(parsed);
            return;
        }

        CliLog.Error("[film] 拍摄实例发来的 film.result 缺少 result.seq，未采用：" + root.GetRawText());
    }

    internal Task<JsonObject> Result(int seq) => Slot(seq).Task;

    private TaskCompletionSource<JsonObject> Slot(int seq) =>
        results.GetOrAdd(seq, _ => new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously));

    internal void Forget(int seq) => results.TryRemove(seq, out _);

    internal void Close() => closed.TrySetResult();
}
