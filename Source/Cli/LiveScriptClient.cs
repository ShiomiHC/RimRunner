using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

public static partial class LiveSession
{
    private const int ActionsBatchBytes = 2 * 1024 * 1024;

    internal const string ScriptDirName = "live";

    private static readonly string ReservedName = $"{LiveCompiler.EvalName} 是 live eval 的回执名，不作脚本名";

    internal static int Eval(string searchDir, int? explicitNumber, string? code, string? file, IReadOnlyList<string> usings,
        bool json, int? longEventSeconds)
    {
        if (!TryResolveNumber(searchDir, explicitNumber, out int number, out int failed))
            return failed;
        if (ColdStartRefusal(number) is { } refusal)
        {
            Screen(refusal);
            return ExitCodes.Rejected;
        }

        string source;
        string origin = LiveCompiler.EvalOrigin;
        if (file != null)
        {
            origin = CommandContext.FullPath(file);
            try
            {
                source = LiveCompiler.ReadSource(origin);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Screen($"[FAIL] live eval 被拒，未执行：无法读取 {origin}：{exception.Message}");
                return ExitCodes.Rejected;
            }
        }
        else
        {
            source = code!;
        }

        (string productRoot, OrchestratorDescriptor descriptor) = ProgramDiscover(searchDir);
        LiveCompiler.Result compiled = LiveCompiler.CompileEval(number, productRoot, descriptor, source, usings, origin);
        if (CompileRefusal(compiled, "live eval") is { } refused)
            return refused;
        return DriveResolved(number, RimRunnerLiveContract.Drive.KindDo, LiveCompiler.EvalName,
            new Dictionary<string, string>(StringComparer.Ordinal), json, longEventSeconds, compiled.Image);
    }

    internal static int Drive(string searchDir, int? explicitNumber, string kind, string verb, IReadOnlyDictionary<string, string> args,
        bool json, int? longEventSeconds)
    {
        if (!TryResolveNumber(searchDir, explicitNumber, out int number, out int failed))
            return failed;
        if (ColdStartRefusal(number) is { } refusal)
        {
            Screen(refusal);
            return ExitCodes.Rejected;
        }

        bool until = kind == RimRunnerLiveContract.Drive.KindUntil;
        bool builtin = until ? verb == RimRunnerDriveTable.UntilLetter : RimRunnerDriveTable.Find(verb) != null;
        byte[]? image = null;
        if (verb == LiveCompiler.EvalName)
        {
            Screen($"[FAIL] live {kind} {verb} 被拒，未执行：{ReservedName}");
            return ExitCodes.Rejected;
        }

        if (!builtin)
        {
            (string productRoot, OrchestratorDescriptor descriptor) = ProgramDiscover(searchDir);
            string dir = ScriptDir(productRoot, descriptor);
            string? path = ScriptPath(dir, verb);
            if (path == null || !File.Exists(path))
            {
                string[] scripts = [.. ScriptPaths(dir).Select(script => Path.GetFileNameWithoutExtension(script))];
                Screen($"[FAIL] live {kind} {verb} 被拒，未执行：没有{(until ? "内置条件" : "原语")}或脚本「{verb}」。"
                       + (until ? $"内置条件：{RimRunnerDriveTable.UntilLetter}" : "原语：" + string.Join("、", RimRunnerDriveTable.All.Select(item => item.Name)))
                       + $"；脚本（{descriptor.WorkspaceDir}/{ScriptDirName}/）：{(scripts.Length == 0 ? "无" : string.Join("、", scripts))}");
                return ExitCodes.Rejected;
            }

            LiveCompiler.Result compiled = LiveCompiler.CompileScript(number, productRoot, descriptor, path);
            if (CompileRefusal(compiled, $"live {kind} {verb}") is { } code)
                return code;
            image = compiled.Image;
        }

        return DriveResolved(number, kind, verb, args, json, longEventSeconds, image);
    }

    private static string? ColdStartRefusal(int number)
    {
        var store = new ClaimStore();
        ColdStartLock.Owners owners = ColdStartLock.OwnersOf(store.Root, number);
        if (!owners.Any)
            return null;
        return owners.Starting != null
            ? ColdStartLock.RefusalLine(store.Root, number, owners.Starting, bypass: false)
            : ColdStartLock.GroupRefusalLine(store.Name(number), owners.Group!, DateTimeOffset.Now);
    }

    private static int? CompileRefusal(LiveCompiler.Result compiled, string command)
    {
        if (compiled.Image == null)
        {
            Screen($"[FAIL] {command} 被拒，未执行：" + (compiled.LinkFailure ? compiled.Errors[0] : "编译失败"));
            if (!compiled.LinkFailure)
            {
                foreach (string line in compiled.Errors)
                    Screen("  " + line);
            }

            return compiled.LinkFailure ? ExitCodes.Incomplete : ExitCodes.Rejected;
        }

        foreach (string note in compiled.Notes)
            Screen("[WARN] " + note);
        return null;
    }

