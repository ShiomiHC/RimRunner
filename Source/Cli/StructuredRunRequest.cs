using System.Text.Json;

namespace RimRunnerCli;

public sealed class StructuredRunRequest
{
    private StructuredRunRequest(IEnumerable<string>? caseIds, IEnumerable<string>? excludeIds,
        EnvCheckMode envChecks, IEnumerable<string>? groups)
    {
        CaseIds = NormalizeExclude(caseIds);
        Groups = RimRunner.RimRunnerCaseGroups.Normalize(groups);
        ExcludeIds = NormalizeExclude(excludeIds);
        EnvChecks = envChecks;
    }

    public static StructuredRunRequest Named(IEnumerable<string> caseIds, bool envChecks = false)
    {
        var request = new StructuredRunRequest(caseIds, null, envChecks ? EnvCheckMode.Allowed : EnvCheckMode.Off, null);
        return request.IsNamed ? request : throw new ArgumentException("指定的用例不得为空", nameof(caseIds));
    }

    public static StructuredRunRequest Batch(IEnumerable<string>? excludeIds = null,
        EnvCheckMode envChecks = EnvCheckMode.Off, IEnumerable<string>? groups = null) =>
        new(null, excludeIds, envChecks, groups);

    private const string BatchSegment = "batch";
    private const string BatchSection = "regressions";

    public IReadOnlyList<string> CaseIds { get; }
    public bool IsNamed => CaseIds.Count > 0;
    public IReadOnlyList<string> Groups { get; }
    public IReadOnlyList<string> ExcludeIds { get; }
    public EnvCheckMode EnvChecks { get; }

    public enum EnvCheckMode
    {
        Off,

        Allowed,

        Only,
    }

    public IReadOnlyList<RunInputs.Entry> Inputs { get; private init; } = [];

    public StructuredRunRequest WithInputs(IReadOnlyList<RunInputs.Entry> inputs) =>
        new(CaseIds, ExcludeIds, EnvChecks, Groups)
        {
            Inputs = inputs,
        };

    public IReadOnlyList<string> ExpectedSections => IsNamed ? [] : [BatchSection];

    public static IReadOnlyList<string> ExpectedSectionsFromCommand(string canonicalCommand)
    {
        const string prefix = "select:";
        if (!canonicalCommand.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException(
                "canonical 命令串缺少 select: 前缀：" + canonicalCommand,
                nameof(canonicalCommand));

        string[] segments = canonicalCommand[prefix.Length..].Split(';');
        if (segments.Contains(BatchSegment, StringComparer.Ordinal))
            return [BatchSection];

        return segments.Any(segment => segment.StartsWith("case=", StringComparison.Ordinal))
            ? []
            : throw new ArgumentException(
                "canonical 命令串缺少 case= 与 batch，不是本版引擎写的选择器标签：" + canonicalCommand,
                nameof(canonicalCommand));
    }

    public static IReadOnlyList<string> GroupsFromCommand(string? canonicalCommand)
    {
        const string prefix = "select:";
        const string segmentHead = "group=";
        if (canonicalCommand == null || !canonicalCommand.StartsWith(prefix, StringComparison.Ordinal))
            return [];
        string? segment = canonicalCommand[prefix.Length..].Split(';')
            .FirstOrDefault(item => item.StartsWith(segmentHead, StringComparison.Ordinal));
        return segment == null ? [] : segment[segmentHead.Length..].Split(',');
    }

    private static string[] NormalizeExclude(IEnumerable<string>? ids) =>
    [
        .. (ids ?? [])
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(id => id, StringComparer.Ordinal)
    ];

    public string ExpectedCommand
    {
        get
        {
            var segments = new List<string>();
            if (IsNamed)
            {
                segments.Add("case=" + string.Join(",", CaseIds));
            }
            else
            {
                segments.Add(BatchSegment);
            }

            if (Groups.Count > 0)
                segments.Add("group=" + string.Join(",", Groups));

            if (ExcludeIds.Count > 0)
                segments.Add("exclude=" + string.Join(",", ExcludeIds));
            if (EnvChecks == EnvCheckMode.Allowed)
                segments.Add("checks");
            else if (EnvChecks == EnvCheckMode.Only)
                segments.Add("checks-only");
            return "select:" + string.Join(";", segments);
        }
    }

    public void WriteParams(Utf8JsonWriter writer, string runId,
        IEnumerable<AcknowledgeStartupErrorPolicy.Ack>? startupAcks = null,
        bool cleanSave = false, bool exclusive = false, IEnumerable<CliBatchWarnings.Entry>? warnings = null,
        IEnumerable<AcknowledgePolicy.Ack>? acks = null,
        IEnumerable<KnownFailingPolicy.Ack>? knownFailing = null, bool saveProbe = false,
        IEnumerable<ComponentExemptionPolicy.Exemption>? componentExemptions = null,
        IReadOnlyCollection<string>? warningChecks = null)
    {
        if (IsNamed)
            WriteTexts(writer, "case", CaseIds);
        else
            writer.WriteBoolean(BatchSegment, true);
        if (Groups.Count > 0)
            WriteTexts(writer, "group", Groups);
        if (ExcludeIds.Count > 0)
            WriteTexts(writer, "exclude", ExcludeIds);
        if (EnvChecks == EnvCheckMode.Allowed)
            writer.WriteBoolean("checks", true);
        else if (EnvChecks == EnvCheckMode.Only)
            writer.WriteBoolean("checksOnly", true);
        WritePairs(writer, "ackStartup", (startupAcks ?? []).Select(ack => (ack.Pattern, ack.Reason)));
        WritePairs(writer, "warn", (warnings ?? []).Select(warning => (warning.Key, warning.Text)));
        if (warningChecks is { Count: > 0 })
            WriteTexts(writer, RimRunner.RimRunnerWireKeys.RunRequest.WarnChecked, warningChecks);
        WritePairs(writer, "ack", (acks ?? []).Select(ack => (ack.Key, ack.Reason)));
        WritePairs(writer, "knownFailing", (knownFailing ?? []).Select(ack => (ack.Id, ack.Reason)));
        WritePairs(writer, RimRunner.RimRunnerWireKeys.RunRequest.ComponentExemption,
            (componentExemptions ?? []).Select(exemption => (exemption.Target, exemption.Reason)));
        WriteInputs(writer);
        if (cleanSave)
            writer.WriteBoolean("cleanSave", true);
        if (exclusive)
            writer.WriteBoolean("exclusive", true);
        if (saveProbe)
            writer.WriteBoolean(RimRunner.RimRunnerWireKeys.RunRequest.SaveProbe, true);
        writer.WriteString("runId", runId);
    }

    public void WriteInputs(Utf8JsonWriter writer) =>
        WritePairs(writer, RimRunner.RimRunnerWireKeys.RunRequest.Inputs, Inputs.Select(input => (input.Path, input.Text)));

    internal string? OversizeNote()
    {
        if (Inputs.Count == 0) return null;
        var sizes = Inputs.Select(input => (input.Path, Bytes: EncodedTextBytes(input.Text))).ToArray();
        (string Path, long Bytes) largest = sizes.MaxBy(size => size.Bytes);
        return $"输入件正文编码后共 {sizes.Sum(size => size.Bytes)} 字节"
               + $"（最大一件 {largest.Path}：{largest.Bytes} 字节）";
    }

    private static long EncodedTextBytes(string text)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(text);
        }

