using System.Collections;
using System.Text.Json;

namespace RimRunner;

/// <summary>DPA 桥：驱动 Dubs Performance Analyzer 探针，一 tick 一周期。</summary>
public static class RimRunnerProfilerBridge
{
    private const int RecordsHeld = 2000;

    private static MethodInfo? updateCycle;
    private static object? productEntry;
    private static MethodInfo? entryPatchMethods;
    private static PropertyInfo? profilesProperty;

    /// <summary>探针是否已装上并启用。</summary>
    public static bool Armed { get; private set; }

    private static long cyclesSinceReset;

    internal static void SampleTick()
    {
        if (!Armed) return;
        updateCycle!.Invoke(null, null);
        cyclesSinceReset++;
    }

    /// <summary>装探针：给本产品在 DPA 里的条目装探针并开始采样。</summary>
    /// <remarks>已启用时直接返回 null。返回 null 时条目的每个方法都已挂上探针；有一个未挂上、或有共享泛型方法，即返回失败原因并点名。</remarks>
    /// <returns>null = 成功；失败时是原因。</returns>
    public static string? Arm()
    {
        if (Armed) return null;

        Type analyzerType = AccessTools.TypeByName("Analyzer.Profiling.Analyzer");
        if (analyzerType == null)
        {
            return "无法解析 Analyzer.Profiling.Analyzer（mod dubwise.dubsperformanceanalyzer.steam）";
        }

        Type entryType = AccessTools.TypeByName("Analyzer.Profiling.Entry");
        Type controllerType = AccessTools.TypeByName("Analyzer.Profiling.ProfileController");
        if (entryType == null || controllerType == null)
        {
            return "[engine] DPA 已加载但内部类型缺失（Entry、ProfileController）—— DPA 的版本与本桥不匹配";
        }

        updateCycle = AccessTools.Method(analyzerType, "UpdateCycle");
        profilesProperty = AccessTools.Property(controllerType, "Profiles");
        entryPatchMethods = AccessTools.Method(entryType, "PatchMethods");
        MethodInfo beginProfiling = AccessTools.Method(analyzerType, "BeginProfiling");
        MethodInfo entrySetActive = AccessTools.Method(entryType, "SetActive");
        if (updateCycle == null || profilesProperty == null || entryPatchMethods == null
            || beginProfiling == null || entrySetActive == null)
        {
            return "[engine] DPA 已加载但成员签名与本桥不匹配（UpdateCycle / Profiles / PatchMethods / "
                   + "BeginProfiling / SetActive）";
        }

        string? nodeName = RimRunnerProduct.Capabilities.ProfilerEntryNodeName;
        if (nodeName == null)
        {
            return "本产品的描述符没有登记 DPA 入口节点名（Configure 里没有 c.ProfilerEntryNode）——"
                   + "profiler 相关能力对该产品不可用";
        }

        productEntry = FindProductEntry(entryType, nodeName);
        if (productEntry == null)
        {
            return $"DPA 的 Entry 表里没有名字含「{nodeName}」的条目"
                   + "（DPA 只读取启用 mod 根目录下的 Analyzer.xml）";
        }

        entrySetActive.Invoke(productEntry, [true]);
        string? patchFailure = PatchAndVerify(productEntry, nodeName);
        if (patchFailure != null) return patchFailure;
        beginProfiling.Invoke(null, null);
        Armed = true;
        cyclesSinceReset = 0;
        ResetGeneration = RimRunnerTickRatePin.Generation;
        return null;
    }

    /// <summary>给一组本 mod 之外的方法另建 DPA 条目并装探针。</summary>
    /// <remarks>须先 Arm。返回 null 即组内方法全部挂上。有方法没挂上、方法是共享泛型、组名只留字母后为空，或与已建的 DPA 动态条目同名时，返回原因。同名组本进程只建一次：再调时方法数相同即交回首次结果，不同即失败。用例窗里失败另记降级说明。</remarks>
    /// <returns>null = 成功；失败时是原因。</returns>
    public static string? ArmExtraGroup(string groupName, HashSet<MethodInfo> methods)
    {
        string? failure = ArmExtraGroupUnnoted(groupName, methods);
        if (failure != null && RimRunnerContext.AmbientPhase == RimRunnerPhase.Case)
        {
            RimRunnerContext.Active!.Current!.degradedNotes.Add(
                "profiler 对照组 " + groupName + " 未安装：" + failure + "；该组读数缺失或不可比");
        }

        return failure;
    }

