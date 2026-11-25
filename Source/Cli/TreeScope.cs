namespace RimRunnerCli;

internal sealed class TreeScope
{
    public sealed record Layer(string Name, string ProductRoot, string DataArg, string OutputArg);

    private TreeScope(string productRoot, string? mainProductRoot)
    {
        ProductRoot = productRoot;
        MainProductRoot = mainProductRoot;
    }

    public string ProductRoot { get; }

    public string? MainProductRoot { get; }

    public bool IsMain => MainProductRoot == null;

    public string MainOrSelfProductRoot => MainProductRoot ?? ProductRoot;

    public static TreeScope Of(string productRoot) => new(productRoot, MainWorktree.Of(productRoot));

    public static string TreeName(string productRoot)
    {
        string? gitTreeRoot = Directory.Exists(productRoot) ? MainWorktree.GitTreeRoot(productRoot) : null;
        return Path.GetFileName((gitTreeRoot ?? CommandContext.FullPath(productRoot)).TrimEnd('\\', '/'));
    }

    public IReadOnlyList<Layer> BorrowChain()
    {
        var layers = new List<Layer>();
        if (MainProductRoot != null)
            layers.Add(new Layer(RimRunner.RimRunnerEngineArgs.MainLayerName, MainProductRoot,
                RimRunner.RimRunnerEngineArgs.MainDataRoot, RimRunner.RimRunnerEngineArgs.MainOutputRoot));
        return layers;
    }

    public MachineLock OutputLock(string claimRoot) => MachineLock.ForProductRoot(claimRoot, ProductRoot);

    public static bool WritesTreeLedger(string purpose, bool shardMember) =>
        !shardMember && PurposePolicies.ForEffective(purpose).BatchOutputLock == OutputLockKind.Shared;
}
