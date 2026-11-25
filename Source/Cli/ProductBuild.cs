using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RimRunnerCli;

public static class ProductBuild
{
    private static readonly TimeSpan BuildTimeoutPerProject = TimeSpan.FromMinutes(2);

    public static void Compile(IReadOnlyList<string> absoluteCsprojs, string productRoot, string packageRoot,
        Action<string>? onLogLine)
    {
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("rimrunner-build-");
        try
        {
            string traversal = Path.Combine(scratch.FullName, "buildOrder.proj");
            File.WriteAllText(traversal, TraversalProject(absoluteCsprojs));
            RunDotnetBuild(traversal, productRoot, packageRoot, absoluteCsprojs.Count, onLogLine);
        }
        finally
        {
            try
            {
                scratch.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static string TraversalProject(IEnumerable<string> absoluteCsprojs)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("<Project DefaultTargets=\"Build\">");
        builder.AppendLine("  <ItemGroup>");
        foreach (string csproj in absoluteCsprojs)
        {
            builder.AppendLine(
                $"    <BuildOrderProject Include=\"{System.Security.SecurityElement.Escape(MsBuildEscape(csproj))}\" />");
        }

        builder.AppendLine("  </ItemGroup>");
        builder.AppendLine("  <Target Name=\"Restore\">");
        builder.AppendLine("    <MSBuild Projects=\"@(BuildOrderProject)\" Targets=\"Restore\" BuildInParallel=\"false\" />");
        builder.AppendLine("  </Target>");
        builder.AppendLine("  <Target Name=\"Build\">");
        builder.AppendLine("    <MSBuild Projects=\"@(BuildOrderProject)\" Targets=\"Build\" BuildInParallel=\"false\" />");
        builder.AppendLine("  </Target>");
        builder.AppendLine("</Project>");
        return builder.ToString();
    }

    internal const string Configuration = "Release";

    internal static IReadOnlyList<string> BuildArguments(string traversal, string packageRoot) =>
    [
        "build", traversal, "-c", Configuration, "--nologo", "-v", "q",
        $"-p:{EnginePackage.HomeProperty}={MsBuildEscape(packageRoot)}",
    ];

    private static string MsBuildEscape(string value) => value
        .Replace("%", "%25")
        .Replace("$", "%24")
        .Replace("@", "%40")
        .Replace("'", "%27")
        .Replace(";", "%3B")
        .Replace("?", "%3F")
        .Replace("*", "%2A");

    private static void RunDotnetBuild(string traversal, string productRoot, string packageRoot, int projectCount,
        Action<string>? onLogLine)
    {
        TimeSpan timeout = BuildTimeoutPerProject * Math.Max(1, projectCount);
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in BuildArguments(traversal, packageRoot))
            info.ArgumentList.Add(arg);
        CommandContext.Prepare(info, productRoot);
        using Process? process = Process.Start(info) ?? throw new StoreException("无法启动 dotnet build");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception)
            {
                onLogLine?.Invoke("[FAIL] 超时的 dotnet build 无法停止："
                    + exception.Message
                    + "；无法保证结果");
            }

            throw new StoreException(
                $"编译超时：{projectCount} 个工程 {timeout.TotalMinutes} 分钟内未完成编译");
        }

        string output = stdout.GetAwaiter().GetResult()
            + stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new StoreException($"编译失败：\n{output}");
        }

        if (onLogLine != null)
        {
            string[] diagnostics = [.. DiagnosticLines(output)];
            foreach (string line in DiagnosticScreenLines(diagnostics, productRoot))
                onLogLine(line);
            foreach (string line in diagnostics)
                CliLog.Info("[编译诊断] " + line);
        }
    }

    internal static List<string> DiagnosticScreenLines(IEnumerable<string> diagnostics, string productRoot)
    {
        List<string> screen = [];
        FindingPresentation.Facts(diagnostics.Select(line => DiagnosticFinding(line, productRoot)),
            "", "[编译诊断]", screen, logOnly: [], indent: "");
        return screen;
    }

    private static EngineReportFinding DiagnosticFinding(string line, string productRoot)
    {
        Match match = PositionedDiagnostic.Match(line);
        if (!match.Success)
            return new EngineReportFinding { Key = null, Subject = null, Text = line };

        string file = match.Groups["file"].Value;
        string relative = Path.GetRelativePath(productRoot, file);
        string where = (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? file : relative)
            + "(" + match.Groups["pos"].Value + ")";
        return new EngineReportFinding { Key = match.Groups["code"].Value, Subject = where, Text = match.Groups["what"].Value };
    }

    internal static IEnumerable<string> DiagnosticLines(string output)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (MsBuildDiagnostic.IsMatch(line) && seen.Add(line))
            {
                yield return line;
            }
        }
    }

    private static readonly Regex PositionedDiagnostic =
        new(@"^(?<file>.+?)\((?<pos>\d+(?:,\d+)*)\):\s(?<what>(?:warning|error)\s(?<code>[A-Za-z]+\d+):\s.*?)(?:\s\[[^\]]+\])?$",
            RegexOptions.CultureInvariant);

    private static readonly Regex MsBuildDiagnostic =
        new(@":\s(?:warning|error)\s[A-Za-z]+\d+:", RegexOptions.CultureInvariant);
}
