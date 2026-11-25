namespace RimRunnerCli;

public static class InstanceProbe
{
    public sealed record PingSnapshot(
        bool InGame, string? RunState, Dictionary<string, string>? AssemblyMvids,
        string? StartupFault = null, string? Mode = null, IReadOnlyList<string>? ProductCapabilities = null,
        double? IdleSeconds = null, double? IdleLimitMinutes = null, LiveHold? Hold = null, int Drives = 0);

    public static bool IsOwnedAutoTestProcess(GameProcessProbe.GameProcess process, int number) =>
        process.CommandLine != null
        && CarriesAutoTestMarker(process.CommandLine)
        && GameProcessProbe.ClaimsNumber(process.CommandLine, RimRunner.RimRunnerEngineArgs.Instance, number);

    public static bool CarriesAutoTestMarker(string commandLine) =>
        GameProcessProbe.HasArg(commandLine, RimRunner.RimRunnerEngineArgs.Enable);

    public static (string Verdict, string? RunState) Liveness(int number) => Liveness(number, GameProcessProbe.Enumerate());

    public static (string Verdict, string? RunState) Liveness(
        int number, IReadOnlyList<GameProcessProbe.GameProcess> processes)
    {
        (string verdict, PingSnapshot? ping) = Detailed(number, processes);
        return (verdict, ping?.RunState);
    }

    public static (string Verdict, PingSnapshot? Ping) Detailed(int number) =>
        Detailed(number, GameProcessProbe.Enumerate());

    public static (string Verdict, PingSnapshot? Ping) Detailed(
        int number, IReadOnlyList<GameProcessProbe.GameProcess> processes)
    {
        int owned = processes.Count(process => IsOwnedAutoTestProcess(process, number));
        PingSnapshot? ping = Ping(number);
        return (BusyGate.Verdict(owned, ping?.RunState, forceBusy: false), ping);
    }

    public static string? RunState(int number) => Ping(number)?.RunState;

    internal static HashSet<int> DrivenNumbers()
    {
        HashSet<int> driven = [.. InstanceLinks.All.Where(link => link.State?.Drives > 0).Select(link => link.Number)];
        foreach (MachineSnapshot.Instance remote in MachineSnapshot.Read()?.Instances ?? [])
        {
            if (remote.Ping?.Drives > 0) driven.Add(remote.Number);
        }

        return driven;
    }

    public static LiveHold? Hold(int number) =>
        InstanceLinks.Get(number) is { } link ? link.Hold : MachineSnapshot.Remote(number)?.Ping?.Hold;

    public static string? CurrentRunId(int number) =>
        InstanceLinks.Get(number) is { } link ? link.Runtime.CurrentRunId : MachineSnapshot.Remote(number)?.CurrentRunId;

    public static PingSnapshot? Ping(int number, bool touch = false) =>
        InstanceLinks.Get(number) is { } link ? Snapshot(link, touch) : MachineSnapshot.Remote(number)?.Ping;

    internal static PingSnapshot? Snapshot(InstanceLink link, bool touch = false)
    {
        if (!link.Connected || link.Hello is not { } hello || link.State is not { } state) return null;
        if (touch) link.Touch();
        return Compose(hello, state, link.IdleFor, link.IdleLimit) with { Hold = link.Hold, Drives = state.Drives };
    }

    internal static PingSnapshot Compose(InstanceLink.Handshake hello, InstanceLink.RunStatus state,
        TimeSpan? idleFor, TimeSpan idleLimit) =>
        new(state.InGame, state.RunState, hello.AssemblyMvids, state.StartupFault, hello.Mode,
            hello.ProductCapabilities, idleFor?.TotalSeconds,
            idleLimit > TimeSpan.Zero ? idleLimit.TotalMinutes : null);

    public enum FactsSource { Local, Remote, Process }

    public sealed record InstanceFacts(int Pid, DateTimeOffset? StartedAt, DateTimeOffset? LaunchedAt,
        DateTimeOffset? LoadedAt, string? TextureTier, FactsSource Source);

    internal static InstanceFacts? Facts(int number, IReadOnlyList<GameProcessProbe.GameProcess>? processes = null,
        MachineSnapshot? machine = null)
    {
        if (InstanceLinks.Get(number) is { Pid: int pid } link)
        {
            InstanceRuntime runtime = link.Runtime;
            return new InstanceFacts(pid, runtime.GameStartedAt, runtime.LaunchedAt, runtime.LoadedAt,
                runtime.TextureTier, FactsSource.Local);
        }

        if ((machine != null ? machine.RemoteOf(number) : MachineSnapshot.Remote(number)) is { Pid: int remotePid } remote)
            return new InstanceFacts(remotePid, remote.StartedAt, remote.LaunchedAt, remote.LoadedAt,
                remote.TextureTier, FactsSource.Remote);

        GameProcessProbe.GameProcess? game =
            (processes ?? GameProcessProbe.Enumerate()).FirstOrDefault(process => IsOwnedAutoTestProcess(process, number));
        return game == null
            ? null
            : new InstanceFacts(game.Pid, null, null, null,
                GameProcessProbe.ArgValue(game.CommandLine, RimRunner.RimRunnerEngineArgs.TextureTier), FactsSource.Process);
    }

    public static LoadedPacks LoadedPackIds(int number)
    {
        InstanceLink? link = InstanceLinks.Get(number);
        if (link is not { Connected: true })
            return new LoadedPacks(null, "实例管道未连接");
        if (link.Hello is not { } hello)
            return new LoadedPacks(null, "实例管道已连接，未收到握手");
        if (hello.LoadedPackIds is not { } ids)
            return new LoadedPacks(null, $"握手里没有 {RimRunner.RimRunnerPipeProtocol.Keys.LoadedPackIds} 数组");
        return hello.ModAssemblyShortfalls is { } shortfalls
            ? new LoadedPacks(ids, null, shortfalls)
            : new LoadedPacks(null, $"握手里没有 {RimRunner.RimRunnerPipeProtocol.Keys.ModAssemblyShortfalls} 对象");
    }

    public sealed record LoadedPacks(IReadOnlyList<string>? Ids, string? Fault,
        IReadOnlyDictionary<string, string>? Shortfalls = null);
}
