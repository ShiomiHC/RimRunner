using System.Text;

namespace RimRunner;

public static class RimRunnerSeenLogLedger
{
    private const int Cap = RimRunnerLedgerFold.SeenLogsCap;
    private const string SeenCell = "1";
    private const int FlushThreshold = 64;

    private const string Separator = " :: ";
    private const string ExpectedLogCategory = "log";
    private const string DegradedNoteCategory = "skip";
    private const string AmbientHead = "<ambient>";
    private const string AmbientCategory = "window";
    private const string AckLineCategory = "ack";
    private const string AckStartCategory = "ack-start";

    internal const string PawnPlaceholder = "{pawn}";

    internal const char DigitMarker = '#';

    private static readonly HashSet<string> generatedPawnNames = new(StringComparer.Ordinal);

    internal static IEnumerable<string> GeneratedPawnNames => generatedPawnNames;

    private static RimRunnerRingLedger? ledger;

    private static RimRunnerRingLedger? warningLedger;
    private const string WarningBatchHead = "<batch>";
    internal const string RegistrationScope = "<registration>";

    internal static string LoadScope(string source)
    {
        return "<load:" + source + ">";
    }

    internal static string CliScope(string key)
    {
        return "<cli:" + key + ">";
    }

    internal static string EnvChainScope(string signature)
    {
        return "<envchain:" + signature + ">";
    }
    private const string PresentCell = "1";
    private const string GoneCell = "0";

    public static string? FileName()
    {
        return EnsureLoaded().FileName;
    }

    internal static void Annotate(RimRunnerResultEntry entry)
    {
        EnsureLoaded();
        entry.expectedLogsFresh = Fresh(entry.id, ExpectedLogCategory, entry.expectedLogs);
        entry.degradedNotesFresh = Fresh(entry.id, DegradedNoteCategory, entry.degradedNotes);
    }

    internal static List<RimRunnerLogAcks.AckFreshClaim> AnnotateAcks(RimRunnerResultEntry entry)
    {
        return AnnotateAcks(EnsureLoaded(), entry, generatedPawnNames, !RimRunnerSaveProbe.Armed);
    }

    internal static List<RimRunnerLogAcks.AckFreshClaim> AnnotateAcks(RimRunnerRingLedger book, RimRunnerResultEntry entry,
        IEnumerable<string> pawnNames, bool remember)
    {
        List<RimRunnerLogAcks.AckFreshClaim> claims = [];
        foreach (RimRunnerLogAckEntry cell in entry.logAcks)
        {
            string identity = RimRunnerLogAcks.IdentityOf(cell);
            string startKey = entry.id + Separator + AckStartCategory + Separator + identity;
            bool started = book.Ring(startKey).Count > 0;
            HashSet<string> keys = new(StringComparer.Ordinal);
            List<string> fresh = [];
            foreach (string line in cell.lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                string key = entry.id + Separator + AckLineCategory + Separator + identity + Separator
                             + ShapeOf(line, pawnNames);
                if (!keys.Add(key) || book.Ring(key).Count > 0)
                {
                    continue;
                }

                fresh.Add(line);
                if (remember)
                {
                    book.Append(key, SeenCell);
                }
            }

            cell.linesFresh = fresh;
            if (fresh.Count == 0)
            {
                continue;
            }

            if (started)
            {
                claims.Add(RimRunnerLogAcks.FreshClaim(cell));
            }
            else if (remember)
            {
                book.Append(startKey, SeenCell);
            }
        }

        return claims;
    }

    internal static void AnnotateWarnings(RimRunnerReport report, IEnumerable<string> ranToEnd)
    {
        RimRunnerRingLedger book = EnsureWarningsLoaded();
        AnnotateWarnings(book,
            report.warnings.Concat(report.regressions.Concat(report.readouts)
                .SelectMany(entry => entry.warnings)),
            [.. ranToEnd.Concat(report.warningScopes)], !RimRunnerSaveProbe.Armed);
        book.Flush();
    }

    internal static void AnnotateWarnings(RimRunnerRingLedger book, IEnumerable<RimRunnerWarningEntry> warnings,
        IReadOnlyCollection<string> participants, bool remember)
    {
        List<KeyValuePair<RimRunnerWarningEntry, string>> keyed = [.. warnings.Select(warning =>
            new KeyValuePair<RimRunnerWarningEntry, string>(warning,
                (warning.subject ?? warning.scope ?? WarningBatchHead) + Separator + warning.key))];
        foreach (KeyValuePair<RimRunnerWarningEntry, string> pair in keyed)
        {
            pair.Key.fresh = !Present(book, pair.Value);
        }

        if (!remember)
        {
            return;
        }

        HashSet<string> present = new(keyed.Select(pair => pair.Value), StringComparer.Ordinal);
        foreach (string key in present.Where(key => !Present(book, key)))
        {
            book.Append(key, PresentCell);
        }

        HashSet<string> heads = new(participants, StringComparer.Ordinal);
        foreach (string key in book.Keys.ToList())
        {
            int end = key.IndexOf(Separator, StringComparison.Ordinal);
            if (end > 0 && heads.Contains(key[..end]) && !present.Contains(key) && Present(book, key))
            {
                book.Append(key, GoneCell);
            }
        }
    }

