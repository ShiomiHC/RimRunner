using System.IO;
using System.Text.Json;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    internal static LiveResponse Handle(JsonElement request)
    {
        string? op = request.TryGetProperty(RimRunnerPipeProtocol.Op, out JsonElement opElement)
                     && opElement.ValueKind == JsonValueKind.String
            ? opElement.GetString()
            : null;
        try
        {
            var p = new RimRunnerRequestParams(request);
            return op switch
            {
                RimRunnerPipeProtocol.Ops.Run => RunRequest(p),
                RimRunnerPipeProtocol.Ops.ShardCells => ShardCellsRequest(p),
                RimRunnerPipeProtocol.Ops.Progress => QueryDiagnosticsRequest(p),
                RimRunnerPipeProtocol.Ops.Save => SaveRequest(p),
                RimRunnerPipeProtocol.Ops.Pawns => QueryPawnsRequest(p),
                RimRunnerPipeProtocol.Ops.Gizmos => QueryGizmosRequest(p),
                RimRunnerPipeProtocol.Ops.Menu => QueryMenuRequest(p),
                RimRunnerPipeProtocol.Ops.Windows => QueryWindowsRequest(p),
                RimRunnerPipeProtocol.Ops.Dev => QueryDevRequest(p),
                RimRunnerPipeProtocol.Ops.Stat => QueryStatRequest(p),
                RimRunnerPipeProtocol.Ops.Hediffs => QueryHediffsRequest(p),
                RimRunnerPipeProtocol.Ops.Screenshot => QueueScreenshotRequest(p),
                RimRunnerPipeProtocol.Ops.Profiler => ProfilerRequest(p),
                RimRunnerPipeProtocol.Ops.Showcase => ShowcaseStatusRequest(p),
                RimRunnerPipeProtocol.Ops.ShowcaseStage => ShowcaseStageRequest(p),
                RimRunnerPipeProtocol.Ops.ShowcaseStep => ShowcaseStepRequest(p),
                RimRunnerPipeProtocol.Ops.ShowcaseTeardown => ShowcaseTeardownRequest(p),
                RimRunnerPipeProtocol.Ops.FilmRoster => FilmRosterRequest(p),
                RimRunnerPipeProtocol.Ops.FilmShot => FilmShotRequest(p),
                RimRunnerPipeProtocol.Ops.FilmStage => FilmStageRequest(p),
                RimRunnerPipeProtocol.Ops.FilmPaths => FilmPathsRequest(p),
                RimRunnerPipeProtocol.Ops.FilmEnd => FilmEndRequest(p),
                RimRunnerPipeProtocol.Ops.Drive => DriveRequest(p),
                RimRunnerPipeProtocol.Ops.DriveStatus => DriveStatusRequest(p),
                RimRunnerPipeProtocol.Ops.DriveCancel => DriveCancelRequest(p),
                RimRunnerPipeProtocol.Ops.Actions => ActionsRequest(p),
                RimRunnerPipeProtocol.Ops.Assemblies => AssembliesRequest(p),
                RimRunnerPipeProtocol.Ops.UnownedErrors => UnownedErrorsRequest(p),
                _ => ErrorResponse(404, "未知请求：" + (op ?? "<缺 " + RimRunnerPipeProtocol.Op + ">")),
            };
        }
        catch (RimRunnerRequestException exception)
        {
            return ErrorResponse(exception.Status, exception.Message);
        }
    }

    internal static LiveResponse Error(int statusCode, string message) => ErrorResponse(statusCode, message);

    private static LiveResponse UnownedErrorsRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly();
        RimRunnerUnownedErrors.Snapshot snapshot = RimRunnerUnownedErrors.Take();
        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.UnownedErrors, writer =>
        {
            writer.WriteNumber(RimRunnerLiveContract.UnownedErrors.Count, snapshot.Count);
            writer.WriteStartArray(RimRunnerLiveContract.UnownedErrors.Heads);
            foreach ((string head, int times) in snapshot.Heads)
            {
                writer.WriteStartObject();
                writer.WriteString(RimRunnerLiveContract.UnownedErrors.Head, head);
                writer.WriteNumber(RimRunnerLiveContract.UnownedErrors.Times, times);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (snapshot.Sustained != null)
            {
                writer.WriteString(RimRunnerLiveContract.UnownedErrors.Sustained, snapshot.Sustained);
            }
        }));
    }

    internal static void WriteHelloMembers(Utf8JsonWriter writer)
    {
        writer.WriteNumber(RimRunnerPipeProtocol.Keys.Pid, Environment.ProcessId);
        writer.WriteString(RimRunnerPipeProtocol.Keys.Mode, RimRunnerProcessMode.Current);
        writer.WriteStartObject(RimRunnerPipeProtocol.Keys.AssemblyMvids);
        foreach (KeyValuePair<string, string> pair in RimRunnerDefScope.OurAssemblyMvids())
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
        IReadOnlyList<string>? capabilityNames = RimRunnerProduct.TryGetCapabilities()?.Names;
        if (capabilityNames == null)
        {
            writer.WriteNull(RimRunnerPipeProtocol.Keys.ProductCapabilities);
        }
        else
        {
            AppendStrings(writer, RimRunnerPipeProtocol.Keys.ProductCapabilities, capabilityNames);
        }

        AppendLoadedPackIds(writer);
        writer.WriteStartObject(RimRunnerPipeProtocol.Keys.ModAssemblyShortfalls);
        foreach ((string packageId, string text) in RimRunnerModAssemblies.Shortfalls)
        {
            writer.WriteString(packageId, text);
        }

        writer.WriteEndObject();
    }

    internal static void WriteStateObject(Utf8JsonWriter writer)
    {
        RimRunnerRunSnapshot run = RimRunnerRunCoordinator.GetSnapshot();
        writer.WriteStartObject();
        writer.WriteBoolean(RimRunnerPipeProtocol.Keys.InGame, Current.ProgramState == ProgramState.Playing);
        writer.WriteString(RimRunnerPipeProtocol.Keys.RunState, run.State.ToString().ToLowerInvariant());
        writer.WriteString(RimRunnerPipeProtocol.Keys.ActiveCommand, run.Command);
        writer.WriteString(RimRunnerPipeProtocol.Keys.ActiveRunId, run.RunId);
        writer.WriteBoolean(RimRunnerPipeProtocol.Keys.ProfilerArmed, RimRunnerProfilerBridge.Armed);
        RimRunnerDrive.WriteRecord(writer);
        string? startupFault = Patch_UIRootEntry_RimRunnerAutoLoad.StartupFault;
        if (startupFault != null)
        {
            writer.WriteString(RimRunnerPipeProtocol.Keys.StartupFault, startupFault);
        }

        writer.WriteEndObject();
    }

    private static void AppendLoadedPackIds(Utf8JsonWriter writer)
    {
        AppendStrings(writer, RimRunnerPipeProtocol.Keys.LoadedPackIds,
            LoadedModManager.RunningModsListForReading
                .Select(pack => pack.PackageId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.Ordinal));
    }

    private static void AppendBool(Utf8JsonWriter writer, string key, bool value)
    {
        writer.WriteBoolean(key, value);
    }

    private static void AppendText(Utf8JsonWriter writer, string key, string? value)
    {
        writer.WriteString(key, value);
    }

    private static readonly string[] RunKeys =
    [
        "runId", "batch", "case", "group", "checks", "checksOnly", "exclude", "ackStartup", "warn",
        RimRunnerWireKeys.RunRequest.WarnChecked, "ack", "knownFailing",
        RimRunnerWireKeys.RunRequest.ComponentExemption, "cleanSave", "exclusive", RimRunnerWireKeys.RunRequest.SaveProbe,
        RimRunnerWireKeys.RunRequest.Inputs, RimRunnerWireKeys.ShardRun.Group, RimRunnerWireKeys.ShardRun.Index, RimRunnerWireKeys.ShardRun.Count,
        RimRunnerWireKeys.ShardRun.Queue, RimRunnerWireKeys.ShardRun.Fingerprint,
        RimRunnerWireKeys.RunSource.Commit, RimRunnerWireKeys.RunSource.Worktree, RimRunnerWireKeys.RunSource.Changes,
        RimRunnerWireKeys.RunSource.ChangeCount, RimRunnerWireKeys.RunSource.Error
    ];

    private static LiveResponse RunRequest(RimRunnerRequestParams p)
    {
        if (RimRunnerProcessMode.Stages)
        {
            return ErrorResponse(409, RimRunnerProcessMode.Film ? "拍摄实例不执行批次" : "展台实例不执行批次");
        }

        p.RequireOnly(RunKeys);
        RimRunnerRunClaims.Bundle claims = new(
            p.Pairs("ackStartup", "子串").Select(pair => new RimRunnerRunClaims.Ack(pair.Key, pair.Value)).ToList(),
            p.Pairs("warn", "键"),
            p.Pairs("ack", "键").Select(pair => new RimRunnerRunClaims.Ack(pair.Key, pair.Value)).ToList(),
            p.Pairs("knownFailing", "体检 Id").Select(pair => new RimRunnerRunClaims.Ack(pair.Key, pair.Value)).ToList(),
            p.Pairs(RimRunnerWireKeys.RunRequest.ComponentExemption, "组件元素")
                .Select(pair => new RimRunnerRunClaims.Ack(pair.Key, pair.Value)).ToList(),
            p.Texts(RimRunnerWireKeys.RunRequest.WarnChecked) ?? [],
            InputsParam(p));

        bool batch = p.Bool("batch") ?? false;
        RimRunnerCaseSelector.EnvCheckMode? envChecks =
            RimRunnerCaseSelector.EnvCheckModeOf(p.Bool("checks") ?? false, p.Bool("checksOnly") ?? false);
        bool cleanSave = p.Bool("cleanSave") ?? false;
        bool exclusive = p.Bool("exclusive") ?? false;
        bool saveProbe = p.Bool(RimRunnerWireKeys.RunRequest.SaveProbe) ?? false;

        if (envChecks == null)
        {
            return ErrorResponse(400, "checks 与 checksOnly 不能同时为 true");
        }

        RimRunnerShardRequest? shard = ParseShardRequest(p, out string? shardError);
        if (shardError != null)
        {
            return ErrorResponse(400, shardError);
        }

        if (!RimRunnerRunSource.TryParse(p, out RimRunnerRunSource.Identity? source, out string? sourceUnavailable,
                out string? sourceError))
        {
            return ErrorResponse(400, sourceError);
        }

        RimRunnerCaseSelector? selector = RimRunnerCaseSelector.TryParse(
            batch, p.Texts("case"), p.Texts("group"), p.Texts("exclude"), envChecks.Value,
            out string? parseError);
        if (selector == null)
        {
            return ErrorResponse(400, parseError!);
        }

        if (selector.ResolveCases().Count == 0)
        {
            return ErrorResponse(400, "选择器合法，匹配到 0 条用例："
                                      + selector.ToCanonicalLabel());
        }

        return AcceptRun(selector, p.Text("runId"), claims, cleanSave, exclusive, shard, source, sourceUnavailable, saveProbe);
    }

    private static List<KeyValuePair<string, string>> InputsParam(RimRunnerRequestParams p)
    {
        var inputs = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> pair in p.Pairs(RimRunnerWireKeys.RunRequest.Inputs, "输入件路径"))
        {
            string key = RimRunnerWireKeys.RunRequest.NormalizeInputPath(pair.Key, out string? problem)
                         ?? throw new RimRunnerRequestException(400, RimRunnerWireKeys.RunRequest.Inputs + " 的路径" + problem + "：" + pair.Key);
            if (inputs.Any(existing => existing.Key == key))
            {
                throw new RimRunnerRequestException(400, RimRunnerWireKeys.RunRequest.Inputs + " 的路径重复：" + key);
            }

            inputs.Add(new KeyValuePair<string, string>(key, pair.Value));
        }

        return inputs;
    }

    private static readonly string[] ShardRunKeys =
    [
        RimRunnerWireKeys.ShardRun.Group, RimRunnerWireKeys.ShardRun.Index, RimRunnerWireKeys.ShardRun.Count,
        RimRunnerWireKeys.ShardRun.Queue, RimRunnerWireKeys.ShardRun.Fingerprint
    ];

    private static RimRunnerShardRequest? ParseShardRequest(RimRunnerRequestParams p, out string? error)
    {
        error = null;
        string? group = p.Text(RimRunnerWireKeys.ShardRun.Group);
        int? index = p.Int(RimRunnerWireKeys.ShardRun.Index);
        int? count = p.Int(RimRunnerWireKeys.ShardRun.Count);
        string? queue = p.Text(RimRunnerWireKeys.ShardRun.Queue);
        string? fingerprint = p.Text(RimRunnerWireKeys.ShardRun.Fingerprint);
        bool[] given = [group != null, index != null, count != null, queue != null, fingerprint != null];
        if (!given.Contains(true))
        {
            return null;
        }

        if (given.Contains(false))
        {
            error = "分片参数须五个同时传入（" + string.Join("、", ShardRunKeys) + "），缺少："
                    + string.Join("、", ShardRunKeys.Where((_, i) => !given[i]));
            return null;
        }

        if (count!.Value < 1 || index!.Value < 1 || index.Value > count.Value)
        {
            error = RimRunnerWireKeys.ShardRun.Index + " / " + RimRunnerWireKeys.ShardRun.Count
                    + " 须为 1 ≤ 片号 ≤ 片数 的整数，收到：" + index + " / " + count;
            return null;
        }

        if (group!.Length == 0 || fingerprint!.Length == 0)
        {
            error = RimRunnerWireKeys.ShardRun.Group + " 与 " + RimRunnerWireKeys.ShardRun.Fingerprint + " 不能为空";
            return null;
        }

        return new RimRunnerShardRequest
        {
            Group = group,
            Index = index.Value,
            Count = count.Value,
            QueuePath = queue!,
            Fingerprint = fingerprint
        };
    }

    private static LiveResponse ShardCellsRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly(RimRunnerLiveContract.ShardCells.Case, RimRunnerWireKeys.RunRequest.Inputs);
        string caseId = p.RequireText(RimRunnerLiveContract.ShardCells.Case, "用例 Id");
        List<KeyValuePair<string, string>> inputs = InputsParam(p);

        return ExecuteOnMainThread(waitMilliseconds: ShardCellsWaitMilliseconds, query: () =>
        {
            if (Current.ProgramState != ProgramState.Playing)
            {
                return ErrorResponse(503, "游戏当前不在 Playing 状态");
            }

            RimRunnerTestCase? testCase = RimRunnerRegistry.FirstPerId(RimRunnerRegistry.All)
                .FirstOrDefault(c => string.Equals(c.Id, caseId, StringComparison.Ordinal));
            if (testCase == null)
            {
                return ErrorResponse(404, "名册里没有用例 " + caseId);
            }

            if (testCase is not IRimRunnerShardable shardable)
            {
                return ErrorResponse(400, "用例 " + caseId + " 没有实现 IRimRunnerShardable");
            }

            RimRunnerShardEnumeration.Outcome outcome =
                RimRunnerInput.WithSpecified(inputs, () => RimRunnerShardEnumeration.Enumerate(testCase));
            List<RimRunnerShardCell>? cells = outcome.Cells;
            if (cells == null)
            {
                return ErrorResponse(RimRunnerLiveContract.ShardCells.UnprocessableStatus, outcome.Fault!);
            }

            var warnings = new List<RimRunnerWarningEntry>();
            outcome.PartitionWarnings(warnings, []);

            return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.ShardCells, writer =>
            {
                AppendText(writer, RimRunnerLiveContract.ShardCells.Case, testCase.Id);
                writer.WriteNumber(RimRunnerLiveContract.ShardCells.MinShardWeight, shardable.MinShardWeight);
                AppendText(writer, RimRunnerLiveContract.ShardCells.Fingerprint,
                    RimRunnerShardEnumeration.Fingerprint(cells));
                writer.WriteStartArray(RimRunnerLiveContract.ShardCells.Cells);
                foreach (RimRunnerShardCell cell in cells)
                {
                    writer.WriteStartObject();
                    AppendText(writer, RimRunnerLiveContract.ShardCells.Key, cell.Key);
                    writer.WriteNumber(RimRunnerLiveContract.ShardCells.Weight, cell.Weight);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartArray(RimRunnerLiveContract.ShardCells.Warnings);
                foreach (RimRunnerWarningEntry warning in warnings)
                {
                    writer.WriteStartObject();
                    AppendText(writer, RimRunnerLiveContract.ShardCells.WarningKey, warning.key);
                    AppendText(writer, RimRunnerLiveContract.ShardCells.WarningText, warning.text);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }));
        });
    }

    private const int ShardCellsWaitMilliseconds = 30000;

    private static LiveResponse AcceptRun(
        RimRunnerCaseSelector selector, string? requestedRunId,
        RimRunnerRunClaims.Bundle claims, bool cleanSave, bool exclusive, RimRunnerShardRequest? shard,
        RimRunnerRunSource.Identity? source, string? sourceUnavailable, bool saveProbe = false)
    {
        string command = selector.ToCanonicalLabel();
        string runId;
        if (string.IsNullOrEmpty(requestedRunId))
        {
            runId = RimRunnerRunId.New();
        }
        else if (!RimRunnerSession.IsValidRunId(requestedRunId))
        {
            return ErrorResponse(400, "runId 须是 " + RimRunnerRunId.Shape);
        }
        else
        {
            runId = requestedRunId;
        }

        RimRunnerRunSnapshot currentRun =
            RimRunnerRunCoordinator.GetSnapshot();
        if (currentRun.State != RimRunnerRunState.Idle)
        {
            return BusyResponse(currentRun);
        }

        if (Current.ProgramState != ProgramState.Playing)
        {
            return ErrorResponse(503, "游戏当前不在 Playing 状态");
        }

        if (!RimRunnerRunCoordinator.TryReserve(
                command, runId, out RimRunnerRunSnapshot activeRun))
        {
            return BusyResponse(activeRun);
        }

        RimRunnerRunClaims.Set(runId, claims);
        RimRunnerRunSource.Set(runId, source, sourceUnavailable);
        RimRunnerPipeLink.NoteStateMaybeChanged();
        EnqueueMainThreadAction(() =>
        {
            try
            {
                if (Current.ProgramState != ProgramState.Playing)
                {
                    RimRunnerSession.WriteTerminalError(
                        command, runId, "发车请求执行时游戏不在 Playing 状态（ProgramState="
                                        + Current.ProgramState + "）",
                        selector: selector);
                    return;
                }

                if (RimRunnerCurrentMapProbe.EngineCurrentMap() == null)
                {
                    RimRunnerSession.WriteTerminalError(
                        command, runId, "发车请求执行时没有当前地图",
                        selector: selector);
                    return;
                }

                if (saveProbe)
                {
                    RimRunnerSaveProbe.Arm();
                }

                RimRunnerGameComponent.ExecuteCommand(selector, runId, cleanSave, exclusive, shard);
            }
            catch (Exception exception)
            {
                RimRunnerSession.WriteTerminalError(
                    command, runId, "发车请求的主线程运行入口抛出异常", exception,
                    selector);
            }
        });
        return JsonResponse(202, RimRunnerJson.Write(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("accepted", command);
                writer.WriteString("runId", runId);
                writer.WriteString("state", "queued");
                writer.WriteEndObject();
            },
            false));
    }
}
