namespace RimRunner;

public static class RimRunnerLog
{
    public static readonly string Prefix =
        "[" + (RimRunnerProduct.TestAssembly ?? RimRunnerDefScope.EngineAssembly).GetName().Name + "]";

    internal const string CategoryName = "RimRunner";

    internal static readonly Brutal.Logging.LogCategory Category = Brutal.Logging.LogCategory.Make(CategoryName);
}
