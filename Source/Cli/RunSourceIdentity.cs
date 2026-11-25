using System.Diagnostics;
using System.Text.Json;

namespace RimRunnerCli;

internal sealed record RunSourceIdentity(string? Commit, string? Worktree, IReadOnlyList<string> Changes, int ChangeCount, string? Error)
{
    public const string MainWorktreeName = "main";

    public const int MaxChanges = 200;

    public static RunSourceIdentity Capture(string productRoot)
    {
        (string? revParse, string? error) = RunGit(productRoot,
            "rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir", "--show-toplevel", "HEAD");
        if (revParse == null) return Unavailable("git rev-parse 失败：" + error);
        string[] lines = revParse.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 4) return Unavailable($"git rev-parse 输出无法解析：\"{revParse.Trim()}\"");

        (string? status, error) = RunGit(productRoot, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (status == null) return Unavailable("git status 失败：" + error);
        List<string> changes = ParseStatus(status);

        bool main = PathIdentity.Same(lines[0], lines[1]);
        string worktree = main ? MainWorktreeName : Path.GetFileName(lines[2].TrimEnd('/', '\\'));
        return new RunSourceIdentity(lines[3], worktree, changes.Take(MaxChanges).ToList(), changes.Count, null);
    }

    internal static List<string> ParseStatus(string status)
    {
        var paths = new List<string>();
        string[] fields = status.Split('\0');
        for (int i = 0; i < fields.Length; i++)
        {
            string field = fields[i];
            if (field.Length < 4) continue;
            paths.Add(field[3..]);
            if (field[0] is 'R' or 'C' || field[1] is 'R' or 'C')
            {
                if (++i < fields.Length && fields[i].Length > 0) paths.Add(fields[i]);
            }
        }

        return paths;
    }

    public static RunSourceIdentity Unavailable(string reason) => new(null, null, [], 0, reason);

    public void WriteParams(Utf8JsonWriter writer)
    {
        if (Error != null)
        {
            writer.WriteString(RimRunner.RimRunnerWireKeys.RunSource.Error, Error);
            return;
        }

        writer.WriteString(RimRunner.RimRunnerWireKeys.RunSource.Commit, Commit);
        writer.WriteString(RimRunner.RimRunnerWireKeys.RunSource.Worktree, Worktree);
        writer.WriteStartArray(RimRunner.RimRunnerWireKeys.RunSource.Changes);
        foreach (string change in Changes) writer.WriteStringValue(change);
        writer.WriteEndArray();
        writer.WriteNumber(RimRunner.RimRunnerWireKeys.RunSource.ChangeCount, ChangeCount);
    }

    public void WarnIfUnavailable(Action<string>? log)
    {
        CliBatchWarnings.Checked(RimRunner.RimRunnerWarningKey.SourceUnavailable);
        if (Error == null) return;
        string text = "无法取得来源身份，读数的封批行只记原因：" + Error;
        log?.Invoke("[WARN] " + text);
        CliBatchWarnings.Add(RimRunner.RimRunnerWarningKey.SourceUnavailable, text);
    }

    private static readonly TimeSpan GitLimit = TimeSpan.FromMinutes(2);

    private static (string? Output, string? Error) RunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            CommandContext.Prepare(start, workingDirectory);

            using Process? process = Process.Start(start);
            if (process == null) return (null, "git 进程无法启动");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (ChildProcess.WaitOrStop(process, GitLimit, "git " + arguments.First(argument => !argument.StartsWith('-'))) is string timedOut)
                return (null, timedOut);
            string output = stdout.GetAwaiter().GetResult();
            string error = stderr.GetAwaiter().GetResult().Trim();
            return process.ExitCode == 0
                ? (output, null)
                : (null, error.Length > 0 ? error : $"git 返回退出码 {process.ExitCode}");
        }
        catch (Exception exception)
        {
            return (null, exception.Message);
        }
    }
}
