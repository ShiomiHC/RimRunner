namespace RimRunnerCli;

public static class RecordBuilder
{
    public static string SaveDataRoot
    {
        get
        {
            return Path.Combine(MachineRoot.Resolve(), "savedata");
        }
    }

    public static ClaimRecord Build(
        OrchestratorDescriptor descriptor,
        string productRoot,
        int number,
        string state,
        string? saveDataRoot = null,
        string purpose = ClaimRecord.PurposeBatch)
    {
        return new ClaimRecord
        {
            Number = number,
            TreeName = TreeScope.TreeName(productRoot),
            Product = descriptor.Product,
            ProductRoot = productRoot,
            State = state,
            Purpose = purpose,
            ShardOrdinal = PurposePolicies.ForWire(purpose).AllowsMultiplePerTree ? 1 : null,
            ModsFolder = descriptor.ModsFolder,
            SaveData = Path.Combine(saveDataRoot ?? SaveDataRoot, number.ToString()),
            TestMod = Path.Combine(descriptor.ModsFolder, TestModSynthesizer.PackageIdFor(descriptor, number)),
        };
    }
}
