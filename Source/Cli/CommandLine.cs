namespace RimRunnerCli;

internal static class CommandLine
{
    internal static ParseResult Parse(string[] args) => CliParser.Parse(CommandTable.Registry, args);
}
