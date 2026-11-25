using Experiments = RimRunner.RimRunnerExperiments;

namespace RimRunnerCli;

internal static class ExperimentPolicy
{
    public static string Current(string productRoot)
    {
        OrchestratorDescriptor descriptor = OrchestratorDescriptor.Load(productRoot).Descriptor;
        return Experiments.Join(name => On(descriptor, name));
    }

    private static bool On(OrchestratorDescriptor descriptor, string name) => name switch
    {
        Experiments.Trace => descriptor.Trace,
        Experiments.ForceMainMap => descriptor.ForceMainMap,
        _ => throw new StoreException($"冷启动开关 {name} 在 {RuntimeDocument.FileName} 里没有对应的键"),
    };

    public static string? ReuseRefusal(string? instance, string current)
    {
        if (instance == null)
            return "登记里没有冷启动开关记录";
        return string.Equals(instance, current, StringComparison.Ordinal)
            ? null
            : $"冷启动开关：实例为 {Shown(instance)}，本轮为 {Shown(current)}";
    }

    public static string Shown(string? list) => string.IsNullOrEmpty(list) ? "（无）" : list;
}
