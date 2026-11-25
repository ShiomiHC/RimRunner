using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

public static class ShowcasePresentation
{
    public static bool IsSettled(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool reloading = root.TryGetProperty(RimRunnerLiveContract.Showcase.Reloading, out JsonElement flag)
                         && flag.ValueKind == JsonValueKind.True;
        string? state = Text(root, RimRunnerLiveContract.Showcase.State);
        return !reloading
               && state != RimRunnerLiveContract.Showcase.StateStaging
               && state != RimRunnerLiveContract.Showcase.StateTearingDown
               && state != RimRunnerLiveContract.Showcase.StateStepping;
    }

    public static IReadOnlyList<string> Format(int status, string body, out int exitCode)
    {
        var lines = new List<string>();
        if (!TryParseShowcase(status, body, lines, out JsonDocument? doc, out exitCode))
            return lines;

        using (doc)
        {
            return FormatWhole(doc!.RootElement, lines, out exitCode);
        }
    }

    public static IReadOnlyList<string> FormatStep(int status, string body, out int exitCode)
    {
        var lines = new List<string>();
        if (!TryParseShowcase(status, body, lines, out JsonDocument? doc, out exitCode))
            return lines;

        using (doc)
        {
            JsonElement root = doc!.RootElement;
            if (!root.TryGetProperty(RimRunnerLiveContract.Showcase.LastStep, out JsonElement lastStep)
                || lastStep.ValueKind != JsonValueKind.Object)
            {
                return FormatWhole(root, lines, out exitCode);
            }

            string? id = Text(root, RimRunnerLiveContract.Showcase.Id);
            string stepName = Text(lastStep, RimRunnerLiveContract.Showcase.SteppingName) ?? "";
            string? abort = Text(root, RimRunnerLiveContract.Showcase.AbortReason);
            List<string> stepAborts = Texts(lastStep, RimRunnerLiveContract.Showcase.StepAborts);
            bool failed = abort != null || stepAborts.Count > 0;
            string head = Text(root, RimRunnerLiveContract.Showcase.State) == RimRunnerLiveContract.Showcase.StateStepping
                ? DescribeState(root)
                : "步骤「" + stepName + "」" + (failed ? "中止" : "完成");
            lines.Add("[showcase] " + (id ?? "无场") + " · " + head);

            if (abort != null) lines.Add("  [中止] " + abort);
            lines.AddRange(stepAborts.Select(line => "  [步骤中止] " + line));
            AppendWarnings(lastStep, lines);
            lines.AddRange(Texts(lastStep, RimRunnerLiveContract.Showcase.Messages).Select(line => "  [消息] " + line));
            AppendOperationErrors(lastStep, lines);

            exitCode = failed ? 1 : 0;
            return lines;
        }
    }

    private static IReadOnlyList<string> FormatWhole(JsonElement root, List<string> lines, out int exitCode)
    {
        string? id = Text(root, RimRunnerLiveContract.Showcase.Id);
        string? title = Text(root, RimRunnerLiveContract.Showcase.Title);
        lines.Add("[showcase] " + (id == null ? "无场" : id + " · " + title) + " · " + DescribeState(root));

        if (Drives(root) is var drives and > 0)
            lines.Add($"  [驱动] 展台实例已受理 {drives} 次 live 驱动，驱动改过的进程状态不还原");
        string? abort = Text(root, RimRunnerLiveContract.Showcase.AbortReason);
        if (abort != null) lines.Add("  [中止] " + abort);
        List<string> stagingErrors = Texts(root, RimRunnerLiveContract.Showcase.StagingErrors);
        lines.AddRange(stagingErrors.Select(line => "  [装配段错误级日志] " + line));
        List<string> stepAborts = Texts(root, RimRunnerLiveContract.Showcase.StepAborts);
        lines.AddRange(stepAborts.Select(line => "  [步骤中止] " + line));
        AppendWarnings(root, lines);

        lines.AddRange(Texts(root, RimRunnerLiveContract.Showcase.Messages).Select(line => "  [消息] " + line));
        lines.AddRange(Texts(root, RimRunnerLiveContract.Showcase.RegistryFaults).Select(line => "  [名册错误] " + line));
        AppendOperationErrors(root, lines);
        foreach ((string key, string tag) in BootErrorKinds)
            lines.AddRange(Texts(root, key).Select(line => "  [" + tag + "] " + line));
        lines.AddRange(Texts(root, RimRunnerLiveContract.Showcase.ReloadErrors).Select(line => "  [载档错误级日志] " + line));

        exitCode = abort != null || stagingErrors.Count > 0 || stepAborts.Count > 0 ? 1 : 0;
        return lines;
    }

