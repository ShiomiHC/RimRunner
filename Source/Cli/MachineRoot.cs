namespace RimRunnerCli;

public static class MachineRoot
{
    public static string Resolve() =>
        ObservationHits.MachineRoot(IsolatedRoot.Dir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
        ?? throw new StoreException("无法确定用户目录");
}
