using System.Text.RegularExpressions;

namespace RimRunnerCli;

internal static partial class GameLogLine
{
    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2} (?:(?<level>TRACE|DEBUG| INFO| WARN|ERROR| CRIT) | {3}(?=at ))")]
    private static partial Regex Prefix();

    internal static string Body(string line)
    {
        Match match = Prefix().Match(line);
        return match.Success ? line[match.Length..] : line;
    }

    internal static bool IsStackFrame(string line) =>
        RimTooling.RimWorld.GameLauncher.IsWaylithStackFrame(line);
}
