using System.IO;

namespace RimRunner;

/// <summary>期望表的一条条目：不同 checkKind 共用扁平字段。</summary>
public class RimRunnerExpectationCase
{
    /// <summary>所属期望文件 id。</summary>
    public required string fileId;
    /// <summary>条目 id。</summary>
    /// <remarks>必填，同一文件内不得重复。</remarks>
    public required string caseId;
    /// <summary>检查种类。</summary>
    public string? checkKind;
    /// <summary>被测对象名。</summary>
    public string? subject;
    /// <summary>涉及的 hediff def。</summary>
    public string? hediffDef;
    /// <summary>涉及的 stage 序号。</summary>
    /// <remarks>JSON 未写时为 0。</remarks>
    public int stageIndex;
    /// <summary>涉及的 stat def。</summary>
    public string? statDef;
    /// <summary>期望值。</summary>
    /// <remarks>JSON 未写时为 0。</remarks>
    public float expected;
    /// <summary>容差。</summary>
    /// <remarks>必填，不得为负。</remarks>
    public float tolerance;
    /// <summary>期望值出处文案。</summary>
    public required string source;
}

/// <summary>一份期望 JSON 文件：常量参数 + 条目列表。</summary>
public class RimRunnerExpectationFile
{
    /// <summary>文件 id（= 用例 Id）。</summary>
    public required string id;
    /// <summary>文件说明。</summary>
    public string? note;
    /// <summary>喂给用例的常量（JSON 键 params）。</summary>
    /// <remarks>JSON 里每项写作 {value, source}，两者必填。</remarks>
    public Dictionary<string, float> parameters = new(StringComparer.Ordinal);
    /// <summary>各常量的出处文案。</summary>
    public Dictionary<string, string> paramSources = new(StringComparer.Ordinal);
    /// <summary>条目列表。</summary>
    public List<RimRunnerExpectationCase> cases = [];

    /// <summary>按名取常量，缺即抛。</summary>
    public float GetParam(string name)
    {
        if (!parameters.TryGetValue(name, out float value))
        {
            throw new FormatException(RimRunnerLog.Prefix + " 期望表 " + id
                                      + " 缺少 params." + name);
        }

        return value;
    }
}

/// <summary>期望表读取入口：每次调用都从磁盘重读。</summary>
public static class RimRunnerExpectationStore
{
    /// <summary>经 RimRunnerInput 读期望文件 expectations/{id}.json。</summary>
    /// <remarks>文件缺失按用例前提不成立认领；格式不合即抛，含 id 与参数不匹配、caseId 重复、tolerance 为负、cases 用未经 ExpectationCaseKeys 为本文件登记的键、cases 与 params 都空。</remarks>
    public static RimRunnerExpectationFile Load(string id)
    {
        string path = "expectations/" + id + ".json";
        if (!RimRunnerInput.TryRead(path, out string text))
        {
            RimRunnerFault.Require(false, MissingReason, path);
            throw new FileNotFoundException(path);
        }

        Dictionary<string, object> root = RequireObject(
            RimRunnerJson.Parse(text), "根对象");
        RimRunnerExpectationFile expectation = new()
        {
            id = GetRequiredString(root, "id", "根对象"),
            note = GetString(root, "note")
        };
        if (!string.Equals(expectation.id, id, StringComparison.Ordinal))
        {
            throw new FormatException(RimRunnerLog.Prefix + " 期望表 id 与文件请求不匹配：请求="
                                                          + id + "，JSON=" + expectation.id);
        }

        ReadParams(root, expectation);

        if (!root.TryGetValue("cases", out object? casesValue)
            || RequireArray(casesValue, "cases").Count == 0)
        {
            if (expectation.parameters.Count == 0)
            {
                throw new FormatException(RimRunnerLog.Prefix + " 期望表缺少必填 cases 数组且无 params："
                                                              + id);
            }

            return expectation;
        }

        HashSet<string> caseIds = new(StringComparer.Ordinal);
        foreach (object caseValue in RequireArray(casesValue, "cases"))
        {
            Dictionary<string, object> fields = RequireObject(caseValue, "cases[]");
            string caseId = GetRequiredString(fields, "caseId", id + ".cases[]");
            if (!caseIds.Add(caseId))
            {
                throw new FormatException(RimRunnerLog.Prefix + " 期望表 caseId 重复："
                                                              + id + "/" + caseId);
            }

            RejectUnknownKeys(id, id + "/" + caseId, fields);

            float tolerance = GetRequiredFloat(fields, "tolerance", id + "/" + caseId);
            if (tolerance < 0f)
            {
                throw new FormatException(RimRunnerLog.Prefix + " tolerance 不得为负数："
                                                              + id + "/" + caseId);
            }

            RimRunnerExpectationLedger.NoteLoaded(id, caseId);
            expectation.cases.Add(new RimRunnerExpectationCase
            {
                fileId = id,
                caseId = caseId,
                checkKind = GetString(fields, "checkKind"),
                subject = GetString(fields, "subject"),
                hediffDef = GetString(fields, "hediffDef"),
                stageIndex = GetInt(fields, "stageIndex", 0),
                statDef = GetString(fields, "statDef"),
                expected = GetFloat(fields, "expected", 0f),
                tolerance = tolerance,
                source = GetRequiredString(fields, "source", id + "/" + caseId)
            });
        }

        return expectation;
    }

