using System.Diagnostics;

namespace RimRunnerCli;

public sealed record SkewNote(IReadOnlyList<string> Lines);

public static class SkewDiagnosis
{
    public static SkewNote? Describe(
        EngineReport report,
        string modSettingsBase,
        IReadOnlyCollection<string> ownMarkers,
        Func<(int? Behind, string? Note)> readBehind)
    {
        if (modSettingsBase != InstanceConfig.ModSettingsBasePlayer || !ShapeMatches(report))
        {
            return null;
        }

        IReadOnlyList<string> texts = StartupTexts(report);
        string? ours = texts.FirstOrDefault(text => MentionsOurs(text, ownMarkers));
        if (ours != null)
        {
            return null;
        }

        (int? behind, string? note) = readBehind();
        if (behind == null)
        {
            return new SkewNote([
                "[批次拒绝] 启动错误级日志正文里没有本产品或引擎的程序集名；"
                + "本 worktree 相对主仓检出的落后提交数未能算出：" + (note ?? "原因不明")
            ]);
        }

        if (behind.Value <= 0)
        {
            return null;
        }

        return new SkewNote([
            "[批次拒绝] 启动错误级日志正文里没有本产品或引擎的程序集名；"
            + "本 worktree 落后主仓检出 " + behind.Value + " 个提交"
        ]);
    }

    private static bool ShapeMatches(EngineReport report)
    {
        IReadOnlyList<EngineReportRejection> rejections = report.Rejections;
        return rejections.Count > 0
            && rejections.All(r => r.Gate == RimRunner.RimRunnerRejectionGate.Startup);
    }

    private static IReadOnlyList<string> StartupTexts(EngineReport report) =>
        [.. report.Rejections.Select(r => r.Text)];

    private static bool MentionsOurs(string text, IReadOnlyCollection<string> ownMarkers) =>
        ownMarkers.Concat(["RimRunner"]).Any(marker =>
            marker.Length > 0
            && text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static readonly TimeSpan GitLimit = TimeSpan.FromMinutes(2);

    public static (int? Behind, string? Note) ReadBehindMainWorktree(string productRoot)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = productRoot,
            };
            start.ArgumentList.Add("rev-list");
            start.ArgumentList.Add("--count");
            start.ArgumentList.Add("HEAD..main-worktree/HEAD");
            CommandContext.Prepare(start, productRoot);

            using Process? process = Process.Start(start);
            if (process == null) return (null, "git 进程无法启动");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (ChildProcess.WaitOrStop(process, GitLimit, "git rev-list") is string timedOut)
                return (null, timedOut);
            string output = stdout.GetAwaiter().GetResult().Trim();
            string error = stderr.GetAwaiter().GetResult().Trim();

            if (process.ExitCode != 0)
                return (null, error.Length > 0 ? error : $"git 返回退出码 {process.ExitCode}");
            if (!int.TryParse(output, out int behind))
                return (null, $"git 输出无法解析为整数：\"{output}\"");
            return (behind, null);
        }
        catch (Exception exception)
        {
            return (null, exception.Message);
        }
    }
}
