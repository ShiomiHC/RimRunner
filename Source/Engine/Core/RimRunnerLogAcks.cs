using System.Text.RegularExpressions;

namespace RimRunner;

internal static class RimRunnerLogAcks
{
    internal static List<RimRunnerAcknowledgeLogAttribute> Declared(Type caseType)
    {
        return caseType.GetCustomAttributes(typeof(RimRunnerAcknowledgeLogAttribute), true)
            .Cast<RimRunnerAcknowledgeLogAttribute>()
            .ToList();
    }

    internal static List<RimRunnerFinding> DeclarationFindings(string caseId, RimRunnerKind kind,
        IReadOnlyList<RimRunnerAcknowledgeLogAttribute>? acks)
    {
        List<RimRunnerFinding> findings = [];
        if (acks == null || acks.Count == 0)
        {
            return findings;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (RimRunnerAcknowledgeLogAttribute ack in acks)
        {
            string label = Label(ack);
            if (string.IsNullOrEmpty(ack.Substring))
            {
                findings.Add(RimRunnerFinding.Warning(caseId, RimRunnerWarningKey.ClaimShape,
                    "用例 " + caseId + " 的 " + label + " 子串为空，一条" + What(ack.Level) + "都无法认领"));
                continue;
            }

            if (!seen.Add(IdentityOf(ack)))
            {
                findings.Add(RimRunnerFinding.Warning(caseId, RimRunnerWarningKey.ClaimShape,
                    "用例 " + caseId + " 的 " + label + " 写不止一条，命中只记在第一条上"));
            }

            if (string.IsNullOrWhiteSpace(ack.Reason))
            {
                findings.Add(RimRunnerFinding.Warning(caseId, RimRunnerWarningKey.EmptyReason,
                    "用例 " + caseId + " 的 " + label + " 理由为空，这条许可不生效"));
            }

            if (ack.Level == RimRunnerLogLevel.Error && AnchorsOf(ack).Length == 0 && TypeNameOnly.IsMatch(ack.Substring))
            {
                findings.Add(RimRunnerFinding.Warning(caseId, RimRunnerWarningKey.AckBroad,
                    "用例 " + caseId + " 的 " + label + " 子串只是异常类型名，认领该类型从任何抛出点出的" + What(ack.Level)));
            }

            if (ack.Tolerance < 0)
            {
                findings.Add(RimRunnerFinding.Warning(caseId, RimRunnerWarningKey.InertDeclaration,
                    "用例 " + caseId + " 的 " + label + " Tolerance = " + ack.Tolerance + " 为负，按 0 判"));
            }

            if (ack.StackAnchors != null)
            {
                if (ack.Level == RimRunnerLogLevel.Warning)
                {
                    findings.Add(RimRunnerFinding.Warning(caseId, RimRunnerWarningKey.InertDeclaration,
                        "用例 " + caseId + " 的 " + label + " 写 StackAnchors：警告级日志没有栈列，锚不参与认领"));
                }
                else if (ack.StackAnchors.Length == 0 || ack.StackAnchors.Any(string.IsNullOrEmpty))
                {
                    findings.Add(RimRunnerFinding.Warning(caseId, RimRunnerWarningKey.InertDeclaration,
                        "用例 " + caseId + " 的 " + label + " StackAnchors 为空或含空串：空锚对一切栈都成立"));
                }
            }
        }

        if (kind.IsRegression())
        {
            string[] errors = acks.Where(ack => ack.Level == RimRunnerLogLevel.Error && !string.IsNullOrEmpty(ack.Substring))
                .Select(ack => "\"" + ack.Substring + "\"")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (errors.Length > 0)
            {
                findings.Add(RimRunnerFinding.Warning(caseId, RimRunnerWarningKey.JudgingLogExemption,
                    "用例 " + caseId + "（Kind=" + kind + "）写错误级日志许可 " + string.Join("、", errors)
                    + "，命中的错误级日志不进本条裁决"));
            }
        }

        return findings;
    }

    private static readonly Regex TypeNameOnly = new(@"^[\w.]*Exception$", RegexOptions.CultureInvariant);

    internal static List<RimRunnerAcknowledgeLogAttribute> Effective(IReadOnlyList<RimRunnerAcknowledgeLogAttribute>? acks)
    {
        if (acks == null)
        {
            return [];
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        return acks.Where(ack => !string.IsNullOrEmpty(ack.Substring) && !string.IsNullOrWhiteSpace(ack.Reason)
                                                                       && seen.Add(IdentityOf(ack)))
            .Select((ack, index) => new { ack, index })
            .OrderByDescending(item => AnchorsOf(item.ack).Length)
            .ThenByDescending(item => item.ack.Substring.Length)
            .ThenBy(item => item.index)
            .Select(item => item.ack)
            .ToList();
    }

    internal static bool TryClaim(RimRunnerResultEntry? entry, IReadOnlyList<RimRunnerAcknowledgeLogAttribute>? effective,
        RimRunnerLogLevel level, string? line, string? stack = null)
    {
        if (entry == null || effective == null || line == null)
        {
            return false;
        }

        RimRunnerAcknowledgeLogAttribute? matched = effective.FirstOrDefault(ack =>
            ack.Level == level && line.IndexOf(ack.Substring, StringComparison.Ordinal) >= 0
                               && StackMatches(ack, stack));
        if (matched == null)
        {
            return false;
        }

        RimRunnerLogAckEntry? cell = CellOf(entry.logAcks, matched);
        if (cell == null)
        {
            cell = NewCell(matched);
            entry.logAcks.Add(cell);
        }

        cell.hits++;
        if (!cell.lines.Contains(line))
        {
            cell.lines.Add(line);
        }

        return true;
    }

    private static bool StackMatches(RimRunnerAcknowledgeLogAttribute ack, string? stack)
    {
        string[] anchors = AnchorsOf(ack);
        if (anchors.Length == 0)
        {
            return true;
        }

        return stack != null && anchors.All(anchor => stack.IndexOf(anchor, StringComparison.Ordinal) >= 0);
    }

    internal static void Settle(RimRunnerResultEntry entry, IReadOnlyList<RimRunnerAcknowledgeLogAttribute>? acks)
    {
        HashSet<RimRunnerAcknowledgeLogAttribute> effective =
            [.. Effective(acks)];
        List<RimRunnerLogAckEntry> settled = [];
        foreach (RimRunnerAcknowledgeLogAttribute ack in (acks ?? [])
                 .Where(effective.Contains))
        {
            settled.Add(CellOf(entry.logAcks, ack) ?? NewCell(ack));
        }

        entry.logAcks = settled;
    }

    internal static List<string> BroadClaims(RimRunnerResultEntry entry)
    {
        return BroadClaims(entry, RimRunnerSeenLogLedger.GeneratedPawnNames);
    }

    internal static List<string> BroadClaims(RimRunnerResultEntry entry, IEnumerable<string> pawnNames)
    {
        List<string> texts = [];
        foreach (RimRunnerLogAckEntry cell in entry.logAcks)
        {
            List<string> kinds = cell.lines.Select(line => RimRunnerSeenLogLedger.ShapeOf(line, pawnNames))
                .Distinct(StringComparer.Ordinal).ToList();
            if (kinds.Count <= 1) continue;
            texts.Add("用例 " + entry.id + " 的日志许可「" + cell.pattern + "」本轮认领到 " + kinds.Count + " 种正文（数字与本轮 pawn 名归一后）："
                      + string.Join(" / ", kinds.Select(Clip)));
        }

        return texts;
    }

    internal static string FreshClaim(string caseId, RimRunnerLogAckEntry cell)
    {
        return "用例 " + caseId + " 的日志许可「" + cell.pattern + "」本轮认领到 " + cell.linesFresh.Count
               + " 种本名单下此前各轮未认领过的正文（数字与本轮 pawn 名归一后）："
               + string.Join(" / ", cell.linesFresh.Select(Clip));
    }

    private const int MaxListedChars = 120;

    private static string Clip(string text)
        => text.Length <= MaxListedChars ? text : text[..MaxListedChars] + "…";

    private static RimRunnerLogAckEntry NewCell(RimRunnerAcknowledgeLogAttribute ack)
    {
        return new RimRunnerLogAckEntry
        {
            level = LevelOf(ack),
            pattern = ack.Substring,
            reason = ack.Reason.Trim(),
            stackAnchors = AnchorsOf(ack).ToList(),
            tolerance = Math.Max(0, ack.Tolerance)
        };
    }

    private static RimRunnerLogAckEntry? CellOf(List<RimRunnerLogAckEntry> cells, RimRunnerAcknowledgeLogAttribute ack)
    {
        string level = LevelOf(ack);
        string[] anchors = AnchorsOf(ack);
        return cells.FirstOrDefault(cell => cell.level == level
                                            && string.Equals(cell.pattern, ack.Substring, StringComparison.Ordinal)
                                            && cell.stackAnchors.SequenceEqual(anchors, StringComparer.Ordinal));
    }

    private static string[] AnchorsOf(RimRunnerAcknowledgeLogAttribute ack)
    {
        return ack.Level == RimRunnerLogLevel.Error && ack.StackAnchors != null
            ? ack.StackAnchors.Where(anchor => !string.IsNullOrEmpty(anchor)).ToArray()
            : [];
    }

    private static string IdentityOf(RimRunnerAcknowledgeLogAttribute ack)
    {
        return Identity(LevelOf(ack), ack.Substring, AnchorsOf(ack));
    }

    internal static List<string> IdentitiesOf(IReadOnlyList<RimRunnerAcknowledgeLogAttribute>? acks,
        RimRunnerLogLevel level, string substring)
    {
        return [.. Effective(acks).Where(ack => ack.Level == level && string.Equals(ack.Substring, substring, StringComparison.Ordinal))
            .Select(IdentityOf)];
    }

    internal static string IdentityOf(RimRunnerLogAckEntry cell)
    {
        return Identity(cell.level, cell.pattern, cell.stackAnchors);
    }

    private static string Identity(string level, string substring, IEnumerable<string> anchors)
    {
        return level + "\n" + substring + "\n" + string.Join("\n", anchors);
    }

    private static string LevelOf(RimRunnerAcknowledgeLogAttribute ack)
    {
        return ack.Level == RimRunnerLogLevel.Error ? RimRunnerLogAckEntry.ErrorLevel : RimRunnerLogAckEntry.WarningLevel;
    }

    private static string Label(RimRunnerAcknowledgeLogAttribute ack)
    {
        return "[RimRunnerAcknowledgeLog(" + ack.Level + ", \"" + ack.Substring + "\")]";
    }

    private static string What(RimRunnerLogLevel level)
    {
        return level == RimRunnerLogLevel.Error ? "错误级日志" : "警告级日志";
    }
}
