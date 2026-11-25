using System.Globalization;

namespace RimRunner;

internal static class RimRunnerAckMisses
{
    private const string Separator = " :: ";
    private const string KeyCategory = "key";
    private const string LogCategory = "log";
    private const int FlushThreshold = 256;

    private static RimRunnerRingLedger? ledger;

    internal static string KeyIdentity(string key, string? origin)
    {
        return KeyCategory + Separator + key + (origin == null ? "" : " @ " + origin.ToLowerInvariant());
    }

    internal static string LogIdentity(RimRunnerLogAckEntry cell)
    {
        return LogCategory + Separator + RimRunnerLogAcks.IdentityOf(cell);
    }

    private static string LogIdentity(string logAckIdentity)
    {
        return LogCategory + Separator + logAckIdentity;
    }

    internal static void MarkKeyUnreachable(RimRunnerResultEntry entry, string key, string? origin, string reason)
    {
        string label = origin == null ? key : key + "[" + origin + "]";
        if (string.IsNullOrWhiteSpace(reason))
        {
            RimRunnerSession.AddRuntimeWarning(entry, RimRunnerWarningKey.EmptyReason,
                "用例 " + entry.id + " 声明认领 " + label + " 本轮够不到，理由为空，这条声明不生效");
            return;
        }

        Type? caseType = CaseTypeOf(entry.id);
        RimRunnerAck? ack = null;
        if (caseType != null && key != null && RimRunnerWarnings.Acks(caseType).TryGetValue(key, out List<RimRunnerAck>? list))
        {
            ack = list.FirstOrDefault(item => item.Origin == null
                ? origin == null
                : origin != null && RimRunnerOrigin.Comparer.Equals(item.Origin, origin));
        }

        if (ack is not { } found)
        {
            RimRunnerSession.AddRuntimeWarning(entry, RimRunnerWarningKey.InertDeclaration,
                "用例 " + entry.id + " 声明认领 " + label + " 本轮够不到，本用例没有这条生效的认领，这条声明不起作用");
            return;
        }

        if (!RimRunnerWarnings.CountsMisses(key!))
        {
            RimRunnerSession.AddRuntimeWarning(entry, RimRunnerWarningKey.InertDeclaration,
                "用例 " + entry.id + " 声明认领 " + label + " 本轮够不到，这个键不计连续未命中，这条声明不起作用");
            return;
        }

        entry.unreachableAcks[KeyIdentity(key!, found.Origin)] = reason;
    }

    internal static void MarkLogUnreachable(RimRunnerResultEntry entry, RimRunnerLogLevel level, string substring, string reason)
    {
        string label = (level == RimRunnerLogLevel.Error ? "错误级日志许可" : "警告级日志许可") + " \"" + substring + "\"";
        if (string.IsNullOrWhiteSpace(reason))
        {
            RimRunnerSession.AddRuntimeWarning(entry, RimRunnerWarningKey.EmptyReason,
                "用例 " + entry.id + " 声明" + label + "本轮够不到，理由为空，这条声明不生效");
            return;
        }

        List<string> identities = RimRunnerLogAcks.IdentitiesOf(RimRunnerSession.LogAcksOf(entry.id), level, substring ?? "");
        if (identities.Count == 0)
        {
            RimRunnerSession.AddRuntimeWarning(entry, RimRunnerWarningKey.InertDeclaration,
                "用例 " + entry.id + " 声明" + label + "本轮够不到，本用例没有这条生效的许可，这条声明不起作用");
            return;
        }

        foreach (string identity in identities)
        {
            entry.unreachableAcks[LogIdentity(identity)] = reason;
        }
    }

    internal static void Annotate(RimRunnerReport report, IEnumerable<string> participants)
    {
        if (RimRunnerSaveProbe.Armed)
        {
            return;
        }

        RimRunnerRingLedger book = EnsureLoaded();
        Annotate(book, [.. report.regressions.Concat(report.readouts)], report.acks,
            new HashSet<string>(participants, StringComparer.Ordinal), report.shard != null, RimRunnerOrigin.IsLoaded);
        book.Flush();
    }