    private static readonly (string Key, string Tag)[] BootErrorKinds =
    [
        (RimRunnerLiveContract.Showcase.StartupOwnErrors, "本产品侧启动错误级日志"),
        (RimRunnerLiveContract.Showcase.StartupForeignErrors, "他 mod 启动错误级日志"),
        (RimRunnerLiveContract.Showcase.StartupUnattributedErrors, "来源未定的启动错误级日志"),
        (RimRunnerLiveContract.Showcase.ColdLoadErrors, "载档错误级日志"),
    ];

    private static bool TryParseShowcase(int status, string body, List<string> lines, out JsonDocument? doc,
        out int exitCode)
    {
        doc = null;
        if (status == 0)
        {
            lines.Add("[FAIL] 展台实例无应答：" + body);
            exitCode = ExitCodes.Incomplete;
            return false;
        }

        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            lines.Add($"[FAIL] 回包不是 JSON（状态码 {status}）");
            exitCode = ExitCodes.Incomplete;
            return false;
        }

        JsonElement root = doc.RootElement;
        if (status is < 200 or >= 300)
        {
            lines.AddRange(LivePresentation.FormatError(root));
            doc.Dispose();
            doc = null;
            exitCode = ExitCodes.Incomplete;
            return false;
        }

        string? mismatch = LivePresentation.SchemaMismatch(root, RimRunnerLiveContract.Schema.Showcase);
        if (mismatch != null)
        {
            lines.Add("[FAIL] " + mismatch);
            doc.Dispose();
            doc = null;
            exitCode = ExitCodes.Incomplete;
            return false;
        }