        return buffer.Length;
    }

    private static void WriteTexts(Utf8JsonWriter writer, string key, IEnumerable<string> values)
    {
        writer.WriteStartArray(key);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WritePairs(Utf8JsonWriter writer, string key, IEnumerable<(string Key, string Text)> pairs)
    {
        bool started = false;
        foreach ((string pairKey, string text) in pairs)
        {
            if (!started)
            {
                writer.WriteStartArray(key);
                started = true;
            }

            writer.WriteStartObject();
            writer.WriteString(RimRunner.RimRunnerPipeProtocol.PairKey, pairKey);
            writer.WriteString(RimRunner.RimRunnerPipeProtocol.PairText, text);
            writer.WriteEndObject();
        }

        if (started)
            writer.WriteEndArray();
    }

    public static StructuredRunRequest Parse(
        IReadOnlyList<string>? caseIds,
        IEnumerable<string>? excludeIds = null,
        bool includeExcluded = false, bool envChecksOnly = false,
        IReadOnlyList<string>? cliGroups = null, IReadOnlyList<string>? defaultGroups = null)
    {
        string[] resolvedExclude = includeExcluded ? [] : NormalizeExclude(excludeIds);
        bool named = caseIds is { Count: > 0 };

        if (named)
        {
            if (caseIds!.Any(string.IsNullOrWhiteSpace))
                throw new StoreException("run 的用例 Id 含空项");
            string[] duplicates =
            [
                .. caseIds!.GroupBy(id => id, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
            ];
            if (duplicates.Length > 0)
            {
                List<string> logOnly = [];
                string listed = FindingPresentation.ListLine("重复的用例 Id：", duplicates, ",", logOnly);
                logOnly.ForEach(CliLog.Info);
                throw new StoreException(listed);
            }
        }

        if (CombinationError(named, includeExcluded, envChecksOnly, cliGroups) is string combination)
            throw new StoreException(combination);

        if (named)
            return Named(caseIds!, envChecks: true);

        string[] groups = cliGroups != null
            ? cliGroups.SequenceEqual([RimRunner.RimRunnerCaseGroups.AllToken], StringComparer.Ordinal) ? [] : [.. cliGroups]
            : !envChecksOnly ? [.. defaultGroups ?? []] : [];

        if (envChecksOnly)
            return Batch(resolvedExclude, EnvCheckMode.Only);

        return Batch(resolvedExclude, EnvCheckMode.Allowed, groups);
    }

    internal static string? CombinationError(bool named, bool includeExcluded, bool envChecksOnly,
        IReadOnlyList<string>? cliGroups)
    {
        if (named)
        {
            string[] given =
            [
                .. envChecksOnly ? ["--env-checks"] : Array.Empty<string>(),
                .. cliGroups != null ? ["--group"] : Array.Empty<string>(),
                .. includeExcluded ? ["--include-excluded"] : Array.Empty<string>(),
            ];
            if (given.Length > 0)
                return $"用例 Id 不能与 {string.Join("、", given)} 同时传入";
        }

        if (envChecksOnly && cliGroups != null)
            return "--group 不能与 --env-checks 同时传入：环境体检不受组过滤";
        if (cliGroups != null && cliGroups.Contains(RimRunner.RimRunnerCaseGroups.AllToken, StringComparer.Ordinal)
            && cliGroups.Count > 1)
            return "--group " + RimRunner.RimRunnerCaseGroups.AllToken + " 表示不按组过滤，不能与别的组同时传入";
        return null;
    }

    public StructuredRunRequest WithoutEnvChecks() =>
        EnvChecks != EnvCheckMode.Allowed
            ? this
            : new StructuredRunRequest(CaseIds, ExcludeIds, EnvCheckMode.Off, Groups)
            {
                Inputs = Inputs,
            };
}