    internal static void Annotate(RimRunnerRingLedger book, IReadOnlyList<RimRunnerResultEntry> entries,
        IReadOnlyList<RimRunnerAckEntry> keyCells, IReadOnlyCollection<string> participants, bool shard,
        Func<string, bool>? originLoaded = null)
    {
        foreach (IGrouping<string, RimRunnerResultEntry> group in entries
                     .Where(entry => participants.Contains(entry.id)).GroupBy(entry => entry.id, StringComparer.Ordinal))
        {
            RimRunnerResultEntry first = group.First();
            Dictionary<string, string> unreachable = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> mark in group.SelectMany(entry => entry.unreachableAcks))
            {
                unreachable[mark.Key] = mark.Value;
            }

            foreach (RimRunnerAckEntry cell in keyCells.Where(cell =>
                         cell.subject == group.Key && cell.kind == RimRunnerAckEntry.KeyKind && RimRunnerWarnings.CountsMisses(cell.target)))
            {
                string identity = KeyIdentity(cell.target, cell.origin);
                unreachable.TryGetValue(identity, out cell.unreachable);
                if (cell.unreachable == null && cell.hits == 0 && cell.origin != null && originLoaded != null
                    && !originLoaded(cell.origin))
                {
                    cell.unreachable = cell.origin + " 本轮未装载";
                }

                cell.misses = Count(book, group.Key + Separator + identity, cell.hits, cell.unreachable);
                Judge(first, RimRunnerAckMissText.KeyLabel(cell.target, cell.origin), cell.hits, cell.misses, cell.tolerance,
                    cell.unreachable, cell.reason, shard);
            }

            foreach (IGrouping<string, RimRunnerLogAckEntry> same in group.SelectMany(entry => entry.logAcks)
                         .GroupBy(LogIdentity, StringComparer.Ordinal))
            {
                RimRunnerLogAckEntry cell = same.First();
                int hits = same.Sum(item => item.hits);
                unreachable.TryGetValue(same.Key, out string? reason);
                int? misses = Count(book, group.Key + Separator + same.Key, hits, reason);
                foreach (RimRunnerLogAckEntry item in same)
                {
                    item.misses = misses;
                    item.unreachable = reason;
                }

                Judge(first, RimRunnerAckMissText.LogLabel(cell.level, cell.pattern, cell.stackAnchors), hits, misses,
                    cell.tolerance, reason, cell.reason, shard);
            }
        }
    }

    private static int? Count(RimRunnerRingLedger book, string key, int hits, string? unreachable)
    {
        if (hits == 0 && unreachable != null)
        {
            return null;
        }

        IReadOnlyList<string> ring = book.Ring(key);
        int prior = ring.Count == 0 ? 0 : RimRunnerLedgerFold.MissesOf(ring[ring.Count - 1]);
        if (prior == int.MaxValue)
        {
            prior = 0;
        }

        int misses = hits > 0 ? 0 : prior + 1;
        book.Append(key, misses.ToString(CultureInfo.InvariantCulture));
        return misses;
    }

    private static void Judge(RimRunnerResultEntry entry, string label, int hits, int? misses, int tolerance,
        string? unreachable, string reason, bool shard)
    {
        if (unreachable != null && hits > 0)
        {
            entry.warnings.Add(new RimRunnerWarningEntry
            {
                subject = entry.id, key = RimRunnerWarningKey.AckReachHit,
                text = RimRunnerAckMissText.ReachHit(entry.id, label, hits, unreachable)
            });
        }

        if (!shard && misses > tolerance)
        {
            entry.warnings.Add(new RimRunnerWarningEntry
            {
                subject = entry.id, key = RimRunnerWarningKey.AckNoHit,
                text = RimRunnerAckMissText.NoHit(entry.id, label, misses.Value, tolerance, reason)
            });
        }
    }

    private static Type? CaseTypeOf(string caseId)
    {
        return RimRunnerRegistry.All.FirstOrDefault(item => string.Equals(item.Id, caseId, StringComparison.Ordinal))?.GetType();
    }

    private static RimRunnerRingLedger EnsureLoaded()
    {
        if (ledger != null)
        {
            return ledger;
        }

        HashSet<string> registered = new(RimRunnerRegistry.All.Select(testCase => testCase.Id), StringComparer.Ordinal);
        return ledger = RimRunnerRingLedger.ForHistory(RimRunnerLedgerFold.AckMissesStem, RimRunnerPassRate.ResolveLedgerKey(),
            "认领连续未命中账", RimRunnerLedgerFold.AckMissesCap, FlushThreshold, key => IsEligible(registered, key));
    }

    internal static bool IsEligible(HashSet<string> registered, string key)
    {
        int end = key.IndexOf(Separator, StringComparison.Ordinal);
        return end > 0 && registered.Contains(key[..end]);
    }
}
