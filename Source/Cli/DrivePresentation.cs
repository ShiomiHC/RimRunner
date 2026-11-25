using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

public static class DrivePresentation
{
    public static bool IsSettled(JsonElement root) =>
        Text(root, RimRunnerLiveContract.Drive.State) is not (null or RimRunnerLiveContract.Drive.StateQueued
            or RimRunnerLiveContract.Drive.StateRunning);

    public static IReadOnlyList<string> Format(JsonElement root, out int exitCode)
    {
        string id = Text(root, RimRunnerLiveContract.Drive.Id) ?? "?";
        string kind = Text(root, RimRunnerLiveContract.Drive.Kind) ?? RimRunnerLiveContract.Drive.KindDo;
        string verb = Command(root);
        string state = Text(root, RimRunnerLiveContract.Drive.State) ?? "?";
        string? reason = Text(root, RimRunnerLiveContract.Drive.Reason);
        var lines = new List<string>();
        List<string> errors = [.. Logs(root).Where(line => line.StartsWith(RimRunnerLiveContract.Drive.ErrorPrefix, StringComparison.Ordinal))];
        List<string> warnings = [.. Logs(root).Where(line => line.StartsWith(RimRunnerLiveContract.Drive.WarningPrefix, StringComparison.Ordinal))];

        (string head, exitCode) = state switch
        {
            RimRunnerLiveContract.Drive.StateDone when kind == RimRunnerLiveContract.Drive.KindUntil =>
                Bool(root, RimRunnerLiveContract.Drive.Met) switch
                {
                    true => ($"live {verb} 成立", 0),
                    false => ($"live {verb} 不成立", 1),
                    null => ($"[FAIL] live {verb} 完成的回执缺判定结果", ExitCodes.Incomplete),
                },
            RimRunnerLiveContract.Drive.StateDone => errors.Count > 0
                ? ($"live {verb} 完成，期间有 {errors.Count} 条错误级日志", 1)
                : ($"live {verb} 完成", 0),
            RimRunnerLiveContract.Drive.StateRefused => ($"[FAIL] live {verb} 被拒，未执行：{reason}", ExitCodes.Rejected),
            RimRunnerLiveContract.Drive.StateAborted => ($"[FAIL] live {verb} 中止：{reason}", ExitCodes.Incomplete),
            RimRunnerLiveContract.Drive.StateFailed => ($"[FAIL] live {verb} 失败：{reason}", ExitCodes.InternalFault),
            _ => ($"[FAIL] live {verb} 回执状态不是终态：{state}", ExitCodes.Incomplete),
        };
        lines.Add(head + $"（操作 {id}）");

        if (Int(root, RimRunnerLiveContract.Drive.StartTick) is int startTick
            && Int(root, RimRunnerLiveContract.Drive.EndTick) is int endTick)
        {
            lines.Add($"  tick {startTick} → {endTick}（推进 {endTick - startTick}）· {Int(root, RimRunnerLiveContract.Drive.Frames) ?? 0} 帧"
                      + $" · {Int(root, RimRunnerLiveContract.Drive.ElapsedMs) ?? 0} ms");
        }

        lines.AddRange(Strings(root, RimRunnerLiveContract.Drive.Lines).Select(line => "  " + line));

        lines.AddRange(errors.Select(line => "  [ERROR] " + line[RimRunnerLiveContract.Drive.ErrorPrefix.Length..]));
        lines.AddRange(warnings.Select(line => "  [WARN] " + line[RimRunnerLiveContract.Drive.WarningPrefix.Length..]));
        return lines;
    }

    private static string Command(JsonElement root)
    {
        string kind = Text(root, RimRunnerLiveContract.Drive.Kind) ?? RimRunnerLiveContract.Drive.KindDo;
        string verb = Text(root, RimRunnerLiveContract.Drive.Verb) ?? "?";
        return kind == RimRunnerLiveContract.Drive.KindDo && verb == LiveCompiler.EvalName ? verb : $"{kind} {verb}";
    }