    private static int DriveResolved(int number, string kind, string verb, IReadOnlyDictionary<string, string> args,
        bool json, int? longEventSeconds, byte[]? image)
    {
        var store = new ClaimStore();
        RimRunnerConfig.MachineConfig config = RimRunnerConfig.Machine();
        int grace = store.Read(number) is { Policy.TakesLiveGrace: false }
            ? 0
            : config.LiveGraceMinutes.Value;
        var policy = new LiveDriveClient.WaitPolicy(
            config.LiveWaitMinutes.Value > 0 ? TimeSpan.FromMinutes(config.LiveWaitMinutes.Value) : null,
            config.LiveAfterWait.Value == LiveDriveClient.AfterWaitCancel,
            config.LiveLimitMinutes.Value * 60);
        int code = LiveDriveClient.Run(number, kind, verb, args, json, Console.WriteLine, longEventSeconds, policy, image);
        if (grace > 0 && code != ExitCodes.Rejected && LiveHolds.Extend(number, grace, out _) is { } hold && !json)
            Screen("  " + hold.Describe());
        return code;
    }

    internal static int Actions(string searchDir, int? explicitNumber, string kind, bool json)
    {
        if (!TryResolveNumber(searchDir, explicitNumber, out int number, out int failed))
            return failed;
        (string productRoot, OrchestratorDescriptor descriptor) = ProgramDiscover(searchDir);
        string[] all = ScriptPaths(ScriptDir(productRoot, descriptor));
        string[] paths = [.. all.Where(path => Path.GetFileNameWithoutExtension(path) != LiveCompiler.EvalName)];
        IReadOnlyList<(string Name, LiveCompiler.Result Result)> compiled = [];
        if (paths.Length > 0)
        {
            compiled = LiveCompiler.CompileScripts(number, productRoot, descriptor, paths, out string? refsError);
            if (refsError != null)
                return Fail(refsError);
        }

        List<string> bodies = [];
        foreach (List<(string Name, byte[] Image)> batch in Batches(compiled))
        {
            using JsonDocument doc = Send(number, RimRunnerPipeProtocol.Ops.Actions, writer =>
            {
                writer.WriteStartArray(RimRunnerLiveContract.Actions.Scripts);
                foreach ((string name, byte[] image) in batch)
                {
                    writer.WriteStartObject();
                    writer.WriteString(RimRunnerLiveContract.Actions.Name, name);
                    writer.WriteBase64String(RimRunnerLiveContract.Actions.Assembly, image);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }, QueryTimeout, out int status);
            JsonElement root = doc.RootElement;
            if (status is < 200 or >= 300)
            {
                foreach (string line in LivePresentation.FormatError(root))
                    Screen(line);
                return ExitCodes.Incomplete;
            }

            if (LivePresentation.SchemaMismatch(root, RimRunnerLiveContract.Schema.Actions) is { } mismatch)
                return Fail(mismatch);
            bodies.Add(root.GetRawText());
        }

        using JsonDocument merged = JsonDocument.Parse(DrivePresentation.MergeRoster(bodies,
        [
            .. compiled.Where(item => item.Result.Image == null)
                .Select(item => (item.Name, RimRunnerLiveContract.Actions.StateCompileError, string.Join("\n", item.Result.Errors))),
            .. all.Length > paths.Length
                ? [(LiveCompiler.EvalName, RimRunnerLiveContract.Actions.StateRegisterError, $"{ReservedName}，{LiveCompiler.EvalName}.cs 不装载")]
                : Array.Empty<(string, string, string)>(),
        ]));
        IReadOnlyList<string> lines = kind == RimRunnerLiveContract.Drive.KindUntil
            ? DrivePresentation.FormatConditions(merged.RootElement, out int code)
            : DrivePresentation.FormatActions(merged.RootElement, out code);
        if (json)
            CliLog.Machine(() => Console.WriteLine(merged.RootElement.GetRawText()));
        else
            foreach (string line in lines)
                Screen(line);
        return code;
    }

    private static List<List<(string Name, byte[] Image)>> Batches(IReadOnlyList<(string Name, LiveCompiler.Result Result)> compiled)
    {
        List<List<(string Name, byte[] Image)>> batches = [[]];
        long bytes = 0;
        foreach ((string name, LiveCompiler.Result result) in compiled)
        {
            if (result.Image is not { } image)
                continue;
            long encoded = (image.Length + 2) / 3 * 4L;
            if (batches[^1].Count > 0 && bytes + encoded > ActionsBatchBytes)
            {
                batches.Add([]);
                bytes = 0;
            }

            batches[^1].Add((name, image));
            bytes += encoded;
        }

        return batches;
    }

    internal static string ScriptDir(string productRoot, OrchestratorDescriptor descriptor) =>
        Path.Combine(productRoot, descriptor.WorkspaceDir.Replace('/', Path.DirectorySeparatorChar), ScriptDirName);

    internal static string[] ScriptPaths(string dir) =>
        Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir, "*.cs").OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)]
            : [];

    internal static string? ScriptPath(string dir, string name) =>
        name.Length == 0 || name.Contains("..", StringComparison.Ordinal) || name.IndexOfAny(['/', '\\', ':']) >= 0
            ? null
            : Path.Combine(dir, name + ".cs");
}