        exitCode = 0;
        return true;
    }

    private static void AppendWarnings(JsonElement root, List<string> lines)
    {
        if (!root.TryGetProperty(RimRunnerLiveContract.Showcase.Warnings, out JsonElement warnings)
            || warnings.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement warning in warnings.EnumerateArray())
        {
            string? label = Text(warning, RimRunnerLiveContract.Showcase.WarningLabel);
            string tag = RimRunnerLiveContract.SceneFinding.Tag(Text(warning, RimRunnerLiveContract.SceneFinding.Kind));
            string handle = Text(warning, RimRunnerLiveContract.SceneFinding.Kind) == RimRunnerLiveContract.SceneFinding.Assert
                && warning.TryGetProperty(RimRunnerLiveContract.SceneFinding.Ordinal, out JsonElement ordinal) && ordinal.ValueKind == JsonValueKind.Number
                && FindingPresentation.AssertHandle(ordinal.GetInt32()) is string name ? name + " " : "";
            lines.Add("  [" + tag + "] " + handle + label + "：" + Text(warning, RimRunnerLiveContract.Showcase.WarningDetail));
            List<string> logOnly = [];
            FindingPresentation.Items(Texts(warning, RimRunnerLiveContract.Showcase.WarningOffending),
                FindingPresentation.OffendingPerAssert, "    ", "项", $"[{tag}违例] {label}：", lines, logOnly);
            logOnly.ForEach(CliLog.Info);
        }
    }

    private static void AppendOperationErrors(JsonElement root, List<string> lines)
    {
        if (root.TryGetProperty(RimRunnerLiveContract.Showcase.OperationErrors, out JsonElement count)
            && count.ValueKind == JsonValueKind.Number && count.GetInt32() > 0)
        {
            lines.Add($"  操作段错误级日志 {count.GetInt32()} 条");
        }
    }

    public static string Summary(int status, string body)
    {
        if (status == 503) return "展台：尚未就绪（首次载档未完成）";
        if (status != 200) return $"展台：状态查询失败（状态码 {status}）";
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        string? id = Text(root, RimRunnerLiveContract.Showcase.Id);
        string head = id == null
            ? "展台：无场"
            : "展台：" + id + " · " + Text(root, RimRunnerLiveContract.Showcase.Title);
        List<string?> kinds = root.TryGetProperty(RimRunnerLiveContract.Showcase.Warnings, out JsonElement w)
                              && w.ValueKind == JsonValueKind.Array
            ? [.. w.EnumerateArray().Select(item => Text(item, RimRunnerLiveContract.SceneFinding.Kind))]
            : [];
        int asserts = kinds.Count(kind => kind == RimRunnerLiveContract.SceneFinding.Assert);
        int keyed = kinds.Count(kind => kind == RimRunnerLiveContract.SceneFinding.Warning);
        int unknown = kinds.Count - asserts - keyed;
        int stagingErrors = Texts(root, RimRunnerLiveContract.Showcase.StagingErrors).Count;
        int operationErrors = root.TryGetProperty(RimRunnerLiveContract.Showcase.OperationErrors, out JsonElement o)
                              && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0;
        int bootErrors = BootErrorKinds.Sum(kind => Texts(root, kind.Key).Count);
        int reloadErrors = Texts(root, RimRunnerLiveContract.Showcase.ReloadErrors).Count;
        return head + " · " + DescribeState(root)
               + $"（失败断言 {asserts} · 带键警告 {keyed}" + (unknown > 0 ? $" · 来源未知 {unknown}" : "")
               + $" · 装配段错误级日志 {stagingErrors} · 操作段错误级日志 {operationErrors}"
               + (bootErrors > 0 ? $" · 就绪前错误级日志 {bootErrors}" : "")
               + (reloadErrors > 0 ? $" · 回载错误级日志 {reloadErrors}" : "") + "）"
               + (Drives(root) is var drives and > 0 ? $" · 已受理 {drives} 次 live 驱动" : "");
    }

    private static int Drives(JsonElement root) =>
        root.TryGetProperty(RimRunnerLiveContract.Showcase.Drives, out JsonElement d) && d.ValueKind == JsonValueKind.Number
            ? d.GetInt32()
            : 0;

    private static string DescribeState(JsonElement root)
    {
        string? state = Text(root, RimRunnerLiveContract.Showcase.State);
        if (state == RimRunnerLiveContract.Showcase.StateStepping
            && root.TryGetProperty(RimRunnerLiveContract.Showcase.Stepping, out JsonElement stepping)
            && Text(stepping, RimRunnerLiveContract.Showcase.SteppingName) is string name)
        {
            return "进行中：" + name;
        }

        return StateLabel(state);
    }

    private static string StateLabel(string? state) => state switch
    {
        RimRunnerLiveContract.Showcase.StateNone => "未立场",
        RimRunnerLiveContract.Showcase.StateStaging => "立场中",
        RimRunnerLiveContract.Showcase.StateTearingDown => "拆场中",
        RimRunnerLiveContract.Showcase.StateStaged => "已立场",
        RimRunnerLiveContract.Showcase.StateStepping => "进行中",
        RimRunnerLiveContract.Showcase.StateAborted => "装配中止",
        _ => "未知状态 " + (state ?? "<缺>"),
    };

    private static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> Texts(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ?
            [
                .. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
            ]
            : [];
}
