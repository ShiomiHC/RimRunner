namespace RimRunnerCli;

internal static class LaunchLogIdentity
{
    private const string ArgsLinePrefix = "Command line arguments:";

    public static string? Mismatch(string logText, string runId)
    {
        string? argsLine = logText
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => GameLogLine.Body(line).Trim())
            .FirstOrDefault(line => line.StartsWith(ArgsLinePrefix, StringComparison.Ordinal));
        if (argsLine == null)
            return "实例日志头无 " + ArgsLinePrefix + " 行，无法判定属于哪一轮，不读正文";

        string key = "-" + RimRunner.RimRunnerEngineArgs.RunId + "=";
        string? logged = argsLine
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(token => token.StartsWith(key, StringComparison.Ordinal))
            ?[key.Length..];
        if (logged == null)
            return "实例日志头无 " + key.TrimEnd('=') + " 参数，无法判定属于哪一轮，不读正文";
        if (logged != runId)
            return $"实例日志头 runId={logged} 与本轮 runId={runId} 不匹配，不读正文";
        return null;
    }
}
