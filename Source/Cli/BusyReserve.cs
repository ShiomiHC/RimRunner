namespace RimRunnerCli;

public static class BusyReserve
{
    public const double DefaultCores = 1;

    public const double InstanceCores = 1;

    public static double Resolve() => RimRunnerConfig.Machine().BusyReserve.Value;
}
