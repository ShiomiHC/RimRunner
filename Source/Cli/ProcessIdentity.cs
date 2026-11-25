using System.Diagnostics;

namespace RimRunnerCli;

public static class ProcessIdentity
{
    public readonly record struct Stamp(int Pid, DateTimeOffset? StartedAt);

    public static readonly IReadOnlyList<string> GameNames =
        [Path.GetFileNameWithoutExtension(GameProcessProbe.ProcessName)];

    public static DateTimeOffset? StartedAt(Process process) =>
        RimTooling.Processes.ProcessIdentity.TryGetStartTime(process);

    public static bool Alive(int pid, DateTimeOffset? startedAt, IReadOnlyList<string> names) =>
        RimTooling.Processes.ProcessIdentity.IsAlive(pid, startedAt, names);

    public static bool GameAlive(int pid, DateTimeOffset? startedAt) =>
        RimTooling.RimWorld.GameLauncher.IsGameAlive(pid, startedAt, GameNames);
}