    internal const string MissingReason = "数据根下缺少期望表";

    private static readonly HashSet<string> ParserKnownKeys = new(StringComparer.Ordinal)
    {
        "caseId", "checkKind", "subject", "hediffDef", "stageIndex", "statDef",
        "expected", "tolerance", "source"
    };

    private static Dictionary<string, HashSet<string>> CaseKeysByFile
    {
        get
        {
            if (field != null)
            {
                return field;
            }

            var built = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string[]> entry
                     in RimRunnerProduct.Capabilities.CaseKeysByFile)
            {
                foreach (string key in entry.Value)
                {
                    if (!ParserKnownKeys.Contains(key))
                    {
                        throw new FormatException(RimRunnerLog.Prefix + " 期望表 " + entry.Key
                            + " 登记解析器无法识别的列 " + key);
                    }
                }

                built[entry.Key] = new HashSet<string>(entry.Value, StringComparer.Ordinal);
            }

            field = built;
            return field;
        }
    }

    private static void RejectUnknownKeys(
        string fileId, string owner, Dictionary<string, object> fields) =>
        RejectUnknownKeys(fileId, owner, fields, CaseKeysByFile);

    internal static void RejectUnknownKeys(
        string fileId, string owner, Dictionary<string, object> fields,
        IReadOnlyDictionary<string, HashSet<string>> allowedByFile)
    {
        if (!allowedByFile.TryGetValue(fileId, out HashSet<string>? allowed))
        {
            throw new FormatException(RimRunnerLog.Prefix + " 期望表 " + fileId
                                      + " 未登记合法键表");
        }

        foreach (string key in fields.Keys)
        {
            if (!allowed.Contains(key))
            {
                throw new FormatException(RimRunnerLog.Prefix + " 期望表 " + owner
                                          + " 含本文件未登记的字段 " + key);
            }
        }
    }

    private static void ReadParams(
        Dictionary<string, object> root, RimRunnerExpectationFile expectation)
    {
        if (!root.TryGetValue("params", out object? paramsValue) || paramsValue == null)
        {
            return;
        }

        foreach (KeyValuePair<string, object> entry in RequireObject(paramsValue, "params"))
        {
            string owner = expectation.id + "/params/" + entry.Key;
            Dictionary<string, object> fields = RequireObject(entry.Value, owner);
            expectation.paramSources[entry.Key] =
                GetRequiredString(fields, "source", owner);
            expectation.parameters[entry.Key] = GetRequiredFloat(fields, "value", owner);
            RimRunnerExpectationLedger.NoteLoadedParam(expectation.id, entry.Key);
        }
    }

    private static Dictionary<string, object> RequireObject(object? value, string fieldName)
    {
        Dictionary<string, object> result = value as Dictionary<string, object> ?? throw new FormatException(
                RimRunnerLog.Prefix + " JSON 字段 " + fieldName + " 须是对象");
        return result;
    }

    private static List<object> RequireArray(object value, string fieldName)
    {
        List<object> result = value as List<object> ?? throw new FormatException(
                RimRunnerLog.Prefix + " JSON 字段 " + fieldName + " 须是数组");
        return result;
    }

    private static string? GetString(Dictionary<string, object> fields, string fieldName)
    {
        if (!fields.TryGetValue(fieldName, out object? value))
        {
            return null;
        }

        if (value == null)
        {
            return null;
        }

        string result = value as string ?? throw new FormatException(
                RimRunnerLog.Prefix + " JSON 字段 " + fieldName + " 须是字符串或 null");
        return result;
    }

    private static string GetRequiredString(
        Dictionary<string, object> fields, string fieldName, string owner)
    {
        string? value = GetString(fields, fieldName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException(RimRunnerLog.Prefix + " JSON 字段 " + owner + "."
                                      + fieldName + " 须是非空字符串");
        }

        return value;
    }

    private static float GetFloat(
        Dictionary<string, object> fields, string fieldName, float defaultValue)
    {
        if (!fields.TryGetValue(fieldName, out object? value))
        {
            return defaultValue;
        }

        if (value is not float)
        {
            throw new FormatException(
                RimRunnerLog.Prefix + " JSON 字段 " + fieldName + " 须是数字");
        }

        return (float)value;
    }

    private static float GetRequiredFloat(
        Dictionary<string, object> fields, string fieldName, string owner)
    {
        if (!fields.ContainsKey(fieldName))
        {
            throw new FormatException(RimRunnerLog.Prefix + " JSON 字段 " + owner + "."
                                      + fieldName + " 为必填数字");
        }

        return GetFloat(fields, fieldName, 0f);
    }

    private static int GetInt(
        Dictionary<string, object> fields, string fieldName, int defaultValue)
    {
        float value = GetFloat(fields, fieldName, defaultValue);
        if (value < int.MinValue || value > int.MaxValue || value != (int)value)
        {
            throw new FormatException(
                RimRunnerLog.Prefix + " JSON 字段 " + fieldName + " 须是 int 范围内的整数");
        }

        return (int)value;
    }
}
