namespace RimRunnerCli;

public static class ShardLayout
{
    public const string ShardsSubDirName = "shards";
    public const string ManifestFileName = "manifest.json";
    public const string QueueFileName = "queue.json";

    public static string GroupsRoot(OrchestratorDescriptor descriptor, string productRoot) =>
        Path.Combine(descriptor.ResolveReportsDir(productRoot), ShardsSubDirName);

    public static string GroupDir(OrchestratorDescriptor descriptor, string productRoot, string group) =>
        Path.Combine(GroupsRoot(descriptor, productRoot), group);

    public static string QueuePath(string groupDir) =>
        Path.Combine(groupDir, QueueFileName);
}