    internal static string? ArmExtraGroupLive(string groupName, HashSet<MethodInfo> methods)
        => ArmExtraGroupUnnoted(groupName, methods);

    private static string? ArmExtraGroupUnnoted(string groupName, HashSet<MethodInfo> methods)
    {
        if (!Armed) return "无法添加对照组：profiler 未启用";
        if (methods == null || methods.Count == 0) return "方法集合为空：" + groupName;

        if (extraGroups.TryGetValue(groupName, out ExtraGroup? built))
        {
            if (built.MethodCount != methods.Count)
            {
                return "组 " + groupName + " 已在本进程创建（" + built.MethodCount + " 个方法），本轮收集到 "
                       + methods.Count + " 个，两轮读数不可比";
            }

            return built.Failure;
        }

        string? sharedRefusal = SharedGenericRefusal(methods);
        if (sharedRefusal != null) return sharedRefusal;

        Type builderType = AccessTools.TypeByName("Analyzer.Profiling.DynamicTypeBuilder");
        Type entryType = AccessTools.TypeByName("Analyzer.Profiling.Entry");
        Type categoryType = AccessTools.TypeByName("Analyzer.Profiling.Category");
        if (builderType == null || entryType == null || categoryType == null)
        {
            return "DPA 内部类型缺失（DynamicTypeBuilder / Entry / Category）";
        }

        MethodInfo createType = AccessTools.Method(builderType, "CreateType");
        MethodInfo entryCreate = AccessTools.Method(entryType, "Create");
        MethodInfo setActive = AccessTools.Method(entryType, "SetActive");
        MethodInfo patchMethods = AccessTools.Method(entryType, "PatchMethods");
        if (createType == null || entryCreate == null || setActive == null || patchMethods == null)
        {
            return "[engine] DPA 的动态条目构造面与本桥不匹配（CreateType / Entry.Create / SetActive / PatchMethods）";
        }

        string typeName = string.Concat(groupName.Where(x => char.IsLetter(x) && !char.IsSymbol(x) && !char.IsWhiteSpace(x)));
        if (typeName.Length == 0)
        {
            return "组名 " + groupName + " 只留字母后为空，DPA 以它作类型名";
        }

        if (AccessTools.Field(builderType, "methods")?.GetValue(null) is not IDictionary builtTypes)
        {
            return "[engine] DPA 的动态条目构造面与本桥不匹配（DynamicTypeBuilder.methods）";
        }

        if (builtTypes.Contains(typeName))
        {
            return "组名 " + groupName + " 只留字母后是 " + typeName + "，DPA 里已有同名的动态条目";
        }

        object? dynType = createType.Invoke(null, [groupName, methods]);
        if (dynType is not Type entryTypeArg) return "CreateType 未返回类型：" + groupName;

        object tickCategory = Enum.Parse(categoryType, "Tick");
        object? entry = entryCreate.Invoke(null,
            [groupName, tickCategory, entryTypeArg, true, true]);
        if (entry == null) return "Entry.Create 返回 null：" + groupName;

        setActive.Invoke(entry, [true]);
        string? failure = PatchAndVerify(entry, groupName);
        extraGroups[groupName] = new ExtraGroup(methods.Count, failure);
        return failure;
    }

    private sealed record ExtraGroup(int MethodCount, string? Failure);

    private static readonly Dictionary<string, ExtraGroup> extraGroups = [];