    private static bool Present(RimRunnerRingLedger book, string key)
    {
        IReadOnlyList<string> ring = book.Ring(key);
        return ring.Count > 0 && ring[ring.Count - 1] == PresentCell;
    }

    public static void Flush()
    {
        ledger?.Flush();
    }

    internal static bool IsFreshAmbient(string typeName)
    {
        string key = AmbientHead + Separator + AmbientCategory + Separator + typeName;
        if (Seen(key))
        {
            return false;
        }

        Remember(key);
        return true;
    }

    private static List<string> Fresh(string caseId, string category, List<string> texts)
    {
        List<string> fresh = [];
        foreach (string text in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            string key = caseId + Separator + category + Separator + ShapeOf(text);
            if (Seen(key))
            {
                continue;
            }

            fresh.Add(text);
            Remember(key);
        }

        return fresh;
    }

    private static void Remember(string key)
    {
        if (!RimRunnerSaveProbe.Armed)
        {
            EnsureLoaded().Append(key, SeenCell);
        }
    }

    private static bool Seen(string key)
    {
        return EnsureLoaded().Ring(key).Count > 0;
    }

    internal static void ResetForRun()
    {
        generatedPawnNames.Clear();
    }

    internal static void NotePawnGenerated(Pawn pawn)
    {
        switch (pawn?.Name)
        {
            case NameTriple triple:
                NoteName(triple.First);
                NoteName(triple.Nick);
                NoteName(triple.Last);
                NoteName(triple.ToStringShort);
                NoteName(triple.ToStringFull);
                break;
            case NameSingle single:
                NoteName(single.Name);
                break;
        }
    }

    private static void NoteName(string name)
    {
        if (name != null && name.Length >= 2)
        {
            generatedPawnNames.Add(name);
        }
    }

    internal static string ShapeOf(string text)
    {
        return ShapeOf(text, generatedPawnNames);
    }

    internal static string ShapeOf(string text, IEnumerable<string> pawnNames)
    {
        foreach (string name in pawnNames.OrderByDescending(n => n.Length))
        {
            text = ReplaceBounded(text, name);
        }

        StringBuilder shape = new(text.Length);
        bool inDigits = false;
        foreach (char c in text)
        {
            bool digit = c is >= '0' and <= '9';
            if (digit && !inDigits)
            {
                shape.Append(DigitMarker);
            }
            else if (!digit)
            {
                shape.Append(c);
            }

            inDigits = digit;
        }

        return shape.ToString();
    }

    private static string ReplaceBounded(string text, string name)
    {
        int at = text.IndexOf(name, StringComparison.Ordinal);
        if (at < 0)
        {
            return text;
        }

        StringBuilder result = new(text.Length);
        int copied = 0;
        while (at >= 0)
        {
            int end = at + name.Length;
            if ((at == 0 || !IsWordChar(text[at - 1])) && (end == text.Length || !IsWordChar(text[end])))
            {
                result.Append(text, copied, at - copied).Append(PawnPlaceholder);
                copied = end;
                at = text.IndexOf(name, end, StringComparison.Ordinal);
            }
            else
            {
                at = text.IndexOf(name, at + 1, StringComparison.Ordinal);
            }
        }

        return result.Append(text, copied, text.Length - copied).ToString();
    }

    private static bool IsWordChar(char c)
    {
        return c < 128 && (char.IsLetterOrDigit(c) || c == '_');
    }

    private static RimRunnerRingLedger EnsureLoaded()
    {
        if (ledger != null)
        {
            return ledger;
        }

        HashSet<string> registered = new(
            RimRunnerRegistry.All.Select(testCase => testCase.Id), StringComparer.Ordinal);
        return ledger = RimRunnerRingLedger.ForHistory(RimRunnerLedgerFold.SeenLogsStem, RimRunnerPassRate.ResolveLedgerKey(),
            "出现台账", Cap, FlushThreshold, key => IsEligible(registered, key));
    }

    private static RimRunnerRingLedger EnsureWarningsLoaded()
    {
        if (warningLedger != null)
        {
            return warningLedger;
        }

        HashSet<string> registered = new(
            RimRunnerRegistry.All.Select(testCase => testCase.Id), StringComparer.Ordinal);
        return warningLedger = RimRunnerRingLedger.ForHistory(RimRunnerLedgerFold.SeenWarningsStem,
            RimRunnerPassRate.ResolveLedgerKey(), "带键警告出现台账", RimRunnerLedgerFold.SeenWarningsCap, FlushThreshold,
            key => IsEligible(registered, key));
    }

    internal static bool IsEligible(HashSet<string> registered, string key)
    {
        int end = key.IndexOf(Separator, StringComparison.Ordinal);
        if (end <= 0)
        {
            return false;
        }

        string head = key[..end];
        return head.StartsWith("<", StringComparison.Ordinal) && head.EndsWith(">", StringComparison.Ordinal)
               || registered.Contains(head);
    }
}

[HarmonyPatch(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), typeof(PawnGenerationRequest))]
internal static class Patch_PawnGenerator_GeneratePawn_RimRunnerSeenLogNames
{
    private static void Postfix(Pawn __result)
    {
        RimRunnerSeenLogLedger.NotePawnGenerated(__result);
    }
}
