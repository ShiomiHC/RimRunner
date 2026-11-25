namespace RimRunnerCli;

internal static class IsolatedRoot
{
    internal const string Variable = ObservationCore.IsolatedRootVariable;
    internal const string SignaturesVariable = "RIMRUNNER_SIGNATURES";

    internal static string? Dir =>
        CommandContext.GetEnvironmentVariable(Variable) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    internal static bool Active => Dir != null;

    internal static string? Refusal(bool inPackage)
    {
        if (Active) return null;
        foreach (string variable in new[] { EnginePackage.RootVariable, SignaturesVariable })
        {
            if (!string.IsNullOrWhiteSpace(CommandContext.GetEnvironmentVariable(variable)))
                return $"{variable} 只在隔离根（{Variable}）下生效；当前不在隔离根下，本命令不运行";
        }

        return inPackage
            ? null
            : $"这份 CLI 不在 RimRunner 安装目录里（旁边没有 {EnginePackage.ManifestFileName}），只在隔离根（{Variable}）下运行";
    }
}