    private static string? PatchAndVerify(object entry, string label)
    {
        Type entryType = entry.GetType();
        FieldInfo? threadedOff = AccessTools.Field(AccessTools.TypeByName("Analyzer.Settings"), "disableThreadedPatching");
        Type? transplanting = AccessTools.TypeByName("Analyzer.Profiling.MethodTransplanting");
        Type? dynType = AccessTools.Field(entryType, "type")?.GetValue(entry) as Type;
        MethodInfo? getPatchMethods = dynType?.GetMethod("GetPatchMethods", BindingFlags.Static | BindingFlags.Public);
        if (threadedOff == null || transplanting == null || getPatchMethods == null)
        {
            return "[engine] DPA 的打补丁面与本桥不匹配（Settings.disableThreadedPatching / MethodTransplanting / "
                   + "条目类型的 GetPatchMethods）";
        }

        List<MethodInfo> targets = getPatchMethods.Invoke(null, null) is IEnumerable<MethodInfo> listed ? [.. listed] : [];
        if (targets.Count == 0) return "DPA 条目 " + label + " 列出的方法数为 0";

        string? sharedRefusal = SharedGenericRefusal(targets);
        if (sharedRefusal != null) return sharedRefusal;

        bool threadedWasOff = (bool)threadedOff.GetValue(null)!;
        threadedOff.SetValue(null, true);
        try
        {
            entryPatchMethods!.Invoke(entry, null);
        }
        catch (TargetInvocationException e)
        {
            Exception inner = e.InnerException ?? e;
            return "DPA 给 " + label + " 打补丁时抛出 " + inner.GetType().Name + "：" + inner.Message;
        }
        finally
        {
            threadedOff.SetValue(null, threadedWasOff);
        }

        List<MethodInfo> missing = targets.Where(target => !CarriesTranspilerFrom(target, transplanting)).ToList();
        if (missing.Count == 0) return null;
        return label + " 有 " + missing.Count + "/" + targets.Count + " 个方法未挂上 DPA 探针："
               + ListMethods(missing);
    }

    private static bool CarriesTranspilerFrom(MethodInfo target, Type patchOwner)
    {
        Patches? info = Harmony.GetPatchInfo(target);
        return info != null && info.Transpilers.Any(patch => patch.PatchMethod.DeclaringType == patchOwner);
    }

    private static string? SharedGenericRefusal(IEnumerable<MethodInfo> methods)
    {
        List<MethodInfo> shared = methods.Where(method => RimRunnerPatchGate.IsSharedGeneric(method)).ToList();
        if (shared.Count == 0) return null;
        return shared.Count + " 个方法是共享泛型（泛型实参含引用类型），补丁在重编后失效，不安装：" + ListMethods(shared);
    }

    private static string ListMethods(List<MethodInfo> methods) =>
        string.Join("、", methods.Select(method => method.DeclaringType?.FullName + ":" + method.Name));

    private static object? FindProductEntry(Type entryType, string nodeName)
    {
        FieldInfo entriesField = AccessTools.Field(entryType, "entries");
        if (entriesField == null) return null;
        if (entriesField.GetValue(null) is not IEnumerable entries) return null;

        FieldInfo nameField = AccessTools.Field(entryType, "name");
        if (nameField == null) return null;

        foreach (object entry in entries)
        {
            if (nameField.GetValue(entry) is string name && name.Contains(nodeName)) return entry;
        }

        return null;
    }

    /// <summary>清空全部 profiler 累积序列；null = 成功。</summary>
    /// <remarks>Reset 之后 tick 分档换过档，Dump 与分段摘要一族即抛。</remarks>
    public static string? Reset()
    {
        if (!Armed) return "profiler 未启用";
        foreach (object profiler in EnumerateProfilers(out _))
        {
            AccessTools.Method(profiler.GetType(), "Reset").Invoke(profiler, null);
        }

        cyclesSinceReset = 0;
        ResetGeneration = RimRunnerTickRatePin.Generation;
        return null;
    }

    internal static int ResetGeneration = -1;

    internal const string TickRateChangedReason = "上次 Reset（或 Arm）之后 tick 分档已改变，本段样本混入两档的结算频率";

    private static void RequireSingleTickRate()
    {
        if (ResetGeneration < 0 || ResetGeneration == RimRunnerTickRatePin.Generation) return;
        throw new InvalidOperationException(RimRunnerLog.Prefix + " RimRunnerProfilerBridge 读取样本："
            + TickRateChangedReason);
    }

