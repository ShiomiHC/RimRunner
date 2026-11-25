namespace RimRunner;

/// <summary>认领本用例的一条引擎警告：指定警告键并写理由。</summary>
/// <remarks>可多条、随基类继承；认领的进报告 warningsAcked；理由为空的不生效并出 EMPTY-REASON。行首标归属的键须写 Origin、只认领那一归属，不写的不生效并出 INERT-DECLARATION。</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RimRunnerAcknowledgeAttribute(string key, string reason) : Attribute
{
    /// <summary>被认领的警告键，取 RimRunnerWarningKey 的常量。</summary>
    public string Key { get; } = key;

    /// <summary>认领理由，引擎不解析。</summary>
    public string Reason { get; } = reason;

    /// <summary>只认领这一归属的行：取警告行首方括号里的串，比较忽略大小写。</summary>
    /// <remarks>取值为用例、产品、vanilla、mod:包名 或 程序集:名；同键不同归属各写一条。程序集:名 是无法归入运行中 mod 时的形状，mod 改打包即变。</remarks>
    public string? Origin { get; set; }

    /// <summary>允许连续多少轮执行到尾而未命中，缺省 0：执行到尾而没命中的那一轮即出 ACK-NO-HIT。</summary>
    /// <remarks>按 mod 名单分别计数，命中一次清零；没执行到尾的轮与 ctx.MarkAckUnreachable 声明够不到的轮不计。登记期判的键写它不起作用；负数按 0 判。</remarks>
    public int Tolerance { get; set; }
}

internal readonly record struct RimRunnerAck(string? Origin, string Reason, int Tolerance = 0);

internal sealed record RimRunnerChainAck(string Key, string Origin, string Anchor, string Reason)
{
    internal bool Matches(string key, string? origin, string? anchor) =>
        Key == key && origin != null && RimRunnerOrigin.Comparer.Equals(Origin, origin)
        && string.Equals(Anchor, anchor, StringComparison.Ordinal);
}

internal sealed class RimRunnerFinding
{
    internal readonly string? Subject;
    internal readonly string? Key;
    internal readonly string Text;
    internal readonly bool NoAcknowledge;

    internal RimRunnerFinding(string? subject, string? key, string text, bool noAcknowledge = false)
    {
        Subject = subject;
        Key = key;
        Text = text;
        NoAcknowledge = noAcknowledge;
    }

    internal static RimRunnerFinding Fact(string? subject, string text) => new(subject, null, text);

    internal static RimRunnerFinding Warning(string? subject, string key, string text) => new(subject, key, text);

    internal static RimRunnerFinding UnackableWarning(string? subject, string key, string text) =>
        new(subject, key, text, noAcknowledge: true);
}

