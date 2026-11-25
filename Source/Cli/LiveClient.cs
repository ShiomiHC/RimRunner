using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

public static partial class LiveSession
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(130);
    private static readonly TimeSpan ScreenshotWait = TimeSpan.FromSeconds(20);

    public static int Execute(
        string searchDir,
        int? explicitNumber,
        IReadOnlyList<string> rest,
        string? statDef,
        bool hediff,
        string? screenshotLabel,
        string? screenshotRect,
        bool json,
        string? dumpPrefix)
    {
        if (rest.Count == 0)
            throw new UnreachableException("裸 live 在解析阶段已答成帮助");

        string action = rest[0];
        return action switch
        {
            "pawn" => Pawn(searchDir, explicitNumber, rest, statDef, hediff, json),
            "screenshot" => Screenshot(searchDir, explicitNumber, screenshotLabel, screenshotRect, json),
            "save" => Save(searchDir, explicitNumber, rest, json),
            "perf" => Perf(searchDir, explicitNumber, rest, json, dumpPrefix),
            "menu" => GetAndPrint(
                searchDir, explicitNumber, RimRunnerPipeProtocol.Ops.Menu, null,
                RimRunnerLiveContract.Schema.Menu, json),
            "windows" => GetAndPrint(
                searchDir, explicitNumber, RimRunnerPipeProtocol.Ops.Windows, null,
                RimRunnerLiveContract.Schema.Windows, json),
            "gizmos" => GetAndPrint(
                searchDir, explicitNumber, RimRunnerPipeProtocol.Ops.Gizmos, null,
                RimRunnerLiveContract.Schema.Gizmos, json),
            "progress" => GetAndPrint(
                searchDir, explicitNumber, RimRunnerPipeProtocol.Ops.Progress, null,
                RimRunnerLiveContract.Schema.Progress, json),
            _ => Fail($"未知 live 动作：{action}"),
        };
    }

    internal static (string Key, string Value)? MethodParam(string token)
    {
        if (!token.StartsWith("--", StringComparison.Ordinal))
            return null;
        int at = token.IndexOf('=');
        if (at < 0)
            return null;
        string key = token[2..at];
        bool named = key.Length > 0 && char.IsAsciiLetter(key[0])
                     && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        return named ? (key, token[(at + 1)..]) : null;
    }

    internal static Dictionary<string, string> MethodArgs(IReadOnlyList<string> positional)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string token in positional.Skip(1))
        {
            (string key, string value) = MethodParam(token)!.Value;
            args[key] = value;
        }

        return args;
    }

    internal static int Dev(string searchDir, int? explicitNumber, string? path, string? find, bool json)
    {
        if (!TryResolveNumber(searchDir, explicitNumber, out int number, out int failed))
            return failed;
        using JsonDocument doc = Send(number, RimRunnerPipeProtocol.Ops.Dev, writer =>
        {
            if (path != null)
                writer.WriteString(RimRunnerLiveContract.Dev.Path, path);
            if (find != null)
                writer.WriteString(RimRunnerLiveContract.Dev.Find, find);
        }, QueryTimeout, out int status);
        if (!PrintResponse(doc, status, RimRunnerLiveContract.Schema.Dev, json, allowNonSuccess: true))
            return ExitCodes.Incomplete;
        return find != null && doc.RootElement.TryGetProperty(RimRunnerLiveContract.Dev.Total, out JsonElement total)
               && total.TryGetInt32(out int count) && count == 0
            ? 1
            : 0;
    }

    internal static int Follow(string searchDir, int? explicitNumber, string verb, string id, bool json)
    {
        if (!TryResolveNumber(searchDir, explicitNumber, out int number, out int failed))
            return failed;
        return verb == "cancel"
            ? LiveDriveClient.Cancel(number, id, json, Console.WriteLine)
            : LiveDriveClient.Status(number, id, json, Console.WriteLine);
    }

    private static int Pawn(
        string searchDir, int? number, IReadOnlyList<string> rest,
        string? statDef, bool hediff, bool json)
    {
        string? key = rest.Count > 1 ? rest[1] : null;
        if (key == null)
        {
            return GetAndPrint(searchDir, number, RimRunnerPipeProtocol.Ops.Pawns, null,
                RimRunnerLiveContract.Schema.Pawns, json);
        }

        int code = 0;
        if (hediff)
        {
            int hediffCode = GetAndPrint(
                searchDir, number, RimRunnerPipeProtocol.Ops.Hediffs,
                writer => writer.WriteString(RimRunnerLiveContract.Hediffs.Pawn, key),
                RimRunnerLiveContract.Schema.Hediffs, json);
            if (hediffCode != 0) return hediffCode;
        }

        if (statDef != null)
        {
            code = GetAndPrint(
                searchDir, number, RimRunnerPipeProtocol.Ops.Stat,
                writer =>
                {
                    writer.WriteString(RimRunnerLiveContract.Stat.Pawn, key);
                    writer.WriteString(RimRunnerLiveContract.Stat.StatName, statDef);
                },
                RimRunnerLiveContract.Schema.Stat, json);
        }

        return code;
    }

    private static int Screenshot(
        string searchDir, int? explicitNumber, string? label, string? rect, bool json)
    {
        double[]? rectValues = null;
        if (!string.IsNullOrEmpty(rect))
            rectValues = ParseRect(rect) ?? throw new UnreachableException("--rect 的取值形状已在解析阶段核对");

        if (!TryResolveNumber(searchDir, explicitNumber, out int number, out int failed))
            return failed;

        using JsonDocument accepted = Send(number, RimRunnerPipeProtocol.Ops.Screenshot, writer =>
        {
            if (!string.IsNullOrEmpty(label))
                writer.WriteString("label", label);
            if (rectValues != null)
            {
                writer.WriteStartArray("rect");
                foreach (double value in rectValues)
                    writer.WriteNumberValue(value);
                writer.WriteEndArray();
            }
        }, QueryTimeout, out int status);
        if (status is < 200 or >= 300)
        {
            PrintResponse(accepted, status, RimRunnerLiveContract.Schema.Screenshot, json, true);
            return ExitCodes.Incomplete;
        }

        string? mismatch = LivePresentation.SchemaMismatch(
            accepted.RootElement, RimRunnerLiveContract.Schema.Screenshot);
        if (mismatch != null)
            return Fail(mismatch);
        if (json)
            CliLog.Machine(() => Console.WriteLine(accepted.RootElement.GetRawText()));
        if (status != 202)
        {
            if (!json)
                PrintResponse(accepted, status, RimRunnerLiveContract.Schema.Screenshot, false, true);
            return 0;
        }

        string? id = Text(accepted.RootElement, RimRunnerLiveContract.Screenshot.ArtifactId);
        if (string.IsNullOrEmpty(id))
            return Fail("截图受理回包缺少 artifactId");

        InstanceOutput? output = InstanceLinks.Get(number)?.Output;
        if (output == null)
            return Fail("实例不在本编排进程名下，无法取得截图产出");
        InstanceOutput.Shot shot;
        try
        {
            Task<InstanceOutput.Shot> arrival = output.ScreenshotArrival(id);
            if (!arrival.Wait(ScreenshotWait))
                return Fail($"截图 {(int)ScreenshotWait.TotalSeconds}s 内未出图");
            shot = arrival.Result;
        }
        finally
        {
            output.ForgetScreenshot(id);
        }

        using JsonDocument snapshot = JsonDocument.Parse(shot.Snapshot);
        string? state = Text(snapshot.RootElement, RimRunnerLiveContract.Screenshot.State);
        if (!PrintResponse(snapshot, 200, RimRunnerLiveContract.Schema.Screenshot, json, true)
            || !string.Equals(state, RimRunnerLiveContract.ScreenshotState.Complete, StringComparison.Ordinal))
        {
            return ExitCodes.Incomplete;
        }

        if (shot.Fault != null)
            return Fail($"截图未写入磁盘：{shot.Fault}");
        if (!json)
        {
            Screen(TextureTierPolicy.ScreenshotNote(InstanceProbe.Facts(number)?.TextureTier));
        }
        return 0;
    }

    private static int Save(string searchDir, int? number, IReadOnlyList<string> rest, bool json)
    {
        string name = rest[1];
        if (!TryResolveNumber(searchDir, number, out int resolved, out int failed))
            return failed;

        (string productRoot, OrchestratorDescriptor descriptor) = ProgramDiscover(searchDir);
        ClaimRecord claim = new ClaimStore().Read(resolved)
                            ?? throw new StoreException($"实例 {ClaimStore.GoneName}：登记已卸载（{productRoot}）");
        string staging = SaveExport.StagingName();
        using JsonDocument doc = Send(resolved, RimRunnerPipeProtocol.Ops.Save,
            writer => writer.WriteString("name", staging), SaveTimeout, out int status);
        if (status is < 200 or >= 300)
        {
            PrintResponse(doc, status, RimRunnerLiveContract.Schema.Save, json, allowNonSuccess: true);
            if (status == TimedOut)
            {
                string? runState = InstanceProbe.RunState(resolved);
                Screen(runState == null
                    ? "  复查状态：实例未连接"
                    : $"  复查状态：runState={runState}");
            }

            if (status is TimedOut or 504)
                Screen("  未导出");
            return ExitCodes.Incomplete;
        }

        string? mismatch = LivePresentation.SchemaMismatch(doc.RootElement, RimRunnerLiveContract.Schema.Save);
        string? staged = Text(doc.RootElement, RimRunnerLiveContract.Save.Path);
        if (mismatch != null || staged == null)
            return Fail(mismatch ?? "save 回包缺少 path");

        SaveExport.Result result;
        try
        {
            result = SaveExport.Finish(staged,
                SaveExport.OutSavesDir(descriptor, productRoot), name, claim,
                TestModSynthesizer.PackageIdFor(descriptor, claim.Number), json ? null : Screen);
        }
        catch (SaveExport.HeaderVerificationException exception)
        {
            Screen($"[FAIL] {exception.Message}");
            if (exception.RewriteNotApplied && EngineMvidNote(descriptor, claim.Number) is { } note)
                Screen("  " + note);
            return ExitCodes.Incomplete;
        }
        if (json)
        {
            CliLog.Machine(() => Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["schema"] = RimRunnerLiveContract.Schema.Save,
                [RimRunnerLiveContract.Save.Saved] = name,
                [RimRunnerLiveContract.Save.Path] = result.Path,
                [RimRunnerLiveContract.Save.Bytes] = result.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [RimRunnerLiveContract.Save.Overwrote] = result.Overwrote,
            }, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            })));
            return 0;
        }

        Screen($"已导出  {name}");
        Screen($"  路径  {result.Path}");
        Screen($"  大小  {result.Bytes}");
        Screen($"  覆盖  {(result.Overwrote ? "是" : "否")}");
        return 0;
    }

    private static string? EngineMvidNote(OrchestratorDescriptor descriptor, int number)
    {
        Dictionary<string, string>? loaded = InstanceProbe.Ping(number)?.AssemblyMvids;
        return EngineArtifact.LoadedIsPackageCopy(loaded, EnginePackage.Locate()) switch
        {
            true => EngineArtifact.PackageCopyNote(true),
            false => EngineArtifact.PackageCopyNote(false),
            null => null,
        };
    }

    private static int Perf(
        string searchDir, int? number, IReadOnlyList<string> rest, bool json,
        string? dumpPrefix)
    {
        string? sub = rest.Count > 1 ? rest[1] : null;
        if (sub == null)
        {
            return GetAndPrint(
                searchDir, number, RimRunnerPipeProtocol.Ops.Profiler, ProfilerAction("status"),
                RimRunnerLiveContract.Schema.Profiler, json);
        }

        return sub switch
        {
            "arm" => GetAndPrint(searchDir, number, RimRunnerPipeProtocol.Ops.Profiler, ProfilerAction("arm"),
                RimRunnerLiveContract.Schema.Profiler, json),
            "reset" => GetAndPrint(searchDir, number, RimRunnerPipeProtocol.Ops.Profiler, ProfilerAction("reset"),
                RimRunnerLiveContract.Schema.Profiler, json),
            "compare" => GetAndPrint(searchDir, number, RimRunnerPipeProtocol.Ops.Profiler,
                ProfilerAction("arm-compare"), RimRunnerLiveContract.Schema.Profiler, json),
            "dump" => Dump(searchDir, number, json, dumpPrefix),
            _ => throw new UnreachableException("perf 子动作已在解析阶段核对"),
        };
    }

    private static Action<Utf8JsonWriter> ProfilerAction(string action) =>
        writer => writer.WriteString("action", action);

    private static int Dump(string searchDir, int? explicitNumber, bool json, string? dumpPrefix)
    {
        _ = json;
        if (!TryResolveNumber(searchDir, explicitNumber, out int number, out int failed))
            return failed;
        using JsonDocument doc = Send(number, RimRunnerPipeProtocol.Ops.Profiler, writer =>
        {
            writer.WriteString("action", "dump");
            if (!string.IsNullOrEmpty(dumpPrefix))
                writer.WriteString("prefix", dumpPrefix);
        }, QueryTimeout, out int status);
        string? mismatch = status is >= 200 and < 300
            ? LivePresentation.SchemaMismatch(doc.RootElement, RimRunnerLiveContract.Schema.Profiler)
            : null;
        if (mismatch != null)
        {
            Screen("[FAIL] " + mismatch);
            return ExitCodes.Incomplete;
        }

        if (status is < 200 or >= 300)
        {
            PrintResponse(doc, status, RimRunnerLiveContract.Schema.Error, json: false, allowNonSuccess: true);
            return ExitCodes.Incomplete;
        }

        CliLog.Machine(() => Console.WriteLine(doc.RootElement.GetRawText()));
        return 0;
    }

    private static int GetAndPrint(
        string searchDir, int? explicitNumber, string op, Action<Utf8JsonWriter>? parameters, string expectedSchema,
        bool json)
    {
        if (!TryResolveNumber(searchDir, explicitNumber, out int number, out int failed))
            return failed;
        using JsonDocument doc = Send(number, op, parameters, QueryTimeout, out int status);
        return PrintResponse(doc, status, expectedSchema, json, allowNonSuccess: true) ? 0 : ExitCodes.Incomplete;
    }

    internal static double[]? ParseRect(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return null;
        var values = new double[4];
        for (int index = 0; index < 4; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]))
                return null;
        }

        return values;
    }

    private static bool TryResolveNumber(string searchDir, int? explicitNumber, out int number, out int exitCode)
    {
        number = 0;
        (string productRoot, _) = ProgramDiscover(searchDir);
        var store = new ClaimStore();
        if (explicitNumber is int requested)
        {
            ClaimRecord? claim = store.Read(requested);
            if (claim == null || !PathIdentity.Same(claim.ProductRoot, productRoot))
            {
                exitCode = Fail($"实例 {claim?.DisplayName ?? ClaimStore.GoneName} 不是本树的登记");
                return false;
            }

            number = requested;
            exitCode = 0;
            ActivityLog.Touch(ActivityLog.Live, number);
            resolvedNumber = number;
            return true;
        }

        List<ClaimRecord> mine = store.FindByProductRoot(productRoot);
        if (mine.Count == 0)
        {
            exitCode = Fail("本树尚无登记");
            return false;
        }

        AutoInstances.Pick pick = PickUnnamed(mine, AutoInstances.AliveNumbers(), InstanceProbe.CurrentRunId);
        if (pick.Claim == null)
        {
            if (pick.Refusal.Count == 1 && !SnapshotAnswers())
            {
                exitCode = Fail("全机协调无应答，无法确定本树哪些实例在运行");
                return false;
            }

            foreach (string line in pick.Refusal) Screen(line);
            exitCode = ExitCodes.Incomplete;
            return false;
        }

        number = pick.Claim.Number;
        exitCode = 0;
        ActivityLog.Touch(ActivityLog.Live, number);
        resolvedNumber = number;
        return true;
    }

    internal static AutoInstances.Pick PickUnnamed(IReadOnlyList<ClaimRecord> mine, ISet<int> alive,
        Func<int, string?> currentRunId)
    {
        List<ClaimRecord> live = [.. mine.Where(claim => alive.Contains(claim.Number))];
        if (live.Count == 0) return new AutoInstances.Pick(null, ["[FAIL] 本树此刻无运行中的实例"]);
        if (live is [{ PurposeKind: not InstancePurpose.Spare } only]) return new AutoInstances.Pick(only, []);

        List<ClaimRecord> auto = AutoInstances.Live(live, alive);
        return new AutoInstances.Pick(null,
        [
            live.Count == 1
                ? "[FAIL] 本树运行中的只有一个临时自动实例，用 --run-id 指定它："
                : $"[FAIL] 本树有 {live.Count} 个运行中的实例，指定其中一个：",
            .. AutoInstances.Candidates(auto, currentRunId),
            .. live.Except(auto).OrderBy(claim => claim.Number)
                .Select(claim => $"  --tree {claim.DisplayName}  {claim.EffectivePurpose}"),
        ]);
    }

    private static bool SnapshotAnswers()
    {
        try
        {
            MachineSnapshot.ReadOrThrow();
            return true;
        }
        catch (StoreException)
        {
            return false;
        }
    }

    private static int? resolvedNumber;

    private static readonly TimeSpan UnownedErrorsTimeout = TimeSpan.FromSeconds(5);

    private const int ScreenedHeads = 3;

    internal static int WithUnownedErrors(bool json, Func<int> command)
    {
        resolvedNumber = null;
        int exitCode = command();
        if (resolvedNumber is not int number)
            return exitCode;

        using JsonDocument doc = Send(number, RimRunnerPipeProtocol.Ops.UnownedErrors, null, UnownedErrorsTimeout, out int status);
        JsonElement root = doc.RootElement;
        if (status is < 200 or >= 300
            || LivePresentation.SchemaMismatch(root, RimRunnerLiveContract.Schema.UnownedErrors) != null)
        {
            CliLog.Info("[错误级日志] 取实例缝里的错误级日志失败：" + (Text(root, RimRunnerLiveContract.Error.Message) ?? "回包格式不匹配"));
            return exitCode;
        }

        Action<string> write = json ? CliLog.Info : Screen;
        IReadOnlyList<string> screened = LivePresentation.FormatUnownedErrors(root, ScreenedHeads, out IReadOnlyList<string> logOnly);
        foreach (string line in screened)
            write(line);
        foreach (string line in logOnly)
            CliLog.Info(line);
        return exitCode;
    }

    internal static JsonDocument Send(int number, string op, Action<Utf8JsonWriter>? parameters, TimeSpan timeout,
        out int status)
    {
        bool local = InstanceLinks.Get(number) != null;
        InstanceLink.Reply reply = InstanceLinks.Request(number, op, parameters, timeout);
        status = reply.Status;
        if (reply.Status == TimedOut)
            return ErrorDocument($"{(int)timeout.TotalSeconds}s 内未收到回包");
        if (reply.Status == InstanceLink.Reply.NoLinkStatus)
            return ErrorDocument(NoLinkReason(local));
        if (string.IsNullOrWhiteSpace(reply.Body))
            return ErrorDocument("空回包");
        try
        {
            return JsonDocument.Parse(reply.Body);
        }
        catch (JsonException)
        {
            status = 500;
            return ErrorDocument("回包不是 JSON");
        }
    }

    internal static string NoLinkReason(bool local) =>
        local ? "实例管道未连接"
        : Leases.Link == null ? "实例不在本编排进程名下，未连接全机协调，无法转发"
        : "实例不在本编排进程名下，经全机协调转发未送达实例";

    private static JsonDocument ErrorDocument(string message) =>
        JsonDocument.Parse("{\"schema\":\"error\",\"error\":" + JsonSerializer.Serialize(message) + "}");

    internal const int TimedOut = InstanceLink.Reply.TimedOutStatus;

    private static bool PrintResponse(
        JsonDocument doc, int status, string expectedSchema, bool json, bool allowNonSuccess)
    {
        JsonElement root = doc.RootElement;
        if (status is < 200 or >= 300)
        {
            foreach (string line in LivePresentation.FormatError(root))
                Screen(line);
            return false;
        }

        string? mismatch = LivePresentation.SchemaMismatch(root, expectedSchema);
        if (mismatch != null)
        {
            Screen("[FAIL] " + mismatch);
            return false;
        }

        if (json)
        {
            CliLog.Machine(() => Console.WriteLine(root.GetRawText()));
            return true;
        }

        if (!LivePresentation.TryFormat(root, expectedSchema, out IReadOnlyList<string> lines, out string? error))
        {
            Screen("[FAIL] " + error);
            return false;
        }

        foreach (string line in lines)
            Screen(line);
        _ = allowNonSuccess;
        return !lines.Any(line => line.StartsWith("[FAIL]", StringComparison.Ordinal));
    }

    private static void Screen(string line) => FindingPresentation.ScreenCapped(line, Console.WriteLine);

    private static int Fail(string message)
    {
        Screen("[FAIL] " + message);
        return ExitCodes.Incomplete;
    }

    private static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static (string ProductRoot, OrchestratorDescriptor Descriptor) ProgramDiscover(string searchDir)
    {
        string? productRoot = OrchestratorDescriptor.FindProductRoot(searchDir) ?? throw new StoreException(
                $"在 {searchDir} 及其上级目录未找到 {OrchestratorDescriptor.FileName}");
        return OrchestratorDescriptor.Load(productRoot);
    }
}
