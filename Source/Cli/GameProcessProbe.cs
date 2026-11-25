using System.Management;
using System.Text.RegularExpressions;

namespace RimRunnerCli;

public static class GameProcessProbe
{
    public const string ProcessName = "RimWorld.exe";

    internal static Func<List<GameProcess>>? EnumerateOverride;

    public static List<GameProcess> Enumerate() => EnumerateOverride?.Invoke() ?? EnumerateByImageName(ProcessName);

    public static List<GameProcess> EnumerateByImageName(string imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName))
        {
            throw new ArgumentException("映像名不能为空", nameof(imageName));
        }

        string escaped = imageName.Replace("'", "''", StringComparison.Ordinal);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{escaped}'");
            return
            [
                .. searcher.Get()
                    .Cast<ManagementObject>()
                    .Select(mo => new GameProcess(
                        (int)(uint)mo["ProcessId"], (string?)mo["CommandLine"]))
            ];
        }
        catch (ManagementException exception)
        {
            throw new StoreException($"无法枚举 {imageName}：{exception.Message}");
        }
    }

    public static bool ClaimsNumber(string? commandLine, string numberArg, int number) =>
        !string.IsNullOrWhiteSpace(commandLine)
        && Regex.IsMatch(commandLine, $@"(?:^|\s)-{Regex.Escape(numberArg)}={number}(?:\s|$)")
        && SameMachineRoot(commandLine);

    public static int? ClaimedNumber(string? commandLine, string numberArg)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        Match match = Regex.Match(
            commandLine, $@"(?:^|\s)-{Regex.Escape(numberArg)}=(\d+)(?:\s|$)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int number) && SameMachineRoot(commandLine)
            ? number
            : null;
    }

    public static string MachineRootArg() => $"-{RimRunner.RimRunnerEngineArgs.MachineRoot}={MachineRoot.Resolve()}";

    private static bool SameMachineRoot(string commandLine) =>
        ArgValue(commandLine, RimRunner.RimRunnerEngineArgs.MachineRoot) is { } root
        && PathIdentity.Same(root, MachineRoot.Resolve());

    public static IEnumerable<int> ClaimedNumbers() =>
        Enumerate()
            .Select(process => ClaimedNumber(process.CommandLine, RimRunner.RimRunnerEngineArgs.Instance))
            .OfType<int>();

    public static string? ArgValue(string? commandLine, string argName)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        string name = Regex.Escape(argName);
        Match match = Regex.Match(
            commandLine, $@"(?:^|\s)(?:""-{name}=([^""]*)""|-{name}=([^\s""]+))(?=\s|$)");
        if (!match.Success) return null;
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    public static bool HasArg(string? commandLine, string argName) =>
        !string.IsNullOrWhiteSpace(commandLine)
        && Regex.IsMatch(commandLine, $@"(?:^|\s)""?-{Regex.Escape(argName)}(?:=[^\s""]*)?""?(?=\s|$)");

    public sealed record GameProcess(int Pid, string? CommandLine);
}