internal static class RimRunnerWarnings
{
    internal static readonly HashSet<string> AllKeys = new(
        typeof(RimRunnerWarningKey).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!),
        StringComparer.Ordinal);

    internal static readonly HashSet<string> DeclarationStaticKeys = new(StringComparer.Ordinal)
    {
        RimRunnerWarningKey.JudgingLogExemption,
        RimRunnerWarningKey.InertDeclaration,
        RimRunnerWarningKey.ClaimShape,
    };

    internal static readonly HashSet<string> IlStaticKeys = new(StringComparer.Ordinal)
    {
        RimRunnerWarningKey.LevelUnder, RimRunnerWarningKey.LevelOver,
        RimRunnerWarningKey.ForceWaitWindow,
        RimRunnerWarningKey.ModsConfigActive,
        RimRunnerWarningKey.TimingUndeclared, RimRunnerWarningKey.ScreenshotTexturesUndeclared
    };

    internal static readonly HashSet<string> StaticKeys = new(
        DeclarationStaticKeys.Concat(IlStaticKeys), StringComparer.Ordinal);

    internal static readonly HashSet<string> AbsenceGateKeys = new(StringComparer.Ordinal)
    {
        RimRunnerWarningKey.AbsentableLoadTime, RimRunnerWarningKey.AbsentableUnlabeled, RimRunnerWarningKey.PresenceInIsolated
    };

    internal static readonly HashSet<string> UnackableKeys = new(StringComparer.Ordinal)
    {
        RimRunnerWarningKey.AssertConstantTrue, RimRunnerWarningKey.AckNoHit, RimRunnerWarningKey.AckReachHit
    };

    internal static bool CountsMisses(string key) =>
        !StaticKeys.Contains(key) && !AbsenceGateKeys.Contains(key) && !UnackableKeys.Contains(key);

    internal static readonly HashSet<string> OriginKeys = new(StringComparer.Ordinal)
    {
        RimRunnerWarningKey.CurrentMapRead, RimRunnerWarningKey.FrozenMapSpawn,
        RimRunnerWarningKey.StorytellerBlocked, RimRunnerWarningKey.UnityStub
    };

    internal static void Warn(string key, string text)
    {
        if (RimRunnerSceneHost.WarnTarget() is { } scene)
        {
            RimRunnerSceneHost.WarnScene(scene, key, text, nameof(RimRunnerWarnings));
            return;
        }

        RimRunnerContext? active = RimRunnerContext.Active;
        Action record = RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Case => () => RimRunnerSession.AddRuntimeWarning(active!.Current!, key, text),
            RimRunnerPhase.Enumeration => () => active!.EnumerationWarnings.Add(new KeyValuePair<string, string>(key, text)),
            RimRunnerPhase.Scene or RimRunnerPhase.None => () => RimRunnerEngineLog.Warn(key + "：" + text)
        };
        record();
    }

    internal static Dictionary<string, List<RimRunnerAck>> Acks(Type caseType)
    {
        Dictionary<string, List<RimRunnerAck>> acks = new(StringComparer.Ordinal);
        foreach (RimRunnerAcknowledgeAttribute ack in caseType
                     .GetCustomAttributes(typeof(RimRunnerAcknowledgeAttribute), true)
                     .Cast<RimRunnerAcknowledgeAttribute>())
        {
            if (ack.Key == null || string.IsNullOrWhiteSpace(ack.Reason) || !OriginShapeOk(ack))
            {
                continue;
            }

            if (!acks.TryGetValue(ack.Key, out List<RimRunnerAck>? list))
            {
                list = [];
                acks[ack.Key] = list;
            }

            if (!list.Any(existing => SameOrigin(existing.Origin, ack.Origin)))
            {
                list.Add(new RimRunnerAck(ack.Origin, ack.Reason, Math.Max(0, ack.Tolerance)));
            }
        }

        return acks;
    }

    internal static HashSet<string> AckedOrigins(Type caseType, string key)
    {
        HashSet<string> origins = new(RimRunnerOrigin.Comparer);
        if (Acks(caseType).TryGetValue(key, out List<RimRunnerAck>? list))
        {
            foreach (RimRunnerAck ack in list)
            {
                if (ack.Origin != null)
                {
                    origins.Add(ack.Origin);
                }
            }
        }

        return origins;
    }

    internal static string? Match(IReadOnlyDictionary<string, List<RimRunnerAck>>? acks, string key, string? origin)
    {
        if (acks == null || !acks.TryGetValue(key, out List<RimRunnerAck>? list))
        {
            return null;
        }

        foreach (RimRunnerAck ack in list)
        {
            if (ack.Origin == null || (origin != null && RimRunnerOrigin.Comparer.Equals(ack.Origin, origin)))
            {
                return ack.Reason;
            }
        }

        return null;
    }

    private static bool OriginShapeOk(RimRunnerAcknowledgeAttribute ack) =>
        OriginKeys.Contains(ack.Key)
            ? ack.Origin != null && RimRunnerOrigin.IsWellFormed(ack.Origin)
            : ack.Origin == null;

    private static bool SameOrigin(string? a, string? b) =>
        a == null ? b == null : b != null && RimRunnerOrigin.Comparer.Equals(a, b);

    internal const string ChainAckKey = RimRunnerWarningKey.CurrentMapRead;

    private const string OriginMark = " @ ";
    private const string AnchorMark = " / ";

    internal static Dictionary<string, string> BatchAcks(IReadOnlyList<RimRunnerRunClaims.Ack> acks)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (RimRunnerRunClaims.Ack ack in acks)
        {
            if (AllKeys.Contains(ack.Target) && ack.Target != ChainAckKey && !string.IsNullOrWhiteSpace(ack.Reason)
                && !result.ContainsKey(ack.Target))
            {
                result[ack.Target] = ack.Reason;
            }
        }

        return result;
    }

    internal static List<RimRunnerChainAck> ChainAcks(IReadOnlyList<RimRunnerRunClaims.Ack> acks)
    {
        List<RimRunnerChainAck> result = [];
        foreach (RimRunnerRunClaims.Ack ack in acks)
        {
            if (SplitChain(ack.Target) is { } parts && ChainShapeProblem(parts) == null && !string.IsNullOrWhiteSpace(ack.Reason)
                && !result.Any(existing => existing.Matches(parts.Key, parts.Origin, parts.Anchor)))
            {
                result.Add(new RimRunnerChainAck(parts.Key, parts.Origin, parts.Anchor, ack.Reason));
            }
        }

        return result;
    }

    private static (string Key, string Origin, string Anchor)? SplitChain(string target)
    {
        int at = target.IndexOf(OriginMark, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        string rest = target[(at + OriginMark.Length)..];
        int slash = rest.IndexOf(AnchorMark, StringComparison.Ordinal);
        return (target[..at].Trim(),
            (slash < 0 ? rest : rest[..slash]).Trim(),
            slash < 0 ? "" : rest[(slash + AnchorMark.Length)..].Trim());
    }

    private static string? ChainShapeProblem((string Key, string Origin, string Anchor) parts)
    {
        if (!AllKeys.Contains(parts.Key))
        {
            return parts.Key + " 不是任何警告键，这条认领无法认领任何警告";
        }

        if (UnackableKeys.Contains(parts.Key))
        {
            return parts.Key + " 这个键不可认领";
        }

        if (parts.Key != ChainAckKey)
        {
            return parts.Key + " 在名单里按键认领，不接受「@ 归属 / 链锚」：只有 " + ChainAckKey + " 的环境链按链锚认领";
        }

        if (!RimRunnerOrigin.IsWellFormed(parts.Origin)
            || !parts.Origin.StartsWith(RimRunnerOrigin.ModPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "归属 \"" + parts.Origin + "\" 不是 mod:<packageId>：环境链的读点只在他 mod 里";
        }

        int dot = parts.Anchor.IndexOf('.');
        if (dot <= 0 || dot == parts.Anchor.Length - 1 || parts.Anchor.Contains(' '))
        {
            return "链锚 \"" + parts.Anchor + "\" 不是「声明类型短名.方法名」";
        }

        return null;
    }

    internal static List<RimRunnerFinding> BatchAckDeclarationFindings(IReadOnlyList<RimRunnerRunClaims.Ack> acks)
    {
        List<RimRunnerFinding> findings = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> seenChains = new(StringComparer.Ordinal);
        foreach (RimRunnerRunClaims.Ack ack in acks)
        {
            if (SplitChain(ack.Target) is { } parts)
            {
                string? problem = ChainShapeProblem(parts);
                if (problem != null)
                {
                    findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.InertDeclaration,
                        "mod 名单的 [rimrunner].acknowledge = " + ack.Target + " 不生效：" + problem));
                    continue;
                }

                string label = parts.Key + "[" + parts.Origin + " / " + parts.Anchor + "]";
                if (string.IsNullOrWhiteSpace(ack.Reason))
                {
                    findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.EmptyReason,
                        "mod 名单认领 " + label + " 的理由为空，这条认领不生效"));
                }

                if (!seenChains.Add(parts.Origin.ToLowerInvariant() + "\n" + parts.Anchor + "\n" + parts.Key))
                {
                    findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.EmptyReason,
                        "mod 名单对 " + label + " 认领不止一次，只取第一条的理由"));
                }

                continue;
            }

            if (ack.Target == ChainAckKey)
            {
                findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.InertDeclaration,
                    "mod 名单认领 " + ack.Target + " 未写「@ 归属 / 链锚」，这条认领不生效：名单对这个键只认领环境链"));
                continue;
            }

            if (!AllKeys.Contains(ack.Target))
            {
                findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.InertDeclaration,
                    "mod 名单的 [rimrunner].acknowledge 点名的 " + ack.Target + " 不是任何警告键，这条认领无法认领任何警告"));
                continue;
            }

            if (UnackableKeys.Contains(ack.Target))
            {
                findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.InertDeclaration,
                    "mod 名单认领 " + ack.Target + " 不生效：这个键不可认领"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(ack.Reason))
            {
                findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.EmptyReason,
                    "mod 名单认领 " + ack.Target + " 的理由为空，这条认领不生效"));
            }

            if (!seen.Add(ack.Target))
            {
                findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.EmptyReason,
                    "mod 名单对 " + ack.Target + " 认领不止一次，只取第一条的理由"));
            }
        }

        return findings;
    }

    internal const string OriginMissingReason = "未指定 Origin";
    internal const string OriginNotAllowedReason = "不带归属";
    internal const string OriginMalformedReason = "无法识别";

    internal static List<RimRunnerFinding> AckDeclarationFindings(string id, Type caseType)
    {
        List<RimRunnerFinding> findings = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (RimRunnerAcknowledgeAttribute ack in caseType
                     .GetCustomAttributes(typeof(RimRunnerAcknowledgeAttribute), true)
                     .Cast<RimRunnerAcknowledgeAttribute>())
        {
            if (ack.Key != null && UnackableKeys.Contains(ack.Key))
            {
                findings.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.InertDeclaration,
                    "用例 " + id + " 认领 " + ack.Key + " 不生效：这个键不可认领"));
                continue;
            }

            if (ack.Key == null || !AllKeys.Contains(ack.Key))
            {
                findings.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.InertDeclaration,
                    "用例 " + id + " 的 [RimRunnerAcknowledge(\"" + ack.Key
                    + "\")] 点名的不是任何警告键，这条认领无法认领任何警告"));
                continue;
            }

            if (OriginKeys.Contains(ack.Key) && ack.Origin == null)
            {
                findings.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.InertDeclaration,
                    "用例 " + id + " 认领 " + ack.Key + " " + OriginMissingReason + "，这条认领不生效：这个键的每行行首标着归属"));
                continue;
            }

            if (!OriginKeys.Contains(ack.Key) && ack.Origin != null)
            {
                findings.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.InertDeclaration,
                    "用例 " + id + " 认领 " + ack.Key + " 写 Origin = \"" + ack.Origin + "\"，这条认领不生效：这个键的警告"
                    + OriginNotAllowedReason));
                continue;
            }

            if (ack.Origin != null && !RimRunnerOrigin.IsWellFormed(ack.Origin))
            {
                findings.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.InertDeclaration,
                    "用例 " + id + " 认领 " + ack.Key + " 的 Origin = \"" + ack.Origin + "\" " + OriginMalformedReason
                    + "，这条认领不生效"));
                continue;
            }

            string label = ack.Origin == null ? ack.Key : ack.Key + "[" + ack.Origin + "]";
            if (string.IsNullOrWhiteSpace(ack.Reason))
            {
                findings.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.EmptyReason,
                    "用例 " + id + " 认领 " + label + " 的理由为空，这条认领不生效"));
            }

            if (!seen.Add(ack.Key + "\n" + ack.Origin))
            {
                findings.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.EmptyReason,
                    "用例 " + id + " 对 " + label + " 认领不止一次，只取第一条的理由"));
            }

            if (ack.Tolerance < 0)
            {
                findings.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.InertDeclaration,
                    "用例 " + id + " 认领 " + label + " 的 Tolerance = " + ack.Tolerance + " 为负，按 0 判"));
            }
            else if (ack.Tolerance > 0 && !CountsMisses(ack.Key))
            {
                findings.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.InertDeclaration,
                    "用例 " + id + " 认领 " + label + " 写 Tolerance 不起作用：这个键不计连续未命中"
                    + (StaticKeys.Contains(ack.Key) ? "，认领而本轮没出即报 " + RimRunnerWarningKey.InertDeclaration : "")));
            }
        }

        return findings;
    }

    internal static RimRunnerFindingResolution Resolve(IEnumerable<RimRunnerFinding> raw,
        IReadOnlyDictionary<string, string> batchAcks, bool ilScanIncomplete = false)
    {
        Dictionary<string, Type> types = new(StringComparer.Ordinal);
        foreach (RimRunnerTestCase testCase in RimRunnerRegistry.All)
        {
            if (!types.ContainsKey(testCase.Id))
            {
                types[testCase.Id] = testCase.GetType();
            }
        }

        Dictionary<Type, Dictionary<string, List<RimRunnerAck>>> acksByType = [];
        Dictionary<string, Dictionary<string, List<RimRunnerAck>>> acksById =
            new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Type> pair in types)
        {
            if (!acksByType.TryGetValue(pair.Value, out Dictionary<string, List<RimRunnerAck>>? acks))
            {
                acks = Acks(pair.Value);
                acksByType[pair.Value] = acks;
            }

            acksById[pair.Key] = acks;
        }

        return Resolve(raw, acksById, batchAcks, ilScanIncomplete);
    }

    internal static RimRunnerFindingResolution Resolve(IEnumerable<RimRunnerFinding> raw,
        IReadOnlyDictionary<string, Dictionary<string, List<RimRunnerAck>>> acksById,
        IReadOnlyDictionary<string, string>? batchAcks = null, bool ilScanIncomplete = false)
    {
        RimRunnerFindingResolution resolution = new();

        void Classify(string? subject, string key, string text)
        {
            string? reason = null;
            if (subject != null)
            {
                acksById.TryGetValue(subject, out Dictionary<string, List<RimRunnerAck>>? caseAcks);
                reason = Match(caseAcks, key, null);
            }
            else if (batchAcks != null)
            {
                batchAcks.TryGetValue(key, out reason);
            }

            if (reason != null)
            {
                resolution.Acked.Add(new RimRunnerWarningEntry { subject = subject, key = key, text = text, reason = reason });
            }
            else
            {
                resolution.Warnings.Add(new RimRunnerWarningEntry { subject = subject, key = key, text = text });
            }
        }

        HashSet<string> emitted = new(StringComparer.Ordinal);
        foreach (RimRunnerFinding finding in raw)
        {
            if (finding.Key == null)
            {
                if (finding.Subject == null)
                {
                    resolution.BatchFacts.Add(finding.Text);
                }
                else
                {
                    resolution.AddCaseFact(finding.Subject, finding.Text);
                }

                continue;
            }

            emitted.Add(finding.Subject + "\n" + finding.Key);
            if (finding.NoAcknowledge)
            {
                resolution.Warnings.Add(new RimRunnerWarningEntry
                {
                    subject = finding.Subject, key = finding.Key, text = finding.Text
                });
                continue;
            }

            Classify(finding.Subject, finding.Key, finding.Text);
        }

        foreach (KeyValuePair<string, Dictionary<string, List<RimRunnerAck>>> pair in acksById)
        {
            foreach (KeyValuePair<string, List<RimRunnerAck>> ack in pair.Value)
            {
                bool checkStale = DeclarationStaticKeys.Contains(ack.Key)
                    || (!ilScanIncomplete && IlStaticKeys.Contains(ack.Key));
                if (checkStale && !emitted.Contains(pair.Key + "\n" + ack.Key))
                {
                    Classify(pair.Key, RimRunnerWarningKey.InertDeclaration,
                        "用例 " + pair.Key + " 认领 " + ack.Key + "，本轮未出这条警告：这个键的检查只看声明与 IL，"
                        + "同一份 DLL 下不会出，这条认领不起作用。认领理由：" + ack.Value[0].Reason);
                }
            }
        }

        return resolution;
    }
}

internal sealed class RimRunnerFindingResolution
{
    internal readonly List<string> BatchFacts = [];

    internal readonly Dictionary<string, List<string>> CaseFacts = new(StringComparer.Ordinal);
    internal readonly List<string> CaseFactOrder = [];

    internal readonly List<RimRunnerWarningEntry> Warnings = [];
    internal readonly List<RimRunnerWarningEntry> Acked = [];

    internal void AddCaseFact(string subject, string text)
    {
        if (!CaseFacts.TryGetValue(subject, out List<string>? texts))
        {
            texts = [];
            CaseFacts[subject] = texts;
            CaseFactOrder.Add(subject);
        }

        texts.Add(text);
    }
}