    /// <summary>导出逐周期原始序列文本；prefix null = 全部。</summary>
    /// <remarks>JSON；times / hits 为环形缓冲、currentIndex 为写入位；cyclesSinceReset 超过 recordsHeld 即已绕圈，hits 为 0 的格子 times 可能属上一圈。未 arm 时返回 null。</remarks>
    /// <exception cref="InvalidOperationException">上次 Reset 之后 tick 分档换过档时抛。</exception>
    public static string? Dump(string? prefix, out string? error)
    {
        RequireSingleTickRate();
        error = ArmedRefusalOrNull();
        return error != null ? null : DumpDocument(EnumerateProfilers(out _), prefix);
    }

    internal static string DumpDocument(IEnumerable<object> profilers, string? prefix)
    {
        return RimRunnerJson.Write(writer =>
        {
            writer.WriteStartObject();
            AppendDump(writer, profilers, prefix);
            writer.WriteEndObject();
        }, false);
    }

    internal static string? ArmedRefusalOrNull()
    {
        return Armed ? null : "profiler 未启用";
    }

    internal static void AppendDump(Utf8JsonWriter writer, string? prefix)
    {
        AppendDump(writer, EnumerateProfilers(out _), prefix);
    }

    private static void AppendDump(Utf8JsonWriter writer, IEnumerable<object> profilers, string? prefix)
    {
        writer.WriteNumber("recordsHeld", RecordsHeld);
        writer.WriteNumber("cyclesSinceReset", cyclesSinceReset);
        writer.WriteStartArray("profilers");
        foreach (object profiler in profilers)
        {
            Type type = profiler.GetType();
            string? key = AccessTools.Field(type, "key").GetValue(profiler) as string;
            if (prefix != null && (key == null || !key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            double[] times = (double[])AccessTools.Field(type, "times").GetValue(profiler)!;
            int[] hits = (int[])AccessTools.Field(type, "hits").GetValue(profiler)!;
            uint currentIndex = (uint)AccessTools.Field(type, "currentIndex").GetValue(profiler)!;

            writer.WriteStartObject();
            writer.WriteString("key", key);
            writer.WriteString("label", AccessTools.Field(type, "label").GetValue(profiler) as string);
            writer.WriteNumber("currentIndex", currentIndex);
            AppendDoubleArray(writer, "times", times);
            AppendIntArray(writer, "hits", hits);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>分段摘要的结构化行集。</summary>
    /// <remarks>聚合的是 Arm 或上次 Reset 以来的周期；只收本段被调到过的方法。</remarks>
    /// <exception cref="InvalidOperationException">profiler 未启用，自 Arm 或上次 Reset 起已过 2000 个周期，或其间 tick 分档换过档时抛。</exception>
    public static List<RimRunnerProfilerRow> SummarizeSegmentRows(string? prefix)
    {
        RequireSingleTickRate();
        if (!Armed)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 无法获取分段摘要：profiler 未启用");
        }

        if (cyclesSinceReset > RecordsHeld)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 自上次 Reset 已过 " + cyclesSinceReset
                + " 个周期，超过 DPA 环形缓冲的 " + RecordsHeld + " 格，读数掺入上一圈的值");
        }

        List<RimRunnerProfilerRow> rows = [];
        foreach (object profiler in EnumerateProfilers(out _))
        {
            Type type = profiler.GetType();
            string? key = AccessTools.Field(type, "key").GetValue(profiler) as string;
            if (prefix != null && (key == null || !key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            double[] times = (double[])AccessTools.Field(type, "times").GetValue(profiler)!;
            int[] hits = (int[])AccessTools.Field(type, "hits").GetValue(profiler)!;

            double totalMs = 0d;
            double peakMs = 0d;
            long totalHits = 0;
            int activeTicks = 0;
            for (int i = 0; i < times.Length; i++)
            {
                if (times[i] > 0d) activeTicks++;
                totalMs += times[i];
                if (times[i] > peakMs) peakMs = times[i];
                totalHits += hits[i];
            }

            if (totalHits == 0) continue;

            rows.Add(new RimRunnerProfilerRow
            {
                Key = key,
                TotalMs = totalMs,
                PeakTickMs = peakMs,
                Calls = totalHits,
                ActiveTicks = activeTicks,
            });
        }

        return rows;
    }

    /// <summary>本轮启用几个 profiler（口径量）。</summary>
    public static int ArmedProfilerCount
    {
        get
        {
            if (!Armed) return 0;
            EnumerateProfilers(out int count);
            return count;
        }
    }

    /// <summary>分段摘要转成 AddSample 的行集。</summary>
    /// <remarks>行同 SummarizeSegmentRows，键为 profiler key，值为 totalMs、peakTickMs、calls、activeTicks。</remarks>
    /// <exception cref="InvalidOperationException">同 SummarizeSegmentRows。</exception>
    public static List<RimRunnerSampleRow> SampleRows(string? prefix)
    {
        List<RimRunnerSampleRow> rows = [];
        foreach (RimRunnerProfilerRow row in SummarizeSegmentRows(prefix))
        {
            rows.Add(new RimRunnerSampleRow(row.Key!)
                .Set("totalMs", row.TotalMs)
                .Set("peakTickMs", row.PeakTickMs)
                .Set("calls", row.Calls)
                .Set("activeTicks", row.ActiveTicks));
        }

        return rows;
    }

    /// <summary>分段摘要文本：各方法本段的合计、峰值、每 tick 均值、调用次数与耗时非零的 tick 数。</summary>
    /// <remarks>行同 SummarizeSegmentRows；msPerTick 按 segmentTicks 除。</remarks>
    /// <exception cref="InvalidOperationException">同 SummarizeSegmentRows。</exception>
    public static string SummarizeSegment(string? prefix, string segmentLabel, int segmentTicks)
    {
        return RimRunnerJson.Write(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("segment", segmentLabel);
                writer.WriteNumber("ticks", segmentTicks);
                writer.WriteStartArray("methods");
                foreach (RimRunnerProfilerRow row in SummarizeSegmentRows(prefix))
                {
                    writer.WriteStartObject();
                    writer.WriteString("key", row.Key);
                    WriteRounded(writer, "totalMs", row.TotalMs);
                    WriteRounded(writer, "peakTickMs", row.PeakTickMs);
                    WriteRounded(writer, "msPerTick", segmentTicks > 0 ? row.TotalMs / segmentTicks : 0d);
                    writer.WriteNumber("calls", row.Calls);
                    writer.WriteNumber("activeTicks", row.ActiveTicks);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            false);
    }

    private static void WriteRounded(Utf8JsonWriter writer, string key, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            writer.WriteNull(key);
            return;
        }

        writer.WriteNumber(key, Math.Round(value, 6));
    }

    private static IEnumerable<object> EnumerateProfilers(out int count)
    {
        count = 0;
        List<object> result = [];
        if (profilesProperty!.GetValue(null) is not IEnumerable pairs) return result;

        foreach (object pair in pairs)
        {
            object? value = pair.GetType().GetProperty("Value")?.GetValue(pair);
            if (value != null) result.Add(value);
        }

        count = result.Count;
        return result;
    }

    private static void AppendDoubleArray(Utf8JsonWriter writer, string name, double[] values)
    {
        writer.WriteStartArray(name);
        int last = LastNonZero(values);
        for (int i = 0; i <= last; i++)
        {
            if (double.IsNaN(values[i]) || double.IsInfinity(values[i]))
            {
                writer.WriteNullValue();
                continue;
            }

            writer.WriteNumberValue(values[i]);
        }

        writer.WriteEndArray();
    }

    private static void AppendIntArray(Utf8JsonWriter writer, string name, int[] values)
    {
        writer.WriteStartArray(name);
        int last = LastNonZero(values);
        for (int i = 0; i <= last; i++)
        {
            writer.WriteNumberValue(values[i]);
        }

        writer.WriteEndArray();
    }

    private static int LastNonZero(double[] values)
    {
        for (int i = values.Length - 1; i >= 0; i--)
            if (values[i] != 0d)
                return i;
        return -1;
    }

    private static int LastNonZero(int[] values)
    {
        for (int i = values.Length - 1; i >= 0; i--)
            if (values[i] != 0)
                return i;
        return -1;
    }
}
