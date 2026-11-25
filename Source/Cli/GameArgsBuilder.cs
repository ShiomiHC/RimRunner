namespace RimRunnerCli;

public static class GameArgsBuilder
{
    public static string LogPathFor(ClaimRecord claim) =>
        Path.Combine(claim.SaveData, "Player.log");

    public static string[] Build(
        OrchestratorDescriptor descriptor,
        string productRoot,
        ClaimRecord claim,
        string runId,
        string command,
        string baselineSaveName,
        string? modList,
        string? newGameDescriptorPath = null,
        string? contentFingerprint = null,
        string? textureTier = null,
        IReadOnlyList<string>? modListPackageIds = null,
        string? pipeName = null,
        string? experiments = null,
        IReadOnlyList<string>? keptLoadDirs = null,
        string? compatOff = null,
        string? customSavePath = null)
    {
        string outputRoot = descriptor.ResolveOutputDir(productRoot);
        var args = new List<string>
        {
            "-" + RimRunner.RimRunnerEngineArgs.Enable,
            $"-{RimRunner.RimRunnerEngineArgs.RunId}={runId}",
            $"-{RimRunner.RimRunnerEngineArgs.Command}={command}",
            $"-{RimRunner.RimRunnerEngineArgs.Instance}={claim.Number}",
            GameProcessProbe.MachineRootArg(),
            $"-{RimRunner.RimRunnerEngineArgs.Save}={baselineSaveName}",
            $"-{RimRunner.RimRunnerEngineArgs.DataRoot}={CommandContext.FullPath(Path.Combine(productRoot, descriptor.DataDir))}",
            $"-{RimRunner.RimRunnerEngineArgs.OutputRoot}={outputRoot}",
            $"-savedatafolder={claim.SaveData}",
        };
        foreach (TreeScope.Layer layer in TreeScope.Of(productRoot).BorrowChain())
        {
            args.Add($"-{layer.DataArg}=" + CommandContext.FullPath(Path.Combine(layer.ProductRoot, descriptor.DataDir)));
            args.Add($"-{layer.OutputArg}=" + descriptor.ResolveOutputDir(layer.ProductRoot));
        }

        if (pipeName != null)
            args.Add($"-{RimRunner.RimRunnerEngineArgs.Pipe}={pipeName}");

        if (!string.IsNullOrWhiteSpace(newGameDescriptorPath))
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.NewGame}={newGameDescriptorPath}");
        }

        if (customSavePath != null)
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.CustomSave}={customSavePath}");
        }

        if (!string.IsNullOrWhiteSpace(modList))
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.ModList}={modList}");
        }

        if (modListPackageIds != null)
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.ModListPackages}="
                + RimRunner.RimRunnerPackagePresence.FormatArg([.. modListPackageIds]));
        }

        IReadOnlyList<string> absentable = AbsentablePackages.Derive(descriptor, productRoot);
        if (absentable.Count > 0)
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.AbsentablePackages}="
                + RimRunner.RimRunnerPackagePresence.FormatArg([.. absentable]));
        }

        IReadOnlyList<(string Assembly, string PackageId)> unitAssemblies = UnitAssemblyMap.Derive(descriptor);
        if (unitAssemblies.Count > 0)
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.UnitAssemblies}="
                + RimRunner.RimRunnerPackagePresence.FormatUnitAssemblies(
                    [.. unitAssemblies.Select(pair => pair.Assembly)],
                    [.. unitAssemblies.Select(pair => pair.PackageId)]));
        }

        if (claim.PackageIds.Count > 0)
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.SuffixedPackageIds}="
                + RimRunner.RimRunnerPackagePresence.FormatArg([.. claim.PackageIds]));
        }

        if (keptLoadDirs is { Count: > 0 })
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.KeptLoadDirs}="
                + string.Join(RimRunner.RimRunnerEngineArgs.KeptLoadDirsSeparator, keptLoadDirs));
        }

        if (!string.IsNullOrEmpty(contentFingerprint))
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.ContentFingerprint}={contentFingerprint}");
        }

        if (!string.IsNullOrEmpty(experiments))
            args.Add($"-{RimRunner.RimRunnerEngineArgs.Experiments}={experiments}");

        if (ObservationMarks.MarksEnabled())
            args.Add($"-{RimRunner.RimRunnerEngineArgs.ObservationMarks}");

        if (!string.IsNullOrEmpty(compatOff))
            args.Add($"-{RimRunner.RimRunnerEngineArgs.CompatOff}={compatOff}");

        if (!string.IsNullOrEmpty(textureTier))
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.TextureTier}={textureTier}");
        }

        if (descriptor.Breaker?.Pumps is int pumps)
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.BreakerPumps}={pumps}");
        }

        if (descriptor.Breaker?.Errors is int errors)
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.BreakerErrors}={errors}");
        }

        PurposePolicy policy = claim.Policy;
        if (policy.ShowsWindow)
            args.Add($"-{RimRunner.RimRunnerEngineArgs.WindowTitle}={WindowTitleFor(claim, modList)}");
        else
            args.Add("-NoWindow");

        (int width, int height) = (descriptor.WindowSize.Width, descriptor.WindowSize.Height);
        args.Add($"-{RimRunner.RimRunnerEngineArgs.WindowSize}={width},{height}");
        if (policy.LocksHostWindowSize)
        {
            args.Add($"-{RimRunner.RimRunnerEngineArgs.HostWindowSize}={width},{height}");
        }

        args.Add("-NoFileLog");
        args.Add("-NoVulkanFallback");
        args.Add("-NoCrashDialog");
        return [.. args];
    }

    public static string WindowTitleFor(ClaimRecord claim, string? modList)
    {
        string title = $"RimWorld · RimRunner · {claim.Product} · {claim.DisplayName}";
        return string.IsNullOrEmpty(modList) ? title : title + " · " + modList;
    }
}
