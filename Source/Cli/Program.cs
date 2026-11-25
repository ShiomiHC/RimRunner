using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args is [OrchestratorPipe.LaunchVerb])
            return OrchestratorHost.Launch();
        if (args is [OrchestratorPipe.HostVerb])
            return OrchestratorHost.Run();
        if (IsolatedRoot.Refusal(OrchestratorRoute.InPackage) is string refusal)
        {
            Console.WriteLine($"[FAIL] {refusal}");
            return ExitCodes.Incomplete;
        }

        if (args is [DescriptorTemplate.Command, ..])
        {
            if (args.Skip(1).Any(arg => arg.StartsWith("--", StringComparison.Ordinal)))
                return Usage($"{DescriptorTemplate.Command} 不收选项");
            CliLog log = CliLog.Install();
            try
            {
                return DescriptorTemplate.Execute(CommandContext.CurrentDirectory, args[1..], problem => Usage(problem));
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[FAIL] {exception.Message}");
                return ExitPolicy.FromException(exception);
            }
            finally
            {
                log.Close();
            }
        }

        try
        {
            if (ModListListing.StripBare(args) is { } rest && ListModLists(rest) is int listed)
                return listed;
            ParseResult result = CommandLine.Parse(args);
            if (result is not ParseResult.Success success)
                return Answer(result);
            ParsedCommand parsed = success.Parsed;
            if (OrchestratorRoute.Forwards(parsed))
            {
                if (PreConnectionRosterCheck(parsed) is int refused)
                    return refused;
                int code = OrchestratorClient.Forward(args);
                if (parsed.Command.Name == "unmount" && code == 0
                    && TreeHosts.OwnDevTree() is string own && TreeHosts.Unclaimed(own))
                    TreeHosts.AwaitExit([own], Console.Out);
                return code;
            }
            if (OrchestratorRoute.ForwardsIfRunning(parsed) && OrchestratorClient.ForwardIfRunning(args) is int forwarded)
                return forwarded;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[FAIL] {exception.Message}");
            return ExitPolicy.FromException(exception);
        }

        return RunCommand(args);
    }

    private static int? ListModLists(string[] rest)
    {
        ParseResult result = CommandLine.Parse(rest);
        if (result is not ParseResult.Success { Parsed: var parsed })
            return Answer(result);
        if (!parsed.Operation.Accepts(Switches.Modlist))
            return null;

        TreeSelector.Selection? tree = SelectTree(parsed);
        (string productRoot, OrchestratorDescriptor descriptor) = tree == null
            ? Discover(CommandContext.CurrentDirectory)
            : (tree.ProductRoot, OrchestratorDescriptor.Load(tree.ProductRoot).Descriptor);
        if (tree == null && parsed.Operation.Accepts(Switches.Product) && CwdProductMismatch(parsed, descriptor) is { } mismatch)
        {
            Console.WriteLine($"[FAIL] {mismatch}");
            return ExitCodes.Incomplete;
        }

        Console.WriteLine($"[FAIL] {Switches.Modlist.FlagName} 未带取值，{parsed.Operation.Name} 未执行");
        foreach (string line in ModListListing.Lines(Path.Combine(productRoot, descriptor.ModListsDir), descriptor.DefaultModList))
            Console.WriteLine(line);
        return ExitCodes.Usage;
    }

    internal static int Answer(ParseResult result) => result switch
    {
        ParseResult.Help help => CommandHelp.Print(help),
        ParseResult.Version => VersionCode(),
        ParseResult.UsageError { IsUnknownCommand: true } error => Fail(error.Errors[0]),
        ParseResult.UsageError error => Usage(error.Errors[0],
            error.HelpVerb is { } verb ? error.HelpCommand + " " + verb : error.HelpCommand),
        _ => throw new UnreachableException(),
    };

    private static int Fail(string problem)
    {
        Console.WriteLine($"[FAIL] {problem}");
        return ExitCodes.Usage;
    }

    private static int VersionCode()
    {
        PrintVersion();
        return 0;
    }

    private static TreeSelector.Selection? SelectTree(ParsedCommand parsed) =>
        parsed.Operation.Accepts(Switches.Tree) && parsed.Text(Switches.Tree) is string treeSpec
            ? TreeSelector.Resolve(treeSpec, new ClaimStore(),
                parsed.Operation.Accepts(Switches.Product) ? parsed.Text(Switches.Product) : null)
            : null;

    private static string? CwdProductMismatch(ParsedCommand parsed, OrchestratorDescriptor descriptor) =>
        parsed.Text(Switches.Product) is string product
        && !string.Equals(descriptor.Product, product, StringComparison.OrdinalIgnoreCase)
            ? $"--product {product}：当前目录的产品是 {descriptor.Product}"
            : null;

    internal static int? PreConnectionRosterCheck(ParsedCommand parsed)
    {
        if (parsed.Command.Name != "run")
            return null;
        IReadOnlyList<string> caseIds = CommandTable.SplitCaseIds(parsed.Operation, parsed.Positionals, out _) ?? [];
        if (caseIds.Count == 0)
            return null;

        string productRoot;
        OrchestratorDescriptor descriptor;
        Roster.Files files;
        try
        {
            TreeSelector.Selection? tree = SelectTree(parsed);
            productRoot = tree?.ProductRoot ?? Discover(CommandContext.CurrentDirectory).ProductRoot;
            descriptor = OrchestratorDescriptor.Load(productRoot).Descriptor;
            if (tree == null && CwdProductMismatch(parsed, descriptor) != null)
                return null;
            ArtifactGate.Output output = new ArtifactGate(descriptor, productRoot).Check(mode: ArtifactGate.Mode.RefuseStale);
            files = Roster.Ensure(descriptor, productRoot, output);
        }
        catch (Exception exception) when (exception is StoreException or DescriptorException
                                              or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        ModListDefinition? modList;
        try
        {
            string? name = descriptor.ResolveModList(parsed.Text(Switches.Modlist));
            modList = name == null
                ? null
                : ModListDefinition.Load(Path.Combine(productRoot, descriptor.ModListsDir), name);
        }
        catch (Exception exception) when (exception is StoreException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        IReadOnlyList<string> faults = RosterSelection.Check(caseIds, files.Cases, modList);
        if (faults.Count == 0)
            return null;

        CliLog log = CliLog.Install();
        try
        {
            CliLog.Target("run", productRoot, descriptor);
            foreach (string fault in faults)
                Console.WriteLine("[FAIL] " + fault);
        }
        finally
        {
            log.Close();
        }

        return ExitCodes.Incomplete;
    }

    private static (ArtifactGate.Output Output, Roster.Files Rosters) ColdStartInputs(
        string productRoot, OrchestratorDescriptor descriptor)
    {
        ArtifactGate.Output output = new ArtifactGate(descriptor, productRoot)
            .Check(line => Console.WriteLine(line), mode: ArtifactGate.Mode.ColdStart);
        return (output, Roster.Ensure(descriptor, productRoot, output));
    }

    internal static int RunCommand(string[] args)
    {
        CliLog log = CliLog.Install();
        try
        {
            ParseResult result = CommandLine.Parse(args);
            int code = result switch
            {
                ParseResult.Success success => Execute(success.Parsed),
                _ => Answer(result),
            };
            if (CommandContext.Current?.RevokeReason is not { } revoked) return code;
            Console.WriteLine($"[撤销] {revoked}");
            return ExitCodes.Incomplete;
        }
        catch (OperationCanceledException) when (CommandContext.Current?.RevokeReason is { } reason)
        {
            Console.WriteLine($"[撤销] {reason}");
            return ExitCodes.Incomplete;
        }
        catch (OperationCanceledException) when (CommandContext.ClientGone.IsCancellationRequested)
        {
            Console.WriteLine("[断开] 客户端已断开：本命令还在排队，已撤销");
            return ExitCodes.Incomplete;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[FAIL] {exception.Message}");
            return ExitPolicy.FromException(exception);
        }
        finally
        {
            string? logPath = log.Close();
            if (logPath != null && log.ShowsPath)
            {
                Console.WriteLine($"RimRunner 日志：{logPath}");
            }
        }
    }

    internal static void RunShardChildCommand(ShardChildSpec spec)
    {
        CliLog log = spec.GroupLog is { } group ? CliLog.InstallShard(group, ShardChildSpec.Tag(spec.Number)) : CliLog.Install();
        int code = ExitCodes.InternalFault;
        try
        {
            code = RunShardChild(spec);
        }
        catch (OperationCanceledException) when (CommandContext.Current?.RevokeReason is { } reason)
        {
            Console.WriteLine($"[撤销] {reason}");
            code = ExitCodes.Incomplete;
        }
        catch (QueueTimeoutException exception) when (spec is { Dispatch: false, Queue: not null })
        {
            Console.WriteLine($"[备片] {exception.Message}");
            spec.Result.QueueTimedOut = true;
            code = ExitCodes.Incomplete;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[FAIL] {exception.Message}");
            code = ExitPolicy.FromException(exception);
        }
        finally
        {
            spec.Result.ExitCode = code;
            log.Close();
        }
    }

    private static int RunShardChild(ShardChildSpec spec)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(spec.ProductRoot);
        CliLog.Target("run", productRoot, descriptor);
        Console.WriteLine(spec.Dispatch
            ? $"[分片] 组 {spec.Group} 第 {spec.Index}/{spec.Count} 片，实例 {ClaimStore.Named(spec.Number)}"
            : $"[分片] 备片，实例 {ClaimStore.Named(spec.Number)}");
        static void announce(StructuredRunRequest batch, bool envChecksStripped) =>
            AnnounceBatch(batch, envChecksStripped);
        foreach (CliBatchWarnings.Entry warning in spec.BatchWarnings)
            CliBatchWarnings.Add(warning.Key, warning.Text);
        foreach (string key in spec.BatchChecks)
            CliBatchWarnings.Checked(key);
        return new RunCoordinator(descriptor, productRoot, new ClaimStore(),
                queueMinutes: spec.QueueMinutes)
            .LaunchAsync(new Launch.ShardPiece(spec, announce),
                new LaunchCommon(spec.ModList, spec.ForceBusy, spec.TimeoutMinutes, line => Console.WriteLine(line),
                    Save: spec.CustomSave == null ? null : BaselineSave.FromPath(spec.CustomSave)))
            .GetAwaiter().GetResult();
    }

    private static int Execute(ParsedCommand parsed)
    {
        string searchDir = CommandContext.CurrentDirectory;
        string command = parsed.Command.Name;

        TreeSelector.Selection? tree = SelectTree(parsed);
        if (tree != null)
        {
            string? purpose = CommandTable.TreePurposeOf(parsed);
            if (tree.Claim is { } claim && purpose != null && claim.EffectivePurpose != purpose)
            {
                Console.WriteLine($"[FAIL] 实例 {claim.DisplayName} 的登记用途是 {claim.EffectivePurpose}，{command} 只认 {purpose}");
                return ExitCodes.Incomplete;
            }

            string? cwdProductRoot = OrchestratorDescriptor.FindProductRoot(searchDir);
            if (command is not ("status" or "stop" or "unmount") && cwdProductRoot != null
                && !PathIdentity.Same(cwdProductRoot, tree.ProductRoot))
                Console.WriteLine($"产品根：{tree.ProductRoot}（--tree {parsed.Text(Switches.Tree)}）");
            searchDir = tree.ProductRoot;
        }

        if (tree == null && command != "status" && parsed.Operation.Accepts(Switches.Product)
            && parsed.Text(Switches.Product) != null && CwdProductMismatch(parsed, Discover(searchDir).Descriptor) is { } mismatch)
        {
            Console.WriteLine($"[FAIL] {mismatch}");
            return ExitCodes.Incomplete;
        }

        bool namesAuto = parsed.Operation.Accepts(Switches.RunId);
        string? runIdArg = namesAuto ? parsed.Text(Switches.RunId) : null;
        if (namesAuto && (runIdArg != null || (tree?.Claim is { PurposeKind: InstancePurpose.Batch } && command != "status")))
        {
            string autoRoot = tree?.ProductRoot ?? Discover(searchDir).ProductRoot;
            List<ClaimRecord> claims = new ClaimStore().FindByProductRoot(autoRoot);
            AutoInstances.Pick pick = AutoInstances.Resolve(claims,
                AutoInstances.Live(claims, AutoInstances.AliveNumbers()), runIdArg, InstanceProbe.CurrentRunId);
            if (pick.Refusal.Count > 0)
            {
                foreach (string line in pick.Refusal) Console.WriteLine(line);
                return ExitCodes.Incomplete;
            }

            if (pick.Claim is { } chosen)
                tree = new TreeSelector.Selection(autoRoot, chosen.DisplayName, chosen.EffectivePurpose, chosen);
        }

        int? selectedNumber = tree?.Claim?.Number;

        if (parsed.Bare)
        {
            (string discoveredRoot, _) = Discover(searchDir);
            if (new ClaimStore().FindByProductRoot(discoveredRoot).Count == 0)
            {
                Console.WriteLine($"本树尚无登记：{discoveredRoot}");
                PrintUnaccountedGames();
                return 0;
            }
        }

        if (CommandTable.MetaOf(parsed.Operation).Launch != null)
            OrphanMountSweep.Run(searchDir, new ClaimStore(), RecordBuilder.SaveDataRoot, Console.WriteLine);

        IReadOnlyList<string> tail = parsed.TailAfterCommand();
        IReadOnlyList<string> caseIds = command is "run" or "history"
            ? CommandTable.SplitCaseIds(parsed.Operation, parsed.Positionals, out _) ?? []
            : [];
        return command switch
        {
            "status" => parsed.Flag(Switches.All) || (parsed.Text(Switches.Product) != null && tree == null)
                        || (tree == null && OrchestratorDescriptor.FindProductRoot(searchDir) == null)
                ? StatusAll(parsed.Text(Switches.Product), parsed.Flag(Switches.Json))
                : Status(searchDir, tree?.Group?.Select(claim => claim.Number).ToHashSet()
                                    ?? (selectedNumber is int number ? [number] : null),
                    shardDetail: selectedNumber != null),
            "machine" => MachineReport.Print(new ClaimStore().Root, parsed.Flag(Switches.Estimates)),
            "cache" => CacheCommand.Execute([.. tail], problem => Usage(problem, "cache")),
            "engine" => parsed.Operation.Verb == "find"
                ? EngineCommand.Execute(tail, json: false, full: parsed.Flag(Switches.Full),
                    problem => Usage(problem, "engine"))
                : EngineCommand.Execute(tail, parsed.Flag(Switches.Json), full: false,
                    problem => Usage(problem, "engine")),
            "config" => ConfigCaptureCommand.Execute(searchDir, parsed.Text(Switches.Modlist), parsed.Text(Switches.Layer),
                parsed.Given(Switches.File) ? parsed.List(Switches.File) : []),
            "cases" => CasesCommand.Execute(tail, searchDir, parsed.Flag(Switches.Json),
                parsed.Operation.Accepts(Switches.Full) && parsed.Flag(Switches.Full)),
            "vram" => Vram(searchDir, parsed.Positionals),
            "reports" => Reports(searchDir, parsed.Flag(Switches.Failed),
                parsed.Int(Switches.Limit) ?? DefaultReportsLimit),
            "samples" => SamplesConvert(searchDir, SampleStore.ParseKind(parsed.Text(Switches.To))!.Value),
            "history" => History(searchDir, parsed.Text(Switches.Modlist), parsed.Text(Switches.Save),
                parsed.Operation.Verb == HistoryMarkWord ? caseIds : [],
                parsed.Operation.Verb == HistoryMarkWord ? [] : caseIds),
            "unmount" => Unmount(searchDir, tree, parsed.Flag(Switches.All), parsed.Flag(Switches.Force)),
            "stop" => Stop(searchDir, tree, parsed.Flag(Switches.All), parsed.Flag(Switches.Force)),
            "showcase" => parsed.Operation.Verb switch
            {
                null when tail.Count == 0 => StartShowcase(searchDir, parsed.Text(Switches.Modlist),
                    parsed.Flag(Switches.Force), parsed.Int(Switches.TimeoutMinutes),
                    QueuePolicy.Resolve(parsed.Int(Switches.QueueMinutes), line => Console.WriteLine(line))),
                null => Showcase(searchDir, parsed.Text(Switches.Modlist), tail,
                    parsed.Flag(Switches.Force), parsed.Int(Switches.TimeoutMinutes),
                    parsed.Int(Switches.QueueMinutes)),
                "list" => ShowcaseRosterPage(searchDir),
                "teardown" => Showcase(searchDir, null, tail, false, parsed.Int(Switches.TimeoutMinutes), null),
                _ => Showcase(searchDir, null, tail, false, null, null),
            },
            "run" => RunBatch(searchDir, parsed.Text(Switches.Modlist), parsed.Text(Switches.Save), caseIds,
                parsed.Flag(Switches.IncludeExcluded), parsed.Flag(Switches.Force),
                parsed.Int(Switches.TimeoutMinutes),
                QueuePolicy.Resolve(parsed.Int(Switches.QueueMinutes), line => Console.WriteLine(line)),
                envChecksOnly: parsed.Flag(Switches.EnvChecks), noCompat: parsed.Flag(Switches.NoCompat),
                keepCompat: parsed.Given(Switches.KeepCompat) ? parsed.List(Switches.KeepCompat) : null,
                inputs: parsed.Repeated(Switches.Input),
                cliGroups: parsed.Given(Switches.Group) ? parsed.List(Switches.Group) : null),
            "start" => Start(searchDir, parsed.Text(Switches.Modlist), parsed.Flag(Switches.Force),
                parsed.Int(Switches.TimeoutMinutes),
                QueuePolicy.Resolve(parsed.Int(Switches.QueueMinutes), line => Console.WriteLine(line))),
            "film" when parsed.Operation.Verb == FilmCompareWord => FilmCompareCommand(searchDir, parsed.Positionals[0],
                parsed.Text(Switches.A) ?? FilmRuns.RefPrev, parsed.Text(Switches.B) ?? FilmRuns.RefLatest,
                parsed.Flag(Switches.Paths)),
            "film" => Film(searchDir, parsed.Text(Switches.Modlist), tail, parsed.Flag(Switches.Test),
                parsed.Flag(Switches.Paths), parsed.Int(Switches.Fps) ?? FilmFlow.DefaultPathsFps,
                parsed.Flag(Switches.KeepFrames), parsed.Flag(Switches.Force),
                parsed.Int(Switches.TimeoutMinutes),
                QueuePolicy.Resolve(parsed.Int(Switches.QueueMinutes), line => Console.WriteLine(line)),
                parsed.Int(Switches.Roll), parsed.Text(Switches.Tag), parsed.Flag(Switches.Clean), parsed.Flag(Switches.Mute)),
            "build-save" => BuildSave(searchDir, parsed.Text(Switches.Modlist), parsed.Flag(Switches.Force),
                parsed.Int(Switches.TimeoutMinutes),
                QueuePolicy.Resolve(parsed.Int(Switches.QueueMinutes), line => Console.WriteLine(line))),
            "judge" => Judge(searchDir, parsed.Positionals.Count > 0 ? parsed.Positionals[0] : null,
                parsed.Flag(Switches.ExpectCurrent), parsed.Text(Switches.Case)),
            "live" => LiveSession.WithUnownedErrors(parsed.Flag(Switches.Json), () => parsed.Operation.Verb switch
            {
                "pawn" => LiveSession.Execute(searchDir, selectedNumber, tail,
                    parsed.Text(Switches.Stat), parsed.Flag(Switches.Hediff), null, null,
                    parsed.Flag(Switches.Json), null),
                "screenshot" => LiveSession.Execute(searchDir, selectedNumber, tail,
                    null, false, parsed.Text(Switches.Label), parsed.Text(Switches.Rect),
                    parsed.Flag(Switches.Json), null),
                "perf" => LiveSession.Execute(searchDir, selectedNumber, tail,
                    null, false, null, null,
                    parsed.Flag(Switches.Json), parsed.Text(Switches.Prefix)),
                "dev" => LiveSession.Dev(searchDir, selectedNumber, parsed.Positionals.Count > 0 ? parsed.Positionals[0] : null,
                    parsed.Text(Switches.Find), parsed.Flag(Switches.Json)),
                "do" or "until" when parsed.Positionals.Count == 0 => LiveSession.Actions(searchDir, selectedNumber,
                    parsed.Operation.Verb!, parsed.Flag(Switches.Json)),
                "do" or "until" => LiveSession.Drive(searchDir, selectedNumber, parsed.Operation.Verb!, parsed.Positionals[0],
                    LiveSession.MethodArgs(parsed.Positionals), parsed.Flag(Switches.Json), parsed.Int(Switches.LongEventSeconds)),
                "eval" => LiveSession.Eval(searchDir, selectedNumber, parsed.Positionals.Count > 0 ? parsed.Positionals[0] : null,
                    parsed.List(Switches.EvalFile) is [string file] ? file : null, parsed.Repeated(Switches.Using),
                    parsed.Flag(Switches.Json), parsed.Int(Switches.LongEventSeconds)),
                "status" or "cancel" => LiveSession.Follow(searchDir, selectedNumber, parsed.Operation.Verb!, parsed.Positionals[0],
                    parsed.Flag(Switches.Json)),
                _ => LiveSession.Execute(searchDir, selectedNumber, tail,
                    null, false, null, null, parsed.Flag(Switches.Json), null),
            }),
            _ => Usage($"未知命令：{command}"),
        };
    }

    internal const string HistoryMarkWord = "mark";

    private static (string ProductRoot, OrchestratorDescriptor Descriptor) Discover(string searchDir)
    {
        string? productRoot = OrchestratorDescriptor.FindProductRoot(searchDir) ?? throw new StoreException(
                $"在 {searchDir} 及其上级目录未找到 {OrchestratorDescriptor.FileName}");
        return OrchestratorDescriptor.Load(productRoot);
    }

    private static IEnumerable<string> ExcludeIdsFrom(OrchestratorDescriptor descriptor) =>
        (descriptor.DefaultExcluded ?? [])
            .Select(item => item.Id);

    private static int Stop(string searchDir, TreeSelector.Selection? tree, bool all, bool forceBusy)
    {
        if (all) return RunCoordinator.StopAll(forceBusy, line => Console.WriteLine(line));
        if (tree != null)
        {
            var store = new ClaimStore();
            if (tree.Claim is { } claim)
            {
                return RunCoordinator.Stop(store.Root, [claim], forceBusy,
                    $"实例 {tree.Label} 无运行中的游戏进程", line => Console.WriteLine(line));
            }

            return RunCoordinator.Stop(store.Root, store.FindByProductRoot(tree.ProductRoot), forceBusy,
                $"树 {tree.Label} 无运行中的实例", line => Console.WriteLine(line));
        }

        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        return new RunCoordinator(descriptor, productRoot, new ClaimStore())
            .Stop(forceBusy, line => Console.WriteLine(line));
    }

    private static int ShowcaseRosterPage(string searchDir)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        ArtifactGate.Output output = new ArtifactGate(descriptor, productRoot)
            .Check(line => Console.WriteLine(line), sources: false);
        Roster.Files files = Roster.Ensure(descriptor, productRoot, output, write: false);
        return ShowcaseRoster.Print(files.Showcases);
    }

    private static int Showcase(string searchDir, string? cliModList, IReadOnlyList<string> rest,
        bool forceBusy, int? timeoutMinutes, int? queueMinutes)
    {
        ShowcaseAction action = ShowcaseAction.Parse(rest);

        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        CliLog.Target("showcase", productRoot, descriptor);
        var store = new ClaimStore();
        switch (action.Kind)
        {
            case ShowcaseAction.Step:
                return ShowcaseClient.Step(ShowcaseNumber(store, productRoot), action.Argument!, timeoutMinutes,
                    Console.WriteLine);
            case ShowcaseAction.Teardown:
                return ShowcaseClient.Teardown(ShowcaseNumber(store, productRoot), timeoutMinutes, Console.WriteLine);
        }

        (ArtifactGate.Output output, Roster.Files rosters) = ColdStartInputs(productRoot, descriptor);
        string? modList = descriptor.ResolveModList(cliModList);
        ModListDefinition? definition = modList == null
            ? null
            : ModListDefinition.Load(Path.Combine(productRoot, descriptor.ModListsDir), modList);
        IReadOnlyList<string> selectionFaults = RosterSelection.CheckShowcase(action.Argument!, rosters.Showcases, definition);
        if (selectionFaults.Count > 0)
        {
            foreach (string fault in selectionFaults)
                Console.WriteLine("[FAIL] " + fault);
            return ExitCodes.Incomplete;
        }

        var coordinator = new RunCoordinator(descriptor, productRoot, store, purpose: ClaimRecord.PurposeShowcase,
            queueMinutes: QueuePolicy.Resolve(queueMinutes, line => Console.WriteLine(line)));
        int launched = coordinator.LaunchAsync(new Launch.Showcase(),
                new LaunchCommon(modList, forceBusy, timeoutMinutes,
                    line => Console.WriteLine(line)), output, rosters)
            .GetAwaiter().GetResult();
        if (launched != 0) return launched;
        return ShowcaseClient.Stage(ShowcaseNumber(store, productRoot), action.Argument!, timeoutMinutes,
            Console.WriteLine);
    }

    private static int ShowcaseNumber(ClaimStore store, string productRoot)
    {
        List<ClaimRecord> mine = store.FindByProductRoot(productRoot, ClaimRecord.PurposeShowcase);
        if (mine.Count == 0) throw new StoreException("本树没有展台登记");
        if (mine.Count > 1) throw new StoreException($"本树有 {mine.Count} 条展台登记，拒绝猜测");
        return mine[0].Number;
    }

    private static int Status(string searchDir, HashSet<int>? onlyNumbers, bool shardDetail)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        var store = new ClaimStore();
        List<ClaimRecord> claims = [.. store.FindByProductRoot(productRoot).Where(claim => onlyNumbers == null || onlyNumbers.Contains(claim.Number))];

        Console.WriteLine($"产品：{descriptor.Product}");
        Console.WriteLine($"产品根：{productRoot}");
        Console.WriteLine($"游戏：{descriptor.GameExe}");
        Console.WriteLine($"产出根：{descriptor.ResolveOutputDir(productRoot)}");
        Console.WriteLine($"名单目录：{CommandContext.FullPath(Path.Combine(productRoot, descriptor.ModListsDir))}");
        if (claims.Count == 0)
        {
            Console.WriteLine("本树尚无登记");
            MachineReport.PrintCoordinator();
            MachineReport.PrintMachineLockSummary(store.Root);
            PrintUnaccountedGames();
            return 0;
        }

        List<GameProcessProbe.GameProcess> processes = GameProcessProbe.Enumerate();
        MachineSnapshot? machine = MachineSnapshot.Read();
        List<AdmissionLedger.QueueEntry> queue = AdmissionLedger.ForClaimRoot(store.Root).LiveQueue();
        TreeView tree = TreeView.For(productRoot, claims)!;
        Console.WriteLine($"树：{tree.TreeName}"
                          + (tree.OnWorktree ? $"（worktree，主树 {tree.MainProductRoot}）" : "（主树）")
                          + $"  Mods：{tree.ModsFolder}");
        var firstShown = new Dictionary<(string Field, string Text), string>();
        string SameAsEarlier(ClaimRecord claim, string field, string text)
        {
            if (firstShown.TryGetValue((field, text), out string? segment))
                return $"同 {segment}";
            firstShown[(field, text)] = claim.PurposeSegment;
            return text;
        }

        bool shardsShown = false;
        foreach (ClaimRecord claim in tree.Slots)
        {
            if (!shardDetail && claim.PurposeKind == InstancePurpose.Shard)
            {
                if (shardsShown) continue;
                shardsShown = true;
                List<ShardStatusView.Slot> shardSlots =
                [
                    .. tree.Slots.Where(slot => slot.PurposeKind == InstancePurpose.Shard)
                        .Select(slot => ShardSlot(slot, processes, machine, queue, store.Root)),
                ];
                foreach (string line in ShardStatusView.Format(tree.TreeName, shardSlots, ShardGroupsLive.Of(productRoot),
                             number => ShardHold.AliveHolder(store.Root, number) != null, DateTimeOffset.Now))
                    Console.WriteLine(line);
                continue;
            }

            (string verdict, InstanceProbe.PingSnapshot? ping) = InstanceProbe.Detailed(claim.Number, processes);
            string? runState = ping?.RunState;
            ColdStartLock.Holder? starting = ColdStartLock.AliveHolder(store.Root, claim.Number);
            (string Short, string Long)? loadPhase = InstanceLiveness.LoadPhase(
                InstanceProbe.Facts(claim.Number, processes, machine), starting != null, DateTimeOffset.Now);
            bool appendix = loadPhase == null && verdict is "stop-idle" or "refuse";
            bool showcaseClaim = claim.PurposeKind == InstancePurpose.Showcase;
            (JsonDocument? progress, string? progressFault) = appendix && !showcaseClaim
                ? FetchProgress(claim.Number)
                : (null, null);
            using JsonDocument? progressScope = progress;
            if (progress != null
                && progress.RootElement.TryGetProperty(RimRunner.RimRunnerPipeProtocol.Keys.RunState, out JsonElement state)
                && state.ValueKind == JsonValueKind.String)
            {
                runState = state.GetString();
                int owned = processes.Count(process => InstanceProbe.IsOwnedAutoTestProcess(process, claim.Number));
                verdict = BusyGate.Verdict(owned, runState, forceBusy: false);
            }

            Console.WriteLine($"{claim.PurposeSegment}：登记 {claim.State}  packageIds="
                              + (claim.PackageIds.Count == 0 ? "[]"
                                  : SameAsEarlier(claim, "packageIds", $"[{string.Join(", ", claim.PackageIds)}]"))
                              + (PathIdentity.Same(claim.ModsFolder, tree.ModsFolder) ? "" : $"  Mods：{claim.ModsFolder}"));
            string? idleNote = verdict == "stop-idle" && ping?.RunState == runState
                ? InstanceLiveness.IdleNote(ping)
                : null;
            string liveness = verdict == "no-instance"
                ? InstanceLiveness.GoneLabel(IdleExit.StopNote(claim)) + RunCoordinator.InstanceGoneNote(claim)
                : InstanceLiveness.Describe(verdict, runState);
            Console.WriteLine($"  实例：{loadPhase?.Long ?? idleNote ?? liveness}");
            if (verdict != "no-instance" && PurposePolicies.IsAuto(claim.PurposeKind)
                && InstanceProbe.CurrentRunId(claim.Number) is { } currentRunId)
                Console.WriteLine($"  当前 runId：{currentRunId}");
            if (starting != null)
                Console.WriteLine($"  {InstanceLiveness.ColdStartPhase(claim.Number, starting, queue, DateTimeOffset.Now)}");
            if (ShardHold.AliveHolder(store.Root, claim.Number) is { } group)
                Console.WriteLine($"  {ColdStartLock.GroupHoldNote(group, DateTimeOffset.Now)}");
            if (ping?.Hold is { } hold)
                Console.WriteLine($"  {hold.Describe()}");
            if (ping is { Drives: > 0 } driven)
                Console.WriteLine($"  本实例已受理 {driven.Drives} 次 live 驱动，启动实例的命令不复用本实例");
            if (ping?.StartupFault != null)
                Console.WriteLine($"  启动故障：{ping.StartupFault}");
            bool ownedHere = InstanceLinks.Get(claim.Number) != null;
            if (ping != null)
                Console.WriteLine("  能力：" + (ownedHere
                    ? SameAsEarlier(claim, "能力", ReportPresentation.Capabilities(ping.ProductCapabilities))
                    : NotOwnedHere + "，未取"));
            Console.WriteLine($"  savedata：{claim.SaveData}");
            if (claim.LoadDirs is { Count: > 0 } loadDirs)
            {
                Console.WriteLine($"  载入目录：{SameAsEarlier(claim, "载入目录", string.Join(" ; ", loadDirs))}");
            }

            if (claim.Wrappers is { Count: > 0 } wrappers)
            {
                Console.WriteLine($"  旧形态载入目录（下次冷启动删除）：{string.Join(" ; ", wrappers)}");
            }

            if (!appendix) continue;
            if (showcaseClaim)
            {
                Console.WriteLine("  " + ShowcaseClient.FetchSummary(claim.Number));
                continue;
            }

            if (progress == null)
            {
                Console.WriteLine("游戏内：");
                Console.WriteLine("  " + progressFault);
                continue;
            }

            DllFreshnessResult dll = ownedHere
                ? DllFreshnessGate.Check(ping?.AssemblyMvids,
                    EngineArtifact.FreshnessSurface(descriptor, EnginePackage.Locate()),
                    productRoot)
                : new DllFreshnessResult { FlowError = NotOwnedHere };
            foreach (string line in LivePresentation.FormatProgress(progress.RootElement, dll, latestSegmentOnly: true))
                Console.WriteLine(line);
        }

        MachineReport.PrintCoordinator();
        MachineReport.PrintMachineLockSummary(store.Root);
        PrintUnaccountedGames(processes);
        return 0;
    }

    private static ShardStatusView.Slot ShardSlot(ClaimRecord claim, List<GameProcessProbe.GameProcess> processes,
        MachineSnapshot? machine, List<AdmissionLedger.QueueEntry> queue, string claimRoot)
    {
        (string verdict, InstanceProbe.PingSnapshot? ping) = InstanceProbe.Detailed(claim.Number, processes);
        ColdStartLock.Holder? starting = ColdStartLock.AliveHolder(claimRoot, claim.Number);
        (string Short, string Long)? loadPhase = InstanceLiveness.LoadPhase(
            InstanceProbe.Facts(claim.Number, processes, machine), starting != null, DateTimeOffset.Now);
        string liveness = loadPhase?.Short
                          ?? InstanceLiveness.IdleNote(ping)
                          ?? (verdict == "no-instance"
                              ? InstanceLiveness.GoneLabel(IdleExit.StopNote(claim))
                              : InstanceLiveness.ShortLabel(verdict));
        string?[] notes =
        [
            starting != null ? InstanceLiveness.ColdStartPhase(claim.Number, starting, queue, DateTimeOffset.Now) : null,
            ping?.StartupFault != null ? "启动故障：" + ping.StartupFault : null,
        ];
        string note = string.Join(" · ", notes.Where(text => text != null));
        return new ShardStatusView.Slot(claim, liveness, note.Length == 0 ? null : note);
    }

    private const string NotOwnedHere = "实例不在本编排进程名下";

    private static (JsonDocument? Progress, string? Fault) FetchProgress(int number)
    {
        bool local = InstanceLinks.Get(number) != null;
        InstanceLink.Reply reply = InstanceLinks.RequestLatestProgress(number);
        if (reply.Status == InstanceLink.Reply.NoLinkStatus) return (null, LiveSession.NoLinkReason(local));
        if (!reply.Ok) return (null, $"进度请求失败：{reply.ErrorText()}");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(reply.Body ?? "");
        }
        catch (JsonException)
        {
            return (null, "进度回包不是 JSON");
        }

        string? mismatch = LivePresentation.SchemaMismatch(
            doc.RootElement, RimRunner.RimRunnerLiveContract.Schema.Progress);
        if (mismatch == null) return (doc, null);
        doc.Dispose();
        return (null, mismatch);
    }

    private static void PrintUnaccountedGames(List<GameProcessProbe.GameProcess>? processes = null)
    {
        List<int> accountedNumbers;
        try
        {
            accountedNumbers = [.. new ClaimStore().All().Select(record => record.Number)];
        }
        catch (StoreException exception)
        {
            Console.WriteLine($"（无法读取登记库，无法判断机器上的实例是否已登记：{exception.Message}）");
            return;
        }

        if (processes == null)
        {
            try
            {
                processes = GameProcessProbe.Enumerate();
            }
            catch (StoreException exception)
            {
                Console.WriteLine($"（枚举 {GameProcessProbe.ProcessName} 失败：{exception.Message}）");
                return;
            }
        }

        var strays = processes
            .Where(process => !accountedNumbers.Any(number => GameProcessProbe.ClaimsNumber(
                process.CommandLine, RimRunner.RimRunnerEngineArgs.Instance, number)))
            .Where(process => process.CommandLine == null
                || GameProcessProbe.ClaimedNumber(
                    process.CommandLine, RimRunner.RimRunnerEngineArgs.Instance) != null)
            .ToList();
        if (strays.Count == 0) return;

        IEnumerable<string> described = strays.Select(process =>
            $"PID {process.Pid}" + (process.CommandLine == null ? "（命令行无法读取）" : ""));
        Console.WriteLine($"未登记的游戏进程（命令行带 RimRunner 实例参数而不在登记里，或命令行无法读取）：{string.Join("、", described)}");
    }

    private const string StuckPhase = "未完成加载";

    internal sealed record ListRow(ClaimRecord Record, string Instance, int Rank, string Suffix,
        ColdStartLock.Holder? Group = null, string? Idle = null, string? Liveness = null, string? Loading = null,
        bool ColdStarting = false, string? StopNote = null, LiveHold? Hold = null, int Drives = 0)
    {
        public bool Active => Rank < ListRank.None || Record.State != ClaimRecord.StateReady;
    }

    internal static class ListRank
    {
        public const int Running = 0;
        public const int Loading = 1;
        public const int Unresponsive = 2;
        public const int Idle = 3;
        public const int None = 4;
    }

    internal sealed record ListTree(TreeView Tree, List<ListRow> All, List<ListRow> Shown);

    internal sealed record ListGroup(string Product, List<ListTree> All, List<ListTree> Shown);

    internal static List<ListGroup> GroupForList(IEnumerable<ListRow> rows, bool expand, string? product)
    {
        List<ListRow> list = [.. rows];
        Dictionary<ClaimRecord, ListRow> byRecord = list.ToDictionary(row => row.Record);
        bool full = expand || product != null;
        return
        [
            .. TreeView.Of(list.Select(row => row.Record))
                .GroupBy(tree => tree.Product, StringComparer.OrdinalIgnoreCase)
                .Where(group => product == null || string.Equals(group.Key, product, StringComparison.OrdinalIgnoreCase))
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    List<ListTree> trees =
                    [
                        .. group.Select(tree =>
                        {
                            List<ListRow> all = [.. tree.Slots.Select(slot => byRecord[slot])];
                            return new ListTree(tree, all, full || all.Any(row => row.Active) ? all : []);
                        }),
                    ];
                    return new ListGroup(trees[0].Tree.Product, trees, full ? trees : [.. trees.Where(tree => tree.Shown.Count > 0)]);
                }),
        ];
    }

    private static int StatusAll(string? product, bool json)
    {
        var store = new ClaimStore();
        List<ClaimRecord> records = store.All();
        if (records.Count == 0)
        {
            if (json)
            {
                PrintStatusJson([]);
                return product != null ? 1 : 0;
            }

            if (product != null)
            {
                Console.WriteLine($"[FAIL] 登记里没有产品 {product}；登记库为空（{store.Root}）");
                return 1;
            }

            Console.WriteLine($"（无登记；登记库={store.Root}）");
            MachineReport.PrintCoordinator();
            MachineReport.PrintMachineLockSummary(store.Root);
            PrintUnaccountedGames();
            return 0;
        }

        if (product != null && !records.Any(record => string.Equals(record.Product, product, StringComparison.OrdinalIgnoreCase)))
        {
            if (json)
            {
                PrintStatusJson([]);
                return 1;
            }

            Console.WriteLine($"[FAIL] 登记里没有产品 {product}；已登记的产品："
                + string.Join("、", records.Select(record => record.Product).Distinct(StringComparer.OrdinalIgnoreCase)));
            return 1;
        }

        List<ClaimRecord> ordered = [.. records.OrderBy(record => record.Number)];
        List<GameProcessProbe.GameProcess> processes = GameProcessProbe.Enumerate();
        MachineSnapshot? machine = MachineSnapshot.Read();
        (string Verdict, InstanceProbe.PingSnapshot? Ping)[] liveness =
            [.. ordered.Select(record => InstanceProbe.Detailed(record.Number, processes))];
        List<AdmissionLedger.QueueEntry> queue = AdmissionLedger.ForClaimRoot(store.Root).LiveQueue();

        var rows = new List<ListRow>();
        for (int i = 0; i < ordered.Count; i++)
        {
            ClaimRecord record = ordered[i];
            ColdStartLock.Holder? starting = ColdStartLock.AliveHolder(store.Root, record.Number);
            (string Short, string Long)? phase = InstanceLiveness.LoadPhase(
                InstanceProbe.Facts(record.Number, processes, machine), starting != null, DateTimeOffset.Now);
            string verdict = liveness[i].Verdict;
            int rank = phase != null || starting != null ? ListRank.Loading : verdict switch
            {
                "refuse" => ListRank.Running,
                "stop-idle" => ListRank.Idle,
                "no-instance" => ListRank.None,
                _ => ListRank.Unresponsive,
            };
            ColdStartLock.Holder? group = ShardHold.AliveHolder(store.Root, record.Number);
            string? idle = phase == null && verdict == "stop-idle" ? InstanceLiveness.IdleNote(liveness[i].Ping) : null;
            string? stopNote = rank == ListRank.None ? IdleExit.StopNote(record) : null;
            LiveHold? hold = liveness[i].Ping?.Hold;
            string label = phase?.Short
                ?? (rank == ListRank.None ? InstanceLiveness.GoneLabel(stopNote) : InstanceLiveness.ShortLabel(verdict));
            string suffix = (stopNote == null ? "" : "  " + stopNote)
                + (starting == null ? "" : "  " + InstanceLiveness.ColdStartPhase(record.Number, starting, queue, DateTimeOffset.Now))
                + (group == null ? "" : "  " + ColdStartLock.GroupHoldNote(group, DateTimeOffset.Now))
                + (hold == null ? "" : "  " + hold.Describe())
                + (phase is { Short: StuckPhase } stuck ? "  " + stuck.Long : "");
            rows.Add(new ListRow(record, label, rank, suffix, group, idle,
                Liveness: verdict, Loading: phase?.Short, ColdStarting: starting != null, StopNote: stopNote,
                Hold: hold, Drives: liveness[i].Ping?.Drives ?? 0));
        }

        List<ListGroup> groups = GroupForList(rows, expand: false, product);
        if (json)
        {
            PrintStatusJson(groups);
            return 0;
        }

        int productWidth = groups.Max(group => group.Product.Length);
        foreach (ListGroup group in groups)
        {
            Console.WriteLine($"{group.Product.PadRight(productWidth)}  树 {group.All.Count}"
                + $" · 实例 {group.All.Sum(tree => tree.All.Count)}"
                + $" · 运行中 {group.All.Sum(tree => tree.All.Count(row => row.Rank < ListRank.None))}");
            foreach (ListTree tree in group.Shown)
            {
                Console.WriteLine($"  {tree.Tree.TreeName}  {tree.Tree.ProductRoot}");
                PrintListRows(tree.Shown);
            }
        }

        if (product == null && groups.Any(group => group.Shown.Count < group.All.Count))
            Console.WriteLine("没有运行中实例的树未列出，全列见 rimrunner status --product <产品名>");
        MachineReport.PrintCoordinator();
        MachineReport.PrintMachineLockSummary(store.Root);
        PrintUnaccountedGames(processes);
        return 0;
    }

    private static readonly JsonSerializerOptions StatusJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void PrintStatusJson(List<ListGroup> groups)
    {
        var payload = new
        {
            products = groups.Select(group => new
            {
                product = group.Product,
                trees = group.Shown.Select(tree => new
                {
                    tree = tree.Tree.TreeName,
                    productRoot = tree.Tree.ProductRoot,
                    instances = tree.Shown.Select(row => new
                    {
                        number = row.Record.Number,
                        purpose = row.Record.PurposeSegment,
                        registration = row.Record.State,
                        liveness = row.Liveness,
                        loading = row.Loading,
                        coldStarting = row.ColdStarting,
                        groupHeld = row.Group is { } held ? held.Label ?? "" : null,
                        stopNote = row.StopNote,
                        liveHold = row.Hold is { } hold ? new { until = hold.Until } : null,
                        drives = row.Drives,
                    }),
                }),
            }),
        };
        CliLog.Machine(() => Console.WriteLine(JsonSerializer.Serialize(payload, StatusJsonOptions)));
    }

    private static void PrintListRows(List<ListRow> rows)
    {
        List<ListRow> fold =
            [.. rows.Where(row => row.Record.PurposeKind == InstancePurpose.Shard && row.Record.State == ClaimRecord.StateReady)];
        bool folding = fold.Count > 1;
        var lines = new List<(string Segment, string Detail)>();
        foreach (ListRow row in rows)
        {
            if (folding && fold.Contains(row))
            {
                if (fold[0] == row)
                    lines.Add(($"{PurposePolicies.Shard.WireName} ×{fold.Count}",
                        $"实例 {string.Join(" · ", fold.GroupBy(r => r.Instance).Select(g => $"{g.Key} {g.Count()}"))}{GroupHeldNote(fold)}"));
                continue;
            }

            ClaimRecord record = row.Record;
            lines.Add((record.PurposeSegment,
                $"实例 {row.Idle ?? row.Instance}" + (record.State == ClaimRecord.StateReady ? "" : $"  登记 {record.State}") + row.Suffix));
        }

        int width = lines.Max(line => line.Segment.Length);
        foreach ((string segment, string detail) in lines)
            Console.WriteLine($"    {segment.PadRight(width)}  {detail}");
    }

    internal static string GroupHeldNote(List<ListRow> fold)
    {
        int held = fold.Count(row => row.Group != null);
        if (held == 0)
            return "";
        List<string> labels = [.. fold.Select(row => row.Group?.Label).OfType<string>().Distinct()];
        return $"  分片组持有 {held} 片" + (labels.Count == 0 ? "" : $"（{string.Join("、", labels)}）");
    }

    private static int Vram(string searchDir, IReadOnlyList<string> names)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        string modListsDir = CommandContext.FullPath(Path.Combine(productRoot, descriptor.ModListsDir));
        List<string> resolved = names.Count == 0
            ? [.. RimTooling.RimWorld.RimWorldModList.ListNames(modListsDir, ModListDefinition.LoadOptions)]
            : [.. names];
        return VramReport.ModLists(new ClaimStore().Root, productRoot, descriptor, resolved);
    }

    internal const int DefaultReportsLimit = 20;

    private static int SamplesConvert(string searchDir, SampleStore.Kind to)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        return SamplesCommand.Convert(productRoot, descriptor, to);
    }

    private static int Reports(string searchDir, bool onlyFailed, int limit)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        string reportsDir = descriptor.ResolveReportsDir(productRoot);

        var parsed = new List<RimRunner.RimRunnerArchiveParts>();
        int unrecognized = 0;

        void ScanDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return;

            foreach (string path in Directory.GetFiles(dir, "*.json"))
            {
                string name = Path.GetFileName(path);
                RimRunner.RimRunnerArchiveParts? parts = RimRunner.RimRunnerArchiveName.Parse(name);
                if (parts == null)
                {
                    unrecognized++;
                    continue;
                }

                parsed.Add(parts);
            }
        }

        ScanDirectory(reportsDir);
        string singleRoot = Path.Combine(
            reportsDir, RimRunner.RimRunnerDataLayout.SingleSubDirName);
        if (Directory.Exists(singleRoot))
        {
            foreach (string caseDir in Directory.GetDirectories(singleRoot))
            {
                ScanDirectory(caseDir);
            }
        }

        if (parsed.Count == 0)
        {
            Console.WriteLine("尚无归档");
            PrintReportsFooter(reportsDir, unrecognized);
            return 0;
        }

        Console.WriteLine($"归档：{CommandContext.FullPath(reportsDir)}");

        List<RimRunner.RimRunnerArchiveParts> rows =
        [
            .. parsed
                .OrderByDescending(parts => parts.Timestamp, StringComparer.Ordinal)
                .Where(parts => !onlyFailed || parts.Failed)
                .Take(limit)
        ];

        if (rows.Count == 0)
        {
            Console.WriteLine(onlyFailed
                ? "这些归档里没有判红的用例"
                : "没有命中的归档");
            PrintReportsFooter(reportsDir, unrecognized);
            return 0;
        }

        int countsWidth = rows.Where(parts => parts.HasCounts)
            .Select(parts => FormatArchiveCounts(parts).Length)
            .DefaultIfEmpty(0)
            .Max();
        int tagWidth = rows.Max(parts => TerminalWidth.Of(parts.BatchTag));
        int runIdWidth = rows.Max(parts => parts.RunId.Length);

        int verdictWidth = RimRunner.RimRunnerArchiveName.RejectedMark.Length;
        foreach (RimRunner.RimRunnerArchiveParts parts in rows)
        {
            string verdict = parts.Rejected
                ? RimRunner.RimRunnerArchiveName.RejectedMark
                : parts.Failed
                    ? RimRunner.RimRunnerArchiveName.FailedMark
                    : RimRunner.RimRunnerArchiveName.PassedMark;
            string counts = parts.HasCounts ? FormatArchiveCounts(parts) : "";
            Console.WriteLine(
                $"{parts.Timestamp}  {verdict.PadRight(verdictWidth)}  "
                + $"{counts.PadLeft(countsWidth)}  "
                + $"{PadDisplay(parts.BatchTag, tagWidth)}  "
                + $"{parts.RunId.PadRight(runIdWidth)}");
        }

        PrintReportsFooter(reportsDir, unrecognized);
        return 0;
    }

    private static string FormatArchiveCounts(RimRunner.RimRunnerArchiveParts parts) =>
        RimRunner.RimRunnerArchiveName.FormatCounts(
            parts.Fail, parts.UnclaimedFault, parts.SentinelFault, parts.EngineFault, parts.NoAssertions, parts.Incomplete);

    private static List<string> SingleArchives(string reportsDir, string caseId, int limit)
    {
        string dir = Path.Combine(reportsDir, RimRunner.RimRunnerDataLayout.SingleSubDirName, caseId);
        if (!Directory.Exists(dir)) return [];
        return
        [
            .. Directory.GetFiles(dir, "*.json")
                .Select(path => (Path: path, Parts: RimRunner.RimRunnerArchiveName.Parse(Path.GetFileName(path))))
                .Where(item => item.Parts != null)
                .OrderByDescending(item => item.Parts!.Timestamp, StringComparer.Ordinal)
                .Take(limit)
                .Select(item => item.Path)
        ];
    }

    private static void PrintReportsFooter(string reportsDir, int unrecognized)
    {
        Console.WriteLine(
            $"顶层轮换保留 {RimRunner.RimRunnerArchiveName.BatchKeepRecent} 份"
            + $"，判红的另轮换保留 {RimRunner.RimRunnerArchiveName.BatchKeepFailed} 份；"
            + $"single/<Id>/ 每条用例各 {RimRunner.RimRunnerArchiveName.SingleKeepRecent}"
            + $" + {RimRunner.RimRunnerArchiveName.SingleKeepFailed}"
            + $"；超过 {RimRunner.RimRunnerArchiveName.MaxAgeDays} 天的只保留最新的一份，已不在名册里的用例不保留");
        if (unrecognized > 0)
        {
            Console.WriteLine(
                $"另有 {unrecognized} 份文件名不是归档名的 JSON，未计入上表");
        }

        string[] elsewhere =
        [
            .. new[]
                {
                    (RimRunner.RimRunnerDataLayout.WorktreesSubDirName, "worktrees/"),
                    (ShardLayout.ShardsSubDirName, "shards/"),
                }
                .Where(name => Directory.Exists(Path.Combine(reportsDir, name.Item1)))
                .Select(name => name.Item2)
        ];
        if (elsewhere.Length > 0)
        {
            Console.WriteLine($"{string.Join("、", elsewhere)} 下另有归档，未计入上表");
        }

        Console.WriteLine($"RimRunner 日志：{Path.Combine(reportsDir, RimRunner.RimRunnerDataLayout.RimRunnerLogSubDirName)}"
            + $" 轮换保留最近 {CliLog.KeepCount} 份");
    }

    private static string PadDisplay(string text, int width) =>
        text + new string(' ', Math.Max(0, width - TerminalWidth.Of(text)));

    private static int History(string searchDir, string? cliModList, string? cliSave, IReadOnlyList<string> markCases,
        IReadOnlyList<string> queryCases)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        string? modList = descriptor.ResolveModList(cliModList);
        string? ledgerName = RimRunner.RimRunnerDataLayout.LedgerFileName(
            RimRunner.RimRunnerDataLayout.PassRateLedgerStem,
            string.IsNullOrWhiteSpace(cliSave)
                ? modList
                : RimRunner.RimRunnerDataLayout.SaveLedgerKey(modList, BaselineSave.PathOf(cliSave, descriptor, productRoot)));
        if (ledgerName == null)
            return Usage($"名单名 {modList} 含字母、数字与 - _ 以外的字符，引擎不为该名单写入台账", "history");

        string ledgerPath = Path.Combine(
            descriptor.ResolveOutputDir(productRoot), RimRunner.RimRunnerDataLayout.HistoryDirName, ledgerName);
        var reader = new PassRateLedger(ledgerPath);
        if (markCases.Count > 0)
        {
            return MarkEpoch(reader, ledgerPath, markCases);
        }

        void PrintSingleArchives(string caseId)
        {
            string dir = Path.Combine(descriptor.ResolveReportsDir(productRoot),
                RimRunner.RimRunnerDataLayout.SingleSubDirName, caseId);
            List<string> archives = SingleArchives(descriptor.ResolveReportsDir(productRoot), caseId, DefaultReportsLimit);
            if (archives.Count == 0) return;
            Console.WriteLine($"单条用例归档：{dir}");
            foreach (string path in archives)
                Console.WriteLine("  " + path);
        }

        string? mainLedgerPath = TreeScope.Of(productRoot).MainProductRoot is string mainProductRoot
            ? Path.Combine(descriptor.ResolveOutputDir(mainProductRoot), RimRunner.RimRunnerDataLayout.HistoryDirName, ledgerName)
            : null;
        if (mainLedgerPath != null && !File.Exists(mainLedgerPath)) mainLedgerPath = null;

        if (!File.Exists(ledgerPath) && mainLedgerPath == null)
        {
            if (modList != null && !File.Exists(Path.Combine(productRoot, descriptor.ModListsDir,
                    modList + RimTooling.RimWorld.RimWorldModList.Extension)))
                Console.WriteLine($"[FAIL] {Path.Combine(productRoot, descriptor.ModListsDir)} 里没有名单 {modList} 的文件，无对应的通过率台账");
            else
                Console.WriteLine($"[FAIL] 名单 {modList ?? "（无名单）"} 还没有通过率台账：{ledgerPath}");
            string[] existing = SiblingLedgers.Names(ledgerPath);
            Console.WriteLine(existing.Length > 0
                ? $"       同目录现有 {existing.Length} 份：{string.Join("、", existing)}"
                : "       同目录没有台账");
            foreach (string caseId in queryCases)
            {
                PrintOtherLedgers(ledgerPath, caseId);
                PrintSingleArchives(caseId);
            }

            return ExitCodes.Incomplete;
        }

        List<PassRateLedger.LedgerEntry> all = OverlayMain(
            mainLedgerPath == null ? [] : new PassRateLedger(mainLedgerPath).Read(),
            File.Exists(ledgerPath) ? reader.Read() : []);

        int PrintQuery(string? caseId)
        {
            List<PassRateLedger.LedgerEntry> entries = all;
            if (caseId != null)
            {
                if (!entries.Any(entry =>
                    string.Equals(entry.CaseId, caseId, StringComparison.Ordinal)))
                {
                    Console.WriteLine($"[FAIL] 台账里没有用例 {caseId}：{ledgerPath}"
                        + (mainLedgerPath != null ? $"（借读主树 {mainLedgerPath}）" : ""));
                    string[] near =
                    [
                        .. entries.Select(entry => entry.CaseId)
                            .Where(id => id.StartsWith(caseId, StringComparison.OrdinalIgnoreCase)
                                         || caseId.StartsWith(id, StringComparison.OrdinalIgnoreCase))
                            .Take(8)
                    ];
                    if (near.Length > 0)
                    {
                        Console.WriteLine($"       前缀相近的有：{string.Join("、", near)}");
                    }

                    PrintOtherLedgers(ledgerPath, caseId);
                    PrintSingleArchives(caseId);
                    return 1;
                }

                entries =
                [
                    .. entries.Where(entry =>
                        string.Equals(entry.CaseId, caseId, StringComparison.Ordinal))
                ];
            }

            Console.WriteLine($"台账：{ledgerPath}");
            if (mainLedgerPath != null)
                Console.WriteLine($"[借读] 主树：{mainLedgerPath}（条带里主树的格在前、本树的在后）");
            string[] siblings = SiblingLedgers.Names(ledgerPath);
            Console.WriteLine($"      名单 {DescribeModList(modList, cliModList)} · {DescribeDepth(all)}"
                + (caseId != null ? $" · 本次只列 {caseId}" : "")
                + (siblings.Length > 0
                    ? $" · 同目录另有 {siblings.Length} 份：{string.Join("、", siblings)}"
                    : ""));
            PrintLedgerDiffs(ledgerPath, all);
            int idWidth = entries.Count == 0 ? 0 : entries.Max(entry => entry.CaseId.Length);
            int liveWidth = entries.Count == 0 ? 1 : entries
                .Max(entry => PassRateLedger.EpochCount(entry.Outcomes).ToString(
                    CultureInfo.InvariantCulture).Length);
            foreach (PassRateLedger.LedgerEntry entry in entries)
            {
                string strip = OutcomeStrip(entry.Outcomes, caseId != null);
                string live = PassRateLedger.EpochCount(entry.Outcomes)
                    .ToString(CultureInfo.InvariantCulture);
                Console.WriteLine($"{entry.CaseId.PadRight(idWidth)}  "
                    + $"n={live.PadLeft(liveWidth)}  {strip}");
            }

            Console.WriteLine($"[done] {entries.Count} 条用例台账");
            if (caseId != null) PrintSingleArchives(caseId);
            return 0;
        }

        if (queryCases.Count == 0) return PrintQuery(null);

        int code = 0;
        for (int index = 0; index < queryCases.Count; index++)
        {
            if (index > 0) Console.WriteLine();
            code = ExitPolicy.Worst(code, PrintQuery(queryCases[index]));
        }

        return code;
    }

    internal static List<PassRateLedger.LedgerEntry> OverlayMain(
        List<PassRateLedger.LedgerEntry> main, List<PassRateLedger.LedgerEntry> own)
    {
        var merged = main
            .Select(entry => new PassRateLedger.LedgerEntry { CaseId = entry.CaseId, Outcomes = [.. entry.Outcomes] })
            .ToList();
        foreach (PassRateLedger.LedgerEntry entry in own)
        {
            PassRateLedger.LedgerEntry? hit = merged.FirstOrDefault(m =>
                string.Equals(m.CaseId, entry.CaseId, StringComparison.Ordinal));
            if (hit == null)
                merged.Add(new PassRateLedger.LedgerEntry { CaseId = entry.CaseId, Outcomes = [.. entry.Outcomes] });
            else
                hit.Outcomes.AddRange(entry.Outcomes);
        }

        return merged;
    }

    internal static char OutcomeGlyph(string outcome) => outcome switch
    {
        PassRateLedger.EpochMark => RimRunner.RimRunnerLedgerFold.EpochGlyph,
        PassRateLedger.SaveMark => RimRunner.RimRunnerLedgerFold.SaveGlyph,
        _ => RimRunner.RimRunnerOutcomes.GlyphOf(outcome),
    };

    internal const int StripWindow = 120;

    internal static string OutcomeStrip(IReadOnlyList<string> outcomes, bool full)
    {
        int start = full ? 0 : Math.Max(0, outcomes.Count - StripWindow);

        int epoch = outcomes.ToList().LastIndexOf(PassRateLedger.EpochMark);
        if (epoch >= 0 && epoch < start) start = epoch;

        var builder = new System.Text.StringBuilder(outcomes.Count - start + 1);
        if (start > 0) builder.Append('…');
        for (int i = start; i < outcomes.Count; i++) builder.Append(OutcomeGlyph(outcomes[i]));
        return builder.ToString();
    }

    private static void PrintLedgerDiffs(string ledgerPath, List<PassRateLedger.LedgerEntry> all)
    {
        string? dir = Path.GetDirectoryName(ledgerPath);
        if (dir == null || !Directory.Exists(dir)) return;
        var own = new HashSet<string>(all.Select(entry => entry.CaseId), StringComparer.Ordinal);
        foreach (string sibling in SiblingLedgers.Names(ledgerPath))
        {
            List<PassRateLedger.LedgerEntry> theirs;
            try
            {
                theirs = new PassRateLedger(Path.Combine(dir, sibling)).Read();
            }
            catch (StoreException)
            {
                continue;
            }

            var other = new HashSet<string>(theirs.Select(entry => entry.CaseId), StringComparer.Ordinal);
            int onlyOwn = own.Count(id => !other.Contains(id));
            int onlyOther = other.Count(id => !own.Contains(id));
            if (onlyOwn == 0 && onlyOther == 0) continue;
            Console.WriteLine($"      与 {sibling} 用例集合差 {onlyOwn + onlyOther} 条"
                + $"（本账独有 {onlyOwn} / 对方独有 {onlyOther}）"
                + (theirs.Count == 0
                    ? " · 对方无采样"
                    : $" · 对方 {DescribeDepth(theirs)}"));
        }
    }

    private static void PrintOtherLedgers(string ledgerPath, string caseId)
    {
        if (SiblingLedgers.OtherLedgersLine(ledgerPath, caseId) is string line)
            Console.WriteLine("       " + line);
    }

    internal static string DescribeModList(string? effective, string? cliModList) =>
        effective == null
            ? "无（继承本机 ModsConfig）"
            : cliModList != null ? effective : $"{effective}（描述符 defaultModList）";

    private static string DescribeDepth(List<PassRateLedger.LedgerEntry> entries)
    {
        if (entries.Count == 0) return "无采样";
        int[] depths = [.. entries.Select(entry => PassRateLedger.EpochCount(entry.Outcomes))];
        int low = depths.Min();
        int high = depths.Max();
        return $"{entries.Count} 条 · 采样 n={(low == high ? low.ToString() : $"{low}–{high}")}";
    }

    private static int MarkEpoch(PassRateLedger ledger, string ledgerPath, IReadOnlyList<string> caseIds)
    {
        List<PassRateLedger.LedgerEntry> before = [];
        Dictionary<string, int> behind = [];
        LedgerBook.Exclusive(ledgerPath, () =>
        {
            before = ledger.Read();
            behind = ledger.MarkEpoch(caseIds);
        });

        foreach (string caseId in caseIds)
        {
            PassRateLedger.LedgerEntry? target = before.FirstOrDefault(entry =>
                string.Equals(entry.CaseId, caseId, StringComparison.Ordinal));
            Console.WriteLine($"归入上一纪元 {caseId}  n={behind[caseId]}  "
                + string.Join(" ", target!.Outcomes.TakeLast(Math.Min(behind[caseId], 30))));
        }

        CliLog.Info($"台账：{ledgerPath}");
        Console.WriteLine($"[done] {caseIds.Count} 条用例的历史已归入上一纪元");
        return 0;
    }

    private static int RunBatch(string searchDir, string? cliModList, string? cliSave,
        IReadOnlyList<string> caseIds,
        bool includeExcluded, bool forceBusy, int? timeoutMinutes, int queueMinutes,
        bool envChecksOnly = false,
        bool noCompat = false, IReadOnlyList<string>? keepCompat = null, IReadOnlyList<string>? inputs = null,
        IReadOnlyList<string>? cliGroups = null)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        CliLog.Target("run", productRoot, descriptor);
        IReadOnlyList<RunInputs.Entry>? specifiedInputs = RunInputs.Resolve(inputs ?? [], CommandContext.FullPath,
            out string? inputProblem);
        if (specifiedInputs == null)
        {
            Console.WriteLine("[FAIL] " + inputProblem);
            return ExitCodes.Incomplete;
        }
        string? modList = descriptor.ResolveModList(cliModList);
        ModListDefinition? definition = modList == null
            ? null
            : ModListDefinition.Load(Path.Combine(productRoot, descriptor.ModListsDir), modList);
        BaselineSave? customSave = null;
        if (cliSave != null && BaselineSave.TryResolve(cliSave, descriptor, productRoot, out customSave) is string saveProblem)
        {
            Console.WriteLine("[FAIL] " + saveProblem);
            return ExitCodes.Incomplete;
        }

        (ArtifactGate.Output output, Roster.Files rosters) = ColdStartInputs(productRoot, descriptor);
        IReadOnlyList<string>? defaultGroups = CaseGroupPolicy.ModListGroups(definition);
        IReadOnlyList<string> selectionFaults =
        [
            .. RosterSelection.Check(caseIds, rosters.Cases, definition),
            .. RosterSelection.CheckExcluded(ExcludeIdsFrom(descriptor), rosters.Cases),
            .. cliGroups == null ? [] : CaseGroupPolicy.Check(cliGroups, "--group", rosters.Cases),
            .. defaultGroups == null
                ? []
                : CaseGroupPolicy.Check(defaultGroups,
                    $"mod 名单 {definition!.DirectiveSource(CaseGroupPolicy.DirectiveKey)} 的 [{ModListDefinition.ExtensionSection}].{CaseGroupPolicy.DirectiveKey}",
                    rosters.Cases),
        ];
        if (selectionFaults.Count > 0)
        {
            foreach (string fault in selectionFaults)
                Console.WriteLine("[FAIL] " + fault);
            return ExitCodes.Incomplete;
        }

        StructuredRunRequest request = StructuredRunRequest.Parse(caseIds,
            ExcludeIdsFrom(descriptor), includeExcluded, envChecksOnly, cliGroups, defaultGroups);
        if (CaseGroupPolicy.EmptyBatch(request, rosters.Cases) is string emptyBatch)
        {
            Console.WriteLine($"[拒绝/{ReportValidator.RejectionGateLabel(RimRunner.RimRunnerRejectionGate.Command)}] {emptyBatch}");
            return ExitCodes.Rejected;
        }

        AnnounceGroups(request, cliGroups, definition);
        if (specifiedInputs.Count > 0)
        {
            request = request.WithInputs(specifiedInputs);
            foreach (RunInputs.Entry input in specifiedInputs)
                Console.WriteLine($"[指定输入件] 指定 {input.Path} ← {input.Origin}");
        }

        static void announce(StructuredRunRequest batch, bool envChecksStripped) =>
            AnnounceBatch(batch, envChecksStripped);

        bool builtThisRun = false;
        string? saveOwner = definition?.SaveOwner;
        if (customSave != null)
        {
            Console.WriteLine($"[指定存档] 本批进 {customSave.SourcePath}；台账记在 "
                + (RimRunner.RimRunnerDataLayout.LedgerFileName(RimRunner.RimRunnerDataLayout.PassRateLedgerStem,
                    RimRunner.RimRunnerDataLayout.SaveLedgerKey(modList, customSave.SourcePath)) ?? "（不写台账文件）"));
        }
        else
        {
            switch (FirstSaveBuild.Judge(productRoot, descriptor, modList, saveOwner, out string savePath, out string? ledgerOwner))
            {
                case FirstSaveBuild.Verdict.Missing:
                    Console.WriteLine($"[FAIL] 基线档不存在：{savePath}");
                    Console.WriteLine($"       名单 {ledgerOwner ?? "（无名单）"} 已有台账，不自动建档");
                    return ExitCodes.Incomplete;
                case FirstSaveBuild.Verdict.FirstBuild:
                    Console.WriteLine($"[建档] 基线档不存在，名单 {modList ?? "（无名单）"} 尚无台账：先建档，再执行本批");
                    int built = BuildSave(productRoot, descriptor, saveOwner, forceBusy, timeoutMinutes, queueMinutes,
                        output, rosters);
                    if (built != 0)
                        return built;
                    builtThisRun = true;
                    break;
            }
        }

        int code = noCompat
            ? RunNoCompat(productRoot, descriptor, modList, request, keepCompat ?? [],
                forceBusy, timeoutMinutes, queueMinutes, announce, output, rosters)
            : LaunchBatch(productRoot, descriptor, modList, request,
                forceBusy, timeoutMinutes, queueMinutes, announce, output, rosters, customSave);
        if (builtThisRun)
            Console.WriteLine($"[建档] 本次命令新建的基线档：{descriptor.BaselineSaveNameFor(saveOwner)}.rws");
        return code;
    }

    private static int Start(string searchDir, string? cliModList, bool forceBusy,
        int? timeoutMinutes, int queueMinutes)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        CliLog.Target("start", productRoot, descriptor);
        string? modList = descriptor.ResolveModList(cliModList);

        (ArtifactGate.Output output, Roster.Files rosters) = ColdStartInputs(productRoot, descriptor);
        var coordinator = new RunCoordinator(descriptor, productRoot, new ClaimStore(), queueMinutes: queueMinutes);
        Console.WriteLine("[本批] 只启动游戏进档，不执行用例");
        return coordinator.LaunchAsync(new Launch.Warm(),
                new LaunchCommon(modList, forceBusy, timeoutMinutes, line => Console.WriteLine(line)),
                output, rosters)
            .GetAwaiter().GetResult();
    }

    private static int StartShowcase(string searchDir, string? cliModList, bool forceBusy,
        int? timeoutMinutes, int queueMinutes)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        CliLog.Target("start-showcase", productRoot, descriptor);
        string? modList = descriptor.ResolveModList(cliModList);

        (ArtifactGate.Output output, Roster.Files rosters) = ColdStartInputs(productRoot, descriptor);
        var coordinator = new RunCoordinator(descriptor, productRoot, new ClaimStore(),
            purpose: ClaimRecord.PurposeShowcase, queueMinutes: queueMinutes);
        return coordinator.LaunchAsync(new Launch.Showcase(),
                new LaunchCommon(modList, forceBusy, timeoutMinutes, line => Console.WriteLine(line)),
                output, rosters)
            .GetAwaiter().GetResult();
    }

    private static int LaunchBatch(string productRoot, OrchestratorDescriptor descriptor,
        string? modList, StructuredRunRequest request,
        bool forceBusy, int? timeoutMinutes, int queueMinutes,
        Action<StructuredRunRequest, bool> announce, ArtifactGate.Output output, Roster.Files rosters,
        BaselineSave? customSave)
    {
        if (ShardPlan.Target(request, rosters.Cases) is string shardCase)
        {
            return new ShardCoordinator(descriptor, productRoot, new ClaimStore(), line => Console.WriteLine(line),
                    customSave)
                .RunAsync(request, shardCase, modList, forceBusy, timeoutMinutes, queueMinutes, default)
                .GetAwaiter().GetResult();
        }

        var coordinator = new RunCoordinator(descriptor, productRoot, new ClaimStore(), queueMinutes: queueMinutes);
        return coordinator.LaunchAsync(new Launch.Batch(request, announce),
                new LaunchCommon(modList, forceBusy, timeoutMinutes, line => Console.WriteLine(line),
                    Save: customSave),
                output, rosters)
            .GetAwaiter().GetResult();
    }

    private static int RunNoCompat(string productRoot, OrchestratorDescriptor descriptor,
        string? modList, StructuredRunRequest request, IReadOnlyList<string> keep,
        bool forceBusy, int? timeoutMinutes, int queueMinutes,
        Action<StructuredRunRequest, bool> announce, ArtifactGate.Output output, Roster.Files rosters)
    {
        string configPath = CompatMirror.RuntimeConfigPath(descriptor.GameExe);
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"[FAIL] 游戏目录里没有 runtimeconfig：{configPath}");
            return ExitCodes.Incomplete;
        }
        if (CompatMirror.Select(File.ReadAllText(configPath), keep, out CompatMirror.Selection? selection) is string problem)
            return Usage(problem, "run");
        Console.WriteLine($"[兼容关闭] {selection!.Summary}；本轮不计台账");

        var store = new ClaimStore();
        using CompatMirror.Use use = CompatMirror.Ensure(descriptor.GameExe, selection, store, line => Console.WriteLine(line));
        OrchestratorDescriptor mirrored = descriptor.WithGameExe(use.Exe);
        IReadOnlyList<string> modIds = modList == null
            ? []
            : ModListDefinition.Load(Path.Combine(productRoot, descriptor.ModListsDir), modList).ModIds;
        CompatMirror.LinkForeignMods(descriptor.ModsFolder, mirrored.ModsFolder, modIds, productRoot,
            line => Console.WriteLine(line));

        return new RunCoordinator(mirrored, productRoot, store, purpose: ClaimRecord.PurposeNoCompat,
                queueMinutes: queueMinutes)
            .LaunchAsync(new Launch.NoCompat(request, selection.Arg, announce),
                new LaunchCommon(modList, forceBusy, timeoutMinutes, line => Console.WriteLine(line)),
                output, rosters)
            .GetAwaiter().GetResult();
    }

    private static int BuildSave(string searchDir, string? cliModList, bool forceBusy,
        int? timeoutMinutes, int queueMinutes)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        CliLog.Target("build-save", productRoot, descriptor);
        (ArtifactGate.Output output, Roster.Files rosters) = ColdStartInputs(productRoot, descriptor);
        return BuildSave(productRoot, descriptor, cliModList, forceBusy, timeoutMinutes, queueMinutes,
            output, rosters);
    }

    private static int BuildSave(string productRoot, OrchestratorDescriptor descriptor, string? cliModList,
        bool forceBusy, int? timeoutMinutes, int queueMinutes, ArtifactGate.Output output, Roster.Files rosters)
    {
        string? modList = descriptor.ResolveModList(cliModList);
        var coordinator = new RunCoordinator(descriptor, productRoot, new ClaimStore(), queueMinutes: queueMinutes);
        return coordinator.LaunchAsync(new Launch.BuildSave(),
                new LaunchCommon(modList, forceBusy, timeoutMinutes, line => Console.WriteLine(line)),
                output, rosters)
            .GetAwaiter().GetResult();
    }

    internal const string FilmCompareWord = "compare";

    private static int FilmCompareCommand(string searchDir, string selector, string a, string b, bool paths)
    {
        FilmFlow.FilmMode mode = paths ? FilmFlow.FilmMode.Paths : FilmFlow.FilmMode.Shots;
        FilmFlow.Target target = FilmFlow.ParseTargets([selector], mode, out string? selectorError) is [var only]
            ? only
            : throw new UnreachableException(selectorError);
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        string? ffmpeg = FilmFlow.LocateFfmpeg(out string where);
        if (ffmpeg == null)
        {
            Console.WriteLine(FilmFlow.MissingFfmpegLine(where));
            return ExitCodes.Incomplete;
        }

        return FilmCompare.Execute(
            Path.Combine(descriptor.ResolveOutputDir(productRoot), RimRunner.RimRunnerDataLayout.FilmDirName), target,
            a, b, paths, ffmpeg, RimRunnerConfig.Machine().Film, line => Console.WriteLine(line));
    }

    private static int Film(string searchDir, string? cliModList, IReadOnlyList<string> selectors, bool test,
        bool paths, int pathsFps, bool keepFrames, bool forceBusy, int? timeoutMinutes, int queueMinutes,
        int? roll = null, string? tag = null, bool clean = false, bool mute = false)
    {
        FilmFlow.FilmMode mode = paths ? FilmFlow.FilmMode.Paths
            : test ? FilmFlow.FilmMode.Test
            : FilmFlow.FilmMode.Shots;
        List<FilmFlow.Target> targets = FilmFlow.ParseTargets(selectors, mode, out string? selectorError)
            ?? throw new UnreachableException(selectorError);

        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);
        CliLog.Target("film", productRoot, descriptor);
        (ArtifactGate.Output output, Roster.Files rosters) = ColdStartInputs(productRoot, descriptor);
        string? ffmpeg = null;
        if (mode != FilmFlow.FilmMode.Test)
        {
            ffmpeg = FilmFlow.LocateFfmpeg(out string where);
            if (ffmpeg == null)
            {
                Console.WriteLine(FilmFlow.MissingFfmpegLine(where));
                return ExitCodes.Incomplete;
            }
        }

        string? modList = descriptor.ResolveModList(cliModList);
        var coordinator = new RunCoordinator(descriptor, productRoot, new ClaimStore(), purpose: ClaimRecord.PurposeFilm,
            queueMinutes: queueMinutes);
        return coordinator.LaunchAsync(
                new Launch.Film(new FilmFlow.Options(targets, keepFrames, mode, timeoutMinutes, ffmpeg, pathsFps, roll,
                    tag, clean, RimRunnerConfig.Machine().Film, mute)),
                new LaunchCommon(modList, forceBusy, timeoutMinutes, line => Console.WriteLine(line)),
                output, rosters)
            .GetAwaiter().GetResult();
    }

    private const string BatchHead = "[本批] ";

    private static void AnnounceGroups(StructuredRunRequest request, IReadOnlyList<string>? cliGroups,
        ModListDefinition? modList)
    {
        if (request.Groups.Count == 0)
        {
            if (cliGroups != null && modList?.DirectiveList(CaseGroupPolicy.DirectiveKey) != null)
                Console.WriteLine($"[按组] --group {RimRunner.RimRunnerCaseGroups.AllToken}：不套用 mod 名单 "
                                  + $"{modList.DirectiveSource(CaseGroupPolicy.DirectiveKey)} 的 [{ModListDefinition.ExtensionSection}].{CaseGroupPolicy.DirectiveKey}，不按组过滤");
            return;
        }

        string origin = cliGroups != null
            ? "--group"
            : $"mod 名单 {modList!.DirectiveSource(CaseGroupPolicy.DirectiveKey)} 的 [{ModListDefinition.ExtensionSection}].{CaseGroupPolicy.DirectiveKey}";
        Console.WriteLine($"[按组] 批部分只选组 {string.Join("、", request.Groups)} 的产品用例（{origin}）；引擎随包检查照常执行");
    }

    private static void AnnounceBatch(StructuredRunRequest request, bool envChecksStripped)
    {
        List<string> logOnly = [];
        Console.WriteLine(BatchHead + DescribeBatch(request, envChecksStripped, logOnly));
        foreach (string line in logOnly)
            CliLog.Info(line);
    }

    private static string DescribeBatch(StructuredRunRequest request, bool envChecksStripped, List<string> logOnly)
    {
        string tail = (envChecksStripped ? "，不含环境体检（本进程已通过环境体检）" : "")
            + (request.ExcludeIds.Count > 0 ? $"，排除名单 {request.ExcludeIds.Count} 条用例" : "");

        var text = new System.Text.StringBuilder();
        if (request.IsNamed)
            text.Append(FindingPresentation.ListLine("指定 ", [.. request.CaseIds], "、", logOnly, "条", logHead: BatchHead + "指定 ",
                maxChars: FindingPresentation.ScreenLineChars - BatchHead.Length - tail.Length));
        else
        {
            text.Append(request.EnvChecks == StructuredRunRequest.EnvCheckMode.Only ? "只执行环境体检" : "回归用例");
            if (request.Groups.Count > 0)
                text.Append("（组 " + string.Join("、", request.Groups) + "）");
        }

        return text.Append(tail).ToString();
    }

    private static string ReadCommandFromReport(string path)
    {
        if (!File.Exists(path)) return "";
        try
        {
            using JsonDocument probe = StrictJson.ParseObject(File.ReadAllText(path));
            return probe.RootElement.TryGetProperty("command", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return "";
        }
    }

    private static int Judge(
        string searchDir, string? reportPath, bool expectCurrent, string? caseId)
    {
        (string productRoot, OrchestratorDescriptor descriptor) = Discover(searchDir);

        reportPath ??= Path.Combine(descriptor.ResolveReportsDir(productRoot), "latest.json");

        if (JudgeTarget.IsRunId(reportPath))
        {
            string reportsDir = descriptor.ResolveReportsDir(productRoot);
            IReadOnlyList<string> found = JudgeTarget.FindByRunId(reportsDir, reportPath);
            if (found.Count != 1)
            {
                Console.WriteLine(found.Count == 0
                    ? $"[FAIL] {reportsDir} 下没有 runId 为 {reportPath} 的批次归档"
                    : $"[FAIL] {reportsDir} 下有 {found.Count} 份 runId 为 {reportPath} 的批次归档，指定其中一份的路径："
                      + string.Concat(found.Select(path => $"{Environment.NewLine}  {path}")));
                return ExitCodes.Incomplete;
            }

            reportPath = found[0];
        }

        if (Directory.Exists(reportPath))
            return JudgeGroup(reportPath, expectCurrent, caseId);

        ReportValidationResult result = ValidateOnDisk(reportPath);

        if (caseId != null && result.Report != null)
        {
            (IReadOnlyList<ReportLine> caseLines, bool attention) = ReportPresentation.FormatCase(result.Report, caseId,
                SiblingLedgers.ForReport(descriptor, productRoot, result.Report));
            ReportPrinter.Write(caseLines, Console.WriteLine);
            return attention ? ExitCodes.EmptyOrFindings : ExitCodes.Ok;
        }

        if (result.Valid && result.Report != null)
        {
            ReportFreshnessNote freshness = ReportFreshness.Describe(
                result.Report,
                EngineArtifact.FreshnessSurface(descriptor, EnginePackage.Locate()),
                productRoot,
                ContentFingerprint.Compute(descriptor, productRoot, result.Report.ModList,
                    CommandContext.GetEnvironmentVariable, result.Report.CustomSave));
            ReportPrinter.Write(
                ReportPresentation.FormatTiered(result.Report, result.ExitCode, freshness,
                    otherLedgers: SiblingLedgers.ForReport(descriptor, productRoot, result.Report),
                    hasOutputFaults: result.OutputFaults.Count > 0),
                Console.WriteLine);

            if (expectCurrent && freshness.Verdict != ReportFreshnessVerdict.Current)
            {
                Console.WriteLine("[FAIL] --expect-current：无法确认这份报告是当前代码的结论");
                return ExitCodes.Incomplete;
            }
        }
        else
        {
            Console.WriteLine(result.Message);
        }

        ReportPrinter.Write(ReportPresentation.OutputFaultLines(result.OutputFaults), Console.WriteLine);
        return result.ExitCode;
    }

    private static ReportValidationResult ValidateOnDisk(string reportPath)
    {
        IReadOnlyList<string> expectedSections;
        string reportCommand = ReadCommandFromReport(reportPath);
        try
        {
            expectedSections = StructuredRunRequest.ExpectedSectionsFromCommand(reportCommand);
        }
        catch (ArgumentException exception)
        {
            ReportValidationResult fileLevel = ReportValidator.Validate(reportPath, null, null, null);
            if (!fileLevel.Valid)
                return fileLevel with { ReportPath = reportPath };

            return new ReportValidationResult { ExitCode = ExitCodes.Incomplete, Message = exception.Message, ReportPath = reportPath };
        }

        return ReportValidator.Validate(reportPath, null, null, expectedSections) with { ReportPath = reportPath };
    }

    private static int JudgeGroup(string groupDir, bool expectCurrent, string? caseId)
    {
        if (expectCurrent)
        {
            Console.WriteLine("[FAIL] --expect-current 只核单份报告，组目录的各片报告不受理");
            return ExitCodes.Incomplete;
        }

        string manifestPath = Path.Combine(groupDir, ShardLayout.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine($"[FAIL] 目录里没有分片组清单：{manifestPath}");
            return ExitCodes.Incomplete;
        }

        ShardGroupManifest manifest;
        try
        {
            manifest = ShardGroupManifest.Load(manifestPath);
        }
        catch (StoreException exception)
        {
            Console.WriteLine("[FAIL] " + exception.Message);
            return ExitCodes.Incomplete;
        }

        if (manifest.Schema != ShardGroupManifest.CurrentSchema)
        {
            string actual = manifest.Schema?.ToString() ?? "旧版（无 schema）";
            Console.WriteLine($"[FAIL] 分片组清单格式 {actual}，本 CLI 读格式 {ShardGroupManifest.CurrentSchema}：{manifestPath}");
            return ExitCodes.Incomplete;
        }

        if (manifest.Claims == null)
        {
            Console.WriteLine($"[FAIL] 分片组 {manifest.Group} 未执行到收尾，组清单没有认领记录：{manifestPath}");
            return ExitCodes.Incomplete;
        }

        List<ShardGroupView.Slice> slices = ShardGroupView.SlicesOf(manifest, shard => shard.Report == null
            ? null
            : ValidateOnDisk(Path.Combine(groupDir, shard.Report)));
        ShardGroupView.Facts facts = ShardGroupView.FactsOf(manifest);

        if (caseId != null)
        {
            (IReadOnlyList<ReportLine> caseLines, bool attention) = ShardGroupView.FormatCase(slices, facts, caseId);
            ReportPrinter.Write(caseLines, Console.WriteLine);
            return attention ? ExitCodes.EmptyOrFindings : ExitCodes.Ok;
        }

        (IReadOnlyList<ReportLine> lines, int exitCode) = ShardGroupView.Format(slices, facts, archived: true);
        ReportPrinter.Write(lines, Console.WriteLine);
        return exitCode;
    }

    private static int Unmount(string searchDir, TreeSelector.Selection? tree, bool all, bool forceBusy)
    {
        var store = new ClaimStore();
        List<ClaimRecord> targets;
        if (all)
        {
            targets =
            [
                .. store.All()
                    .Where(record => !record.MainTreeResident)
                    .OrderBy(record => record.Number)
            ];
            if (targets.Count == 0)
            {
                Console.WriteLine("当前没有可卸载的登记");
                return 0;
            }
        }
        else
        {
            string productRoot = tree?.ProductRoot ?? Discover(searchDir).ProductRoot;
            List<ClaimRecord> mine = store.FindByProductRoot(productRoot);
            if (mine.Count == 0)
            {
                Console.WriteLine(tree == null ? "本树尚无登记" : $"{tree.Label} 尚无登记");
                AwaitTreeHosts([productRoot]);
                return 0;
            }

            ClaimRecord? resident = mine.FirstOrDefault(record =>
                record.MainTreeResident && !UnmountFlow.ProductRootGone(record.ProductRoot));
            if (resident != null)
            {
                Console.WriteLine($"[FAIL] 实例 {resident.DisplayName} 是主树的常驻登记，不随 unmount 卸载");
                return ExitCodes.Incomplete;
            }

            targets = [.. mine.OrderBy(record => record.Number)];
        }

        var descriptors = new Dictionary<int, (OrchestratorDescriptor Descriptor, string Root)?>();
        foreach (ClaimRecord target in targets)
        {
            (OrchestratorDescriptor Descriptor, string Root)? descriptor = UnmountFlow.SourceOf(target, searchDir);
            if (descriptor == null && !UnmountFlow.DescriptorOptional(target))
            {
                Console.WriteLine($"[FAIL] 实例 {target.DisplayName} 的登记（{target.Product}，{target.ProductRoot}）"
                                  + "未找到同产品的编排描述符，无法卸载");
                return ExitCodes.Incomplete;
            }

            descriptors[target.Number] = descriptor;
        }

        RunCoordinator.StopOutcome stop = RunCoordinator.EnsureStopped(store.Root, targets, forceBusy,
            line => Console.WriteLine(line));
        if (stop.Verdict is int stopFailure) return stopFailure;

        int failed = 0;
        foreach (ClaimRecord target in targets)
        {
            UnmountFlow flow = UnmountFlow.For(descriptors[target.Number], store, RecordBuilder.SaveDataRoot);
            if (!flow.Unmount(target, forceBusy, line => Console.WriteLine(line)))
            {
                failed++;
            }
        }

        if (failed > 0)
        {
            Console.WriteLine($"[结果] {failed} 个未卸载");
            return ExitCodes.Incomplete;
        }

        Console.WriteLine($"[结果] 已卸载 {targets.Count} 个");
        AwaitTreeHosts(targets.Select(target => target.ProductRoot));
        return 0;
    }

    private static void AwaitTreeHosts(IEnumerable<string> productRoots)
    {
        List<string> trees =
        [
            .. productRoots
                .Select(root => MainWorktree.GitTreeRoot(root))
                .OfType<string>()
                .Where(TreeHosts.Unclaimed)
        ];
        TreeHosts.AwaitExit(trees, Console.Out);
    }

    private static int Usage(string problem, string? topic = null)
    {
        Console.WriteLine($"[FAIL] {problem}");
        Console.WriteLine();
        if (topic != null && CommandHelp.IsCommand(topic.Split(' ')[0]))
        {
            CommandHelp.Print(topic, exitNotes: false);
        }
        else
        {
            CommandHelp.PrintOverview();
        }

        return ExitCodes.Usage;
    }

    private static void PrintVersion()
    {
        Console.WriteLine($"rimrunner {typeof(Program).Assembly.GetName().Version?.ToString(3)}");

        string? exe = Environment.ProcessPath;
        if (exe == null)
        {
            Console.WriteLine("可执行文件 未知（宿主未报告 ProcessPath）");
            return;
        }

        string built = File.Exists(exe)
            ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm:ss")
            : "文件已不在原处";
        Console.WriteLine($"可执行文件 {exe}");
        Console.WriteLine($"构建时间   {built}");

        EnginePackage package = EnginePackage.Locate();
        Console.WriteLine($"安装目录   {package.Root}");
        try
        {
            PackageManifest manifest = package.ReadManifest();
            Console.WriteLine($"           commit {manifest.Commit}");
            Console.WriteLine($"           built  {manifest.Built}");
            Console.WriteLine($"           类别   {(manifest.Edition == "release" ? "发布版" : "维护者版")}");
            Console.WriteLine($"           游戏   {manifest.Game.Version}");
        }
        catch (StoreException exception)
        {
            Console.WriteLine($"           {exception.Message}");
        }
    }
}