    public static string Running(JsonElement root)
    {
        string id = Text(root, RimRunnerLiveContract.Drive.Id) ?? "?";
        string verb = Command(root);
        string state = Text(root, RimRunnerLiveContract.Drive.State) == RimRunnerLiveContract.Drive.StateQueued ? "排队待执行" : "正在执行";
        string progress = Int(root, RimRunnerLiveContract.Drive.StartTick) is int startTick
                          && Int(root, RimRunnerLiveContract.Drive.EndTick) is int endTick
            ? $" · tick {startTick} → {endTick} · {Int(root, RimRunnerLiveContract.Drive.Frames) ?? 0} 帧 · {Int(root, RimRunnerLiveContract.Drive.ElapsedMs) ?? 0} ms"
            : "";
        return $"live {verb} {state}（操作 {id}）{progress}";
    }

    public static string MergeRoster(IReadOnlyList<string> bodies, IReadOnlyList<(string Name, string State, string Fault)> local)
    {
        List<JsonDocument> docs = [.. bodies.Select(body => JsonDocument.Parse(body))];
        try
        {
            JsonElement first = docs[0].RootElement;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString(RimRunnerLiveContract.KeySchema, RimRunnerLiveContract.Schema.Actions);
                writer.WritePropertyName(RimRunnerLiveContract.Actions.Builtins);
                first.GetProperty(RimRunnerLiveContract.Actions.Builtins).WriteTo(writer);
                writer.WritePropertyName(RimRunnerLiveContract.Actions.Conditions);
                first.GetProperty(RimRunnerLiveContract.Actions.Conditions).WriteTo(writer);
                if (docs.Select(doc => Text(doc.RootElement, RimRunnerLiveContract.Actions.Busy)).FirstOrDefault(busy => busy != null) is { } busyState)
                    writer.WriteString(RimRunnerLiveContract.Actions.Busy, busyState);

                var entries = new List<(string Name, Action<Utf8JsonWriter> Write)>();
                foreach (JsonDocument doc in docs)
                {
                    foreach (JsonElement item in doc.RootElement.GetProperty(RimRunnerLiveContract.Actions.Scripts).EnumerateArray())
                        entries.Add((Text(item, RimRunnerLiveContract.Actions.Name) ?? "", item.WriteTo));
                }

                foreach ((string name, string state, string fault) in local)
                {
                    entries.Add((name, w =>
                    {
                        w.WriteStartObject();
                        w.WriteString(RimRunnerLiveContract.Actions.Name, name);
                        w.WriteString(RimRunnerLiveContract.Actions.State, state);
                        w.WriteString(RimRunnerLiveContract.Actions.Fault, fault);
                        w.WriteEndObject();
                    }));
                }

                writer.WriteStartArray(RimRunnerLiveContract.Actions.Scripts);
                foreach ((_, Action<Utf8JsonWriter> write) in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
                    write(writer);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        finally
        {
            foreach (JsonDocument doc in docs)
                doc.Dispose();
        }
    }

    public static IReadOnlyList<string> FormatActions(JsonElement root, out int exitCode)
    {
        var lines = new List<string>();
        AddSection(lines, [.. root.GetProperty(RimRunnerLiveContract.Actions.Builtins).EnumerateArray()], "原语");
        AddSection(lines, LoadedScripts(root, RimRunnerLiveContract.Drive.KindDo), "脚本动作");
        return WithScriptStates(lines, root, out exitCode);
    }

    public static IReadOnlyList<string> FormatConditions(JsonElement root, out int exitCode)
    {
        var lines = new List<string>();
        AddSection(lines, [.. root.GetProperty(RimRunnerLiveContract.Actions.Conditions).EnumerateArray(),
            .. LoadedScripts(root, RimRunnerLiveContract.Drive.KindUntil)], "条件");
        lines.Add("每个条件都接受以下参数：");
        lines.AddRange(RimRunnerDriveTable.Until.Params.Select(param =>
            $"    --{param.Name}={param.Placeholder}{(param.Required ? "（必填）" : "")}  {param.Meaning}"));
        return WithScriptStates(lines, root, out exitCode);
    }

    private static List<JsonElement> LoadedScripts(JsonElement root, string kind) =>
    [
        .. Scripts(root).Where(item => Text(item, RimRunnerLiveContract.Actions.State) == RimRunnerLiveContract.Actions.StateLoaded
                                       && Text(item, RimRunnerLiveContract.Actions.Kind) == kind),
    ];

    private static IEnumerable<JsonElement> Scripts(JsonElement root) =>
        root.TryGetProperty(RimRunnerLiveContract.Actions.Scripts, out JsonElement scripts) && scripts.ValueKind == JsonValueKind.Array
            ? scripts.EnumerateArray()
            : [];

    private static void AddSection(List<string> lines, IReadOnlyList<JsonElement> entries, string noun)
    {
        lines.Add($"{noun} {entries.Count} 个");
        foreach (JsonElement entry in entries)
        {
            lines.Add($"  {Text(entry, RimRunnerLiveContract.Actions.Id)}  {Text(entry, RimRunnerLiveContract.Actions.Title)}");
            lines.AddRange(ParamLines(entry));
        }
    }

    private static List<string> WithScriptStates(List<string> lines, JsonElement root, out int exitCode)
    {
        var faults = new List<string>();
        foreach (JsonElement item in Scripts(root))
        {
            string name = Text(item, RimRunnerLiveContract.Actions.Name) ?? "?";
            string fault = Text(item, RimRunnerLiveContract.Actions.Fault) ?? "";
            switch (Text(item, RimRunnerLiveContract.Actions.State))
            {
                case RimRunnerLiveContract.Actions.StateCompileError:
                    faults.Add($"  {name}：编译失败");
                    faults.AddRange(fault.Split('\n').Select(line => "    " + line));
                    break;
                case RimRunnerLiveContract.Actions.StateRegisterError:
                    faults.Add($"  {name}：{fault}");
                    break;
            }
        }

        string[] unloaded = [.. Scripts(root)
            .Where(item => Text(item, RimRunnerLiveContract.Actions.State) == RimRunnerLiveContract.Actions.StateUnloaded)
            .Select(item => Text(item, RimRunnerLiveContract.Actions.Name) ?? "?")];
        if (unloaded.Length > 0)
            lines.Add($"实例忙（{Text(root, RimRunnerLiveContract.Actions.Busy)}），{unloaded.Length} 个脚本未装载、未列出：{string.Join("、", unloaded)}");

        int count = faults.Count(line => !line.StartsWith("    ", StringComparison.Ordinal));
        exitCode = count > 0 ? 1 : 0;
        if (count > 0)
        {
            lines.Add($"[FAIL] 有 {count} 个脚本编译失败或无法装载，不执行：");
            lines.AddRange(faults);
        }

        return lines;
    }

    private static IEnumerable<string> ParamLines(JsonElement owner) =>
        owner.GetProperty(RimRunnerLiveContract.Actions.Params).EnumerateArray().Select(param =>
            $"    --{Text(param, RimRunnerLiveContract.Actions.Name)}="
            + (Text(param, RimRunnerLiveContract.Actions.Placeholder) ?? "<值>")
            + (param.GetProperty(RimRunnerLiveContract.Actions.Required).GetBoolean() ? "（必填）" : "")
            + $"  {Text(param, RimRunnerLiveContract.Actions.Meaning)}");

    private static List<string> Logs(JsonElement root) => Strings(root, RimRunnerLiveContract.Drive.Logs);

    private static List<string> Strings(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : [];

    private static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Bool(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    private static int? Int(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number) ? number : null;
}
