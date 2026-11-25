using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

public static class LivePresentation
{
    public static string? SchemaMismatch(JsonElement root, string expected)
    {
        if (!root.TryGetProperty(RimRunnerLiveContract.KeySchema, out JsonElement schema)
            || schema.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(schema.GetString()))
        {
            return $"回包没有 schema 字段（期望 {expected}）";
        }

        string actual = schema.GetString()!;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return $"回包 schema 是 {actual}，本动作要求 {expected}";
        }

        return null;
    }

    private static string? MenuShapeError(JsonElement root)
    {
        if (!root.TryGetProperty(RimRunnerLiveContract.Menu.Open, out JsonElement open))
            return "menu 回包缺少 open";
        if (open.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return "menu 回包的 open 不是布尔值";
        return open.GetBoolean()
               && (!root.TryGetProperty(RimRunnerLiveContract.Menu.Items, out JsonElement options) || options.ValueKind != JsonValueKind.Array)
            ? "menu 回包里已打开的菜单缺少 items 数组"
            : null;
    }

    public static bool TryFormat(
        JsonElement root, string expectedSchema, out IReadOnlyList<string> lines, out string? error)
    {
        error = SchemaMismatch(root, expectedSchema);
        if (error != null)
        {
            lines = [];
            return false;
        }

        if (expectedSchema is RimRunnerLiveContract.Schema.Pawns or RimRunnerLiveContract.Schema.Gizmos or RimRunnerLiveContract.Schema.Dev
                or RimRunnerLiveContract.Schema.Windows
            && (!root.TryGetProperty(RimRunnerLiveContract.Pawns.Items, out JsonElement items)
                || items.ValueKind != JsonValueKind.Array))
        {
            error = expectedSchema + " 回包缺少 items 数组";
            lines = [];
            return false;
        }

        if (expectedSchema == RimRunnerLiveContract.Schema.Menu && MenuShapeError(root) is { } menuError)
        {
            error = menuError;
            lines = [];
            return false;
        }

        lines = expectedSchema switch
        {
            RimRunnerLiveContract.Schema.Pawns => FormatPawns(root),
            RimRunnerLiveContract.Schema.Menu => FormatMenu(root),
            RimRunnerLiveContract.Schema.Windows => FormatWindows(root),
            RimRunnerLiveContract.Schema.Gizmos => FormatGizmos(root),
            RimRunnerLiveContract.Schema.Dev => FormatDev(root),
            RimRunnerLiveContract.Schema.Stat => FormatStat(root),
            RimRunnerLiveContract.Schema.Hediffs => FormatHediffs(root),
            RimRunnerLiveContract.Schema.Screenshot => FormatScreenshot(root),
            RimRunnerLiveContract.Schema.Save => FormatSave(root),
            RimRunnerLiveContract.Schema.Profiler => FormatProfiler(root),
            RimRunnerLiveContract.Schema.Progress => FormatProgress(root, null, latestSegmentOnly: false),
            RimRunnerLiveContract.Schema.Error => FormatError(root),
            _ => [$"未实现的 schema 格式化：{expectedSchema}"],
        };
        return true;
    }

    public static IReadOnlyList<string> FormatError(JsonElement root)
    {
        var lines = new List<string>
        {
            $"[FAIL] {Text(root, RimRunnerLiveContract.Error.Message) ?? "请求失败，回包未给出原因"}",
        };
        if (root.TryGetProperty(RimRunnerLiveContract.Error.State, out JsonElement state)
            && state.ValueKind == JsonValueKind.String)
        {
            lines.Add($"  runState={state.GetString()}");
        }

        if (root.TryGetProperty(RimRunnerLiveContract.Error.Hint, out JsonElement hint)
            && hint.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(hint.GetString()))
        {
            lines.Add("  " + hint.GetString());
        }

        if (root.TryGetProperty(RimRunnerLiveContract.Error.Available, out JsonElement available)
            && available.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (JsonElement item in available.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    names.Add(item.GetString()!);
            }

            lines.Add("  可用：" + (names.Count == 0 ? "（无）" : string.Join(", ", names)));
        }

        return lines;
    }

    public static IReadOnlyList<string> FormatDev(JsonElement root)
    {
        var lines = new List<string>();
        if (Text(root, RimRunnerLiveContract.Dev.Find) is { } find)
        {
            int total = root.TryGetProperty(RimRunnerLiveContract.Dev.Total, out JsonElement count) && count.TryGetInt32(out int n) ? n : 0;
            int shown = root.GetProperty(RimRunnerLiveContract.Dev.Items).GetArrayLength();
            lines.Add($"路径含「{find}」的 {total} 项" + (shown < total ? $"，列前 {shown} 项" : "") + "（只搜已展开的部分）");
        }
        else
        {
            string path = Text(root, RimRunnerLiveContract.Dev.Path) ?? "";
            lines.Add($"{(path.Length == 0 ? "dev 菜单" : path)}（{Text(root, RimRunnerLiveContract.Dev.Kind)}）");
        }

        foreach (JsonElement item in root.GetProperty(RimRunnerLiveContract.Dev.Items).EnumerateArray())
        {
            bool hidden = item.TryGetProperty(RimRunnerLiveContract.Dev.Hidden, out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
            lines.Add($"  {Text(item, RimRunnerLiveContract.Dev.Path)}  {Text(item, RimRunnerLiveContract.Dev.Kind)}{(hidden ? " · 隐藏" : "")}");
        }

        return lines;
    }

    public static IReadOnlyList<string> FormatWindows(JsonElement root)
    {
        JsonElement items = root.GetProperty(RimRunnerLiveContract.Windows.Items);
        if (items.GetArrayLength() == 0)
        {
            return ["窗口栈为空"];
        }

        var lines = new List<string> { $"窗口 {items.GetArrayLength()} 个，从下到上" };
        foreach (JsonElement item in items.EnumerateArray())
        {
            var notes = new List<string> { Text(item, RimRunnerLiveContract.Windows.Layer) ?? "?" };
            if (item.TryGetProperty(RimRunnerLiveContract.Windows.ForcePause, out JsonElement pause) && pause.ValueKind == JsonValueKind.True)
                notes.Add("强制暂停");
            if (item.TryGetProperty(RimRunnerLiveContract.Windows.CloseOnCancel, out JsonElement cancel) && cancel.ValueKind == JsonValueKind.True)
                notes.Add("Esc 关闭");
            string index = item.TryGetProperty(RimRunnerLiveContract.Windows.Index, out JsonElement number) ? number.GetRawText() : "?";
            string title = Text(item, RimRunnerLiveContract.Windows.Title) is { } text ? $"「{text}」" : "";
            lines.Add($"  {index}  {Text(item, RimRunnerLiveContract.Windows.Type)}{title}  {string.Join(" · ", notes)}");
        }

        return lines;
    }

    public static IReadOnlyList<string> FormatMenu(JsonElement root)
    {
        if (!root.GetProperty(RimRunnerLiveContract.Menu.Open).GetBoolean())
        {
            return ["没有打开的右键菜单"];
        }

        JsonElement items = root.GetProperty(RimRunnerLiveContract.Menu.Items);
        var lines = new List<string> { $"右键菜单 {items.GetArrayLength()} 项" };
        foreach (JsonElement item in items.EnumerateArray())
        {
            var notes = new List<string>();
            if (item.TryGetProperty(RimRunnerLiveContract.Menu.Disabled, out JsonElement disabled) && disabled.ValueKind == JsonValueKind.True)
                notes.Add("禁用");
            if (item.TryGetProperty(RimRunnerLiveContract.Menu.Shown, out JsonElement shown) && shown.ValueKind == JsonValueKind.False)
                notes.Add("不在可见区");
            string index = item.TryGetProperty(RimRunnerLiveContract.Menu.Index, out JsonElement number) ? number.GetRawText() : "?";
            lines.Add($"  {index}  {Text(item, RimRunnerLiveContract.Menu.Label)}" + (notes.Count > 0 ? "  " + string.Join(" · ", notes) : ""));
        }

        return lines;
    }

    public static IReadOnlyList<string> FormatGizmos(JsonElement root)
    {
        string[] selected = root.TryGetProperty(RimRunnerLiveContract.Gizmos.Selected, out JsonElement picks) && picks.ValueKind == JsonValueKind.Array
            ? [.. picks.EnumerateArray().Select(item => item.GetString() ?? "?")]
            : [];
        var lines = new List<string> { selected.Length == 0 ? "没有选中" : $"选中 {selected.Length} 个：{string.Join("、", selected)}" };
        if (!root.TryGetProperty(RimRunnerLiveContract.Gizmos.Items, out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            lines.Add("[FAIL] gizmos 回包缺少 items 数组");
            return lines;
        }

        lines.Add($"按钮 {items.GetArrayLength()} 个");
        foreach (JsonElement item in items.EnumerateArray())
        {
            var notes = new List<string> { Text(item, RimRunnerLiveContract.Gizmos.Type) ?? "?" };
            if (item.TryGetProperty(RimRunnerLiveContract.Gizmos.Toggle, out JsonElement toggle) && toggle.ValueKind is JsonValueKind.True or JsonValueKind.False)
                notes.Add(toggle.GetBoolean() ? "开" : "关");
            if (item.TryGetProperty(RimRunnerLiveContract.Gizmos.Group, out JsonElement group) && group.TryGetInt32(out int count) && count > 1)
                notes.Add($"合并 {count} 个");
            if (item.TryGetProperty(RimRunnerLiveContract.Gizmos.Visible, out JsonElement visible) && visible.ValueKind == JsonValueKind.False)
                notes.Add("隐藏");
            if (item.TryGetProperty(RimRunnerLiveContract.Gizmos.Disabled, out JsonElement disabled) && disabled.ValueKind == JsonValueKind.True)
                notes.Add("禁用" + (Text(item, RimRunnerLiveContract.Gizmos.DisabledReason) is { } reason ? "：" + reason : ""));
            string index = item.TryGetProperty(RimRunnerLiveContract.Gizmos.Index, out JsonElement number) ? number.GetRawText() : "?";
            lines.Add($"  {index}  {Text(item, RimRunnerLiveContract.Gizmos.Label)}  {string.Join(" · ", notes)}");
        }

        return lines;
    }

    public static IReadOnlyList<string> FormatPawns(JsonElement root)
    {
        if (!root.TryGetProperty(RimRunnerLiveContract.Pawns.Items, out JsonElement items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return ["[FAIL] pawns 回包缺少 items 数组"];
        }

        var lines = new List<string>();
        foreach (JsonElement pawn in items.EnumerateArray())
        {
            string nick = Text(pawn, RimRunnerLiveContract.Pawns.Nick) ?? "—";
            string id = Text(pawn, RimRunnerLiveContract.Pawns.Id) ?? "—";
            string faction = Text(pawn, RimRunnerLiveContract.Pawns.Faction) ?? "—";
            lines.Add($"{nick}    {id}    {faction}");
        }

        if (lines.Count == 0)
            lines.Add("（地图上没有 spawned pawn）");
        return lines;
    }

    public static IReadOnlyList<string> FormatStat(JsonElement root)
    {
        var lines = new List<string>
        {
            $"{Text(root, RimRunnerLiveContract.Stat.Pawn)}  {Text(root, RimRunnerLiveContract.Stat.StatName)}  {Number(root, RimRunnerLiveContract.Stat.Value)}",
        };
        string? description = Text(root, RimRunnerLiveContract.Stat.Description);
        if (!string.IsNullOrEmpty(description))
            lines.Add(description);
        string? explanation = Text(root, RimRunnerLiveContract.Stat.Explanation);
        if (!string.IsNullOrEmpty(explanation))
            lines.Add(explanation);
        return lines;
    }

    public static IReadOnlyList<string> FormatHediffs(JsonElement root)
    {
        var lines = new List<string>
        {
            $"{Text(root, RimRunnerLiveContract.Hediffs.Pawn)}  painTotal={Number(root, RimRunnerLiveContract.Hediffs.PainTotal)}",
        };
        if (root.TryGetProperty(RimRunnerLiveContract.Hediffs.Items, out JsonElement items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement hediff in items.EnumerateArray())
            {
                string def = Text(hediff, RimRunnerLiveContract.Hediffs.Def) ?? "—";
                string sev = Number(hediff, RimRunnerLiveContract.Hediffs.Severity);
                string part = Text(hediff, RimRunnerLiveContract.Hediffs.Part) ?? "—";
                bool isBad = hediff.TryGetProperty(RimRunnerLiveContract.Hediffs.IsBad, out JsonElement bad)
                             && bad.ValueKind == JsonValueKind.True;
                string ticks = hediff.TryGetProperty(
                                   RimRunnerLiveContract.Hediffs.TicksToDisappear, out JsonElement ttd)
                               && ttd.ValueKind == JsonValueKind.Number
                    ? ttd.GetRawText()
                    : "—";
                lines.Add($"  {def}  sev={sev}  part={part}{(isBad ? "  isBad" : "")}  ticks={ticks}");
                if (hediff.TryGetProperty(RimRunnerLiveContract.Hediffs.StatEntries, out JsonElement entries)
                    && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement entry in entries.EnumerateArray())
                    {
                        lines.Add(
                            $"    {Text(entry, RimRunnerLiveContract.Hediffs.EntryLabel)}  {Text(entry, RimRunnerLiveContract.Hediffs.EntryValue)}");
                    }
                }
            }
        }

        if (root.TryGetProperty(RimRunnerLiveContract.Hediffs.ProductDiagnostics, out JsonElement diagnostics)
            && diagnostics.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(diagnostics.GetString()))
        {
            lines.Add("  productDiagnostics: " + diagnostics.GetString());
        }
        else
        {
            lines.Add("  productDiagnostics: —");
        }

        return lines;
    }

    public static IReadOnlyList<string> FormatScreenshot(JsonElement root)
    {
        string state = Text(root, RimRunnerLiveContract.Screenshot.State) ?? "";
        if (string.Equals(state, RimRunnerLiveContract.ScreenshotState.Error, StringComparison.Ordinal))
        {
            return
            [
                $"[FAIL] 截图失败：{Text(root, RimRunnerLiveContract.Screenshot.Error) ?? "回包未给出原因"}"
            ];
        }

        if (!string.Equals(state, RimRunnerLiveContract.ScreenshotState.Complete, StringComparison.Ordinal))
        {
            return [$"截图 {state}"];
        }

        string path = Text(root, RimRunnerLiveContract.Screenshot.Path) ?? "—";
        string width = Number(root, RimRunnerLiveContract.Screenshot.Width);
        string height = Number(root, RimRunnerLiveContract.Screenshot.Height);
        return [$"已拍  {path}  ({width}x{height})"];
    }

    public static IReadOnlyList<string> FormatSave(JsonElement root)
    {
        bool overwrote = root.TryGetProperty(RimRunnerLiveContract.Save.Overwrote, out JsonElement flag)
                         && flag.ValueKind == JsonValueKind.True;
        return
        [
            $"已存  {Text(root, RimRunnerLiveContract.Save.Saved)}",
            $"  路径  {Text(root, RimRunnerLiveContract.Save.Path)}",
            $"  大小  {Text(root, RimRunnerLiveContract.Save.Bytes)}",
            $"  覆盖  {(overwrote ? "是" : "否")}"
        ];
    }

    public static IReadOnlyList<string> FormatProfiler(JsonElement root)
    {
        var lines = new List<string>();
        if (root.TryGetProperty(RimRunnerLiveContract.Profiler.Armed, out _))
        {
            lines.Add($"armed  {Bool(root, RimRunnerLiveContract.Profiler.Armed)}");
        }

        if (root.TryGetProperty(RimRunnerLiveContract.Profiler.Reset, out JsonElement reset)
            && reset.ValueKind == JsonValueKind.True)
        {
            lines.Add("已清空累积序列");
        }

        if (root.TryGetProperty(RimRunnerLiveContract.Profiler.ArmedGroups, out JsonElement groups)
            && groups.ValueKind == JsonValueKind.Number)
        {
            lines.Add($"armedGroups  {groups.GetRawText()}");
        }

        if (lines.Count == 0)
            lines.Add(root.GetRawText());
        return lines;
    }

    public static IReadOnlyList<string> FormatProgress(
        JsonElement root, DllFreshnessResult? dll, bool latestSegmentOnly)
    {
        var lines = new List<string> { "游戏内：" };
        bool mainThread = !root.TryGetProperty(
                              RimRunnerLiveContract.Progress.MainThreadAvailable, out JsonElement mt)
                          || mt.ValueKind != JsonValueKind.False;
        if (!mainThread)
        {
            bool inBurst = root.TryGetProperty(RimRunnerLiveContract.Progress.Burst, out JsonElement b)
                           && b.ValueKind == JsonValueKind.Object;
            lines.Add("  主线程未应答"
                      + (inBurst ? "：正在 burst 阻塞执行" : "")
                      + "；以下为上次取得的快照");
        }

        string? programState = Text(root, RimRunnerLiveContract.Progress.ProgramState);
        if (programState != null)
            lines.Add($"  ProgramState  {programState}");

        string? runStateText = null;
        if (root.TryGetProperty(RimRunnerPipeProtocol.Keys.RunState, out JsonElement runState)
            && runState.ValueKind == JsonValueKind.String)
        {
            runStateText = runState.GetString();
            lines.Add($"  runState      {runStateText}");
        }

        int done = Int(root, RimRunnerLiveContract.Progress.CasesDone);
        int total = Int(root, RimRunnerLiveContract.Progress.CasesTotal);
        string? current = Text(root, RimRunnerLiveContract.Progress.CurrentCase);
        if (total >= 0 && runStateText != "idle")
        {
            lines.Add($"  进度          {Math.Max(done, 0)}/{total}"
                      + (string.IsNullOrEmpty(current) ? "" : $"  {current}"));
        }

        AppendRunPulse(lines, root, latestSegmentOnly);

        if (root.TryGetProperty(RimRunnerLiveContract.Progress.TestPawns, out JsonElement pawns)
            && pawns.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement pawn in pawns.EnumerateArray())
            {
                string nick = Text(pawn, RimRunnerLiveContract.Pawns.Nick) ?? "—";
                string job = Text(pawn, RimRunnerLiveContract.Progress.Job) ?? "—";
                string pos = Text(pawn, RimRunnerLiveContract.Progress.Position) ?? "—";
                bool downed = pawn.TryGetProperty(RimRunnerLiveContract.Progress.Downed, out JsonElement d)
                              && d.ValueKind == JsonValueKind.True;
                lines.Add($"  {nick}  job={job}  {pos}{(downed ? "  downed" : "")}");
            }
        }

        if (root.TryGetProperty(RimRunnerWireKeys.Progress.LoadedPackIds, out JsonElement packs)
            && packs.ValueKind == JsonValueKind.Array)
        {
            lines.Add($"  已加载 pack   {packs.GetArrayLength()}");
        }

        if (dll != null)
        {
            if (dll.FlowError != null)
                lines.Add($"  dll   无法比对：{dll.FlowError}");
            else if (dll.Stale.Count > 0)
                lines.Add($"  dll   {string.Join("、", dll.Stale)} 与盘上不匹配");
            else
                lines.Add($"  dll   与盘上匹配，比对 {dll.ComparedCount} 个");
            if (dll.Skipped.Count > 0)
                lines.Add($"        未比对（实例未装载）：{string.Join("、", dll.Skipped)}");
        }

        return lines;
    }

    public static IReadOnlyList<string> FormatUnownedErrors(JsonElement root, int screened, out IReadOnlyList<string> logOnly)
    {
        List<string> lines = [];
        List<string> rest = [];
        logOnly = rest;
        int count = Math.Max(0, Int(root, RimRunnerLiveContract.UnownedErrors.Count));
        string? sustained = Text(root, RimRunnerLiveContract.UnownedErrors.Sustained);
        if (count == 0 && sustained == null)
            return lines;

        lines.Add($"[错误级日志] 上一条 live 命令以来，实例在没有用例或驱动执行时出现 {count} 条错误级日志"
                  + "（正文与栈见该实例最近一份 RimRunner 日志的「错误级日志调用栈」条目）");
        if (sustained != null)
            lines.Add("  [持续] " + sustained);

        int listed = 0;
        int listedTimes = 0;
        if (root.TryGetProperty(RimRunnerLiveContract.UnownedErrors.Heads, out JsonElement heads)
            && heads.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement head in heads.EnumerateArray())
            {
                int times = Math.Max(0, Int(head, RimRunnerLiveContract.UnownedErrors.Times));
                string line = $"  ×{times}  {Text(head, RimRunnerLiveContract.UnownedErrors.Head)}";
                (listed < screened ? lines : rest).Add(line);
                listed++;
                listedTimes += times;
            }
        }

        if (listed > screened)
            lines.Add($"  其余 {listed - screened} 种首行见 RimRunner 日志");
        if (count > listedTimes)
            lines.Add($"  其余 {count - listedTimes} 条未分种");
        return lines;
    }

    private static string? Text(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind == JsonValueKind.Null ? null
            : value.GetRawText();
    }

    private const int PulseRowsShown = 6;

    private const int PulseValuesShown = 8;

    private static void AppendRunPulse(List<string> lines, JsonElement root, bool latestSegmentOnly)
    {
        if (root.TryGetProperty(RimRunnerLiveContract.Progress.Burst, out JsonElement burst)
            && burst.ValueKind == JsonValueKind.Object)
        {
            lines.Add($"  推进 tick     {Number(burst, RimRunnerLiveContract.Progress.GameTick)}"
                      + $"（{Age(burst)}）");
        }

        if (!root.TryGetProperty(RimRunnerLiveContract.Progress.Samples, out JsonElement samples)
            || samples.ValueKind != JsonValueKind.Object
            || !samples.TryGetProperty(RimRunnerLiveContract.Progress.Segments, out JsonElement segments)
            || segments.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        string caseId = Text(samples, RimRunnerLiveContract.Progress.CurrentCase) ?? "—";
        int count = samples.TryGetProperty(RimRunnerLiveContract.Progress.SegmentCount, out JsonElement total)
                    && total.TryGetInt32(out int reported)
            ? reported
            : segments.GetArrayLength();
        lines.Add($"  读数          {caseId} 已产 {count} 段"
                  + (latestSegmentOnly && count > 1 ? $"（最新一段；全部见 {FindingPresentation.LiveProgress}）" : ""));
        int skip = latestSegmentOnly ? Math.Max(segments.GetArrayLength() - 1, 0) : 0;
        foreach (JsonElement segment in segments.EnumerateArray().Skip(skip))
        {
            string series = Text(segment, "series") ?? "—";
            string name = Text(segment, "segment") ?? "—";
            lines.Add($"    {series} / {name}  窗 {Number(segment, "windowTicks")}t"
                      + $"  tick {Number(segment, RimRunnerLiveContract.Progress.GameTick)}（{Age(segment)}）");
            if (!segment.TryGetProperty("rows", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
                continue;
            int shown = 0;
            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (shown++ >= PulseRowsShown)
                {
                    lines.Add("      " + FindingPresentation.Omitted(rows.GetArrayLength() - PulseRowsShown, "行", null));
                    break;
                }

                List<string> values = row.TryGetProperty("values", out JsonElement map)
                                      && map.ValueKind == JsonValueKind.Object
                    ? [.. map.EnumerateObject().Select(p => $"{p.Name}={p.Value.GetRawText()}")]
                    : [];
                string shownValues = latestSegmentOnly && values.Count > PulseValuesShown
                    ? string.Join(" ", values.Take(PulseValuesShown)) + "  "
                      + FindingPresentation.Omitted(values.Count - PulseValuesShown, "项",
                          FindingPresentation.InLiveProgress)
                    : string.Join(" ", values);
                lines.Add($"      {Text(row, "key") ?? "—"}  {shownValues}");
            }
        }
    }

    private static string Age(JsonElement element)
    {
        if (!element.TryGetProperty(RimRunnerLiveContract.Progress.AgeMs, out JsonElement age)
            || !age.TryGetInt64(out long ms))
            return "—";
        return ms < 1000 ? "不到 1s 前" : ms < 60_000 ? $"{ms / 1000}s 前" : $"{ms / 60_000}m{ms % 60_000 / 1000:00}s 前";
    }

    private static string Number(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value))
            return "—";
        return value.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
            : "—";
    }

    private static int Int(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
            return -1;
        return value.TryGetInt32(out int n) ? n : -1;
    }

    private static string Bool(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value))
            return "—";
        if (value.ValueKind == JsonValueKind.True) return "true";
        if (value.ValueKind == JsonValueKind.False) return "false";
        return value.GetRawText();
    }
}
