namespace RimRunnerCli;

public static class IdleExit
{
    public const int ShardDefault = 4;
    public const int BatchDefault = 10;
    public const int FilmDefault = 20;
    public const int ShowcaseDefault = 20;

    public static int Resolve(RimRunnerConfig.MachineConfig machine, InstancePurpose purpose) =>
        PurposePolicies.For(purpose).IdleMinutes(machine);

    public static int Resolve(RimRunnerConfig.MachineConfig machine, string purpose) =>
        PurposePolicies.ForEffective(purpose).IdleMinutes(machine);

    public static bool SurvivesDrain(string purpose) =>
        PurposePolicies.ForEffective(purpose).SurvivesDrain;

    public static double EvictableMinutes(double idleLimitMinutes) => idleLimitMinutes / 2;

    public static string Minutes(double minutes) =>
        minutes.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " 分钟";

    public static string? StopNote(ClaimRecord claim)
    {
        if (claim.StopReason != null)
            return claim.StoppedAt is { } at ? $"{claim.StopReason}（{at.ToLocalTime():HH:mm:ss}）" : claim.StopReason;
        return InstanceLink.SelfExitReason(GameArgsBuilder.LogPathFor(claim)) is string own
            ? "实例自行退出：" + own
            : null;
    }
}
