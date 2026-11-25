namespace RimRunner;

public static class RimRunnerLedgerFold
{
    public const string SeenLogsStem = "seen-logs";
    public const string SeenWarningsStem = "seen-warnings";
    public const string HolderShapesStem = "holder-shapes";
    public const string CheckpointTicksStem = "checkpoint-ticks";
    public const string SavePrintsStem = "save-prints";
    public const string AckMissesStem = "ack-misses";

    public const string EpochMark = "<epoch>";
    public const string SaveMark = "<save>";

    public static bool IsMark(string cell) => cell is EpochMark or SaveMark;

    public const char EpochGlyph = '|';
    public const char SaveGlyph = '¦';

    public const int PassRateCap = 100;
    public const int SeenLogsCap = 1;
    public const int SeenWarningsCap = 1;
    public const int HolderShapesCap = 1;
    public const int SavePrintsCap = 1;
    public const int AckMissesCap = 1;

    public enum Rule
    {
        Worst,
        FirstSeen,
        Drop,
        Latest,
        Min
    }

    public static bool TryRuleOf(string stem, out Rule rule, out int cap)
    {
        switch (stem)
        {
            case RimRunnerDataLayout.PassRateLedgerStem:
                rule = Rule.Worst;
                cap = PassRateCap;
                return true;
            case SeenLogsStem:
                rule = Rule.FirstSeen;
                cap = SeenLogsCap;
                return true;
            case HolderShapesStem:
                rule = Rule.FirstSeen;
                cap = HolderShapesCap;
                return true;
            case CheckpointTicksStem:
                rule = Rule.Drop;
                cap = 0;
                return true;
            case SavePrintsStem:
                rule = Rule.Latest;
                cap = SavePrintsCap;
                return true;
            case SeenWarningsStem:
                rule = Rule.Latest;
                cap = SeenWarningsCap;
                return true;
            case AckMissesStem:
                rule = Rule.Min;
                cap = AckMissesCap;
                return true;
            default:
                rule = Rule.Drop;
                cap = 0;
                return false;
        }
    }

    public static readonly string[] KnownStems =
    [
        RimRunnerDataLayout.PassRateLedgerStem, SeenLogsStem, SeenWarningsStem, HolderShapesStem, CheckpointTicksStem, SavePrintsStem,
        AckMissesStem
    ];

    public static System.Collections.Generic.List<string> Fold(Rule rule, System.Collections.Generic.IReadOnlyList<string> cells)
    {
        System.Collections.Generic.List<string> folded = new(2);
        switch (rule)
        {
            case Rule.Worst:
            {
                string? worst = null;
                bool saveMarked = false;
                for (int i = 0; i < cells.Count; i++)
                {
                    if (cells[i] == SaveMark)
                    {
                        saveMarked = true;
                        continue;
                    }

                    if (worst == null || Rank(cells[i]) > Rank(worst)) worst = cells[i];
                }

                if (saveMarked) folded.Add(SaveMark);
                if (worst != null) folded.Add(worst);
                return folded;
            }
            case Rule.Latest:
                folded.Add(cells[cells.Count - 1]);
                return folded;
            case Rule.Min:
            {
                string? least = null;
                for (int i = 0; i < cells.Count; i++)
                {
                    if (least == null || MissesOf(cells[i]) < MissesOf(least)) least = cells[i];
                }

                folded.Add(least!);
                return folded;
            }
            default:
                folded.Add(cells[0]);
                return folded;
        }
    }

    public static int MissesOf(string cell) =>
        int.TryParse(cell, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int misses)
            ? misses : int.MaxValue;

    public static void Merge(System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> main,
        System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.List<string>>> folded,
        Rule rule, int cap)
    {
        if (rule == Rule.Drop) return;
        foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.List<string>> pair in folded)
        {
            if (!main.TryGetValue(pair.Key, out System.Collections.Generic.List<string>? ring))
            {
                ring = [];
                main[pair.Key] = ring;
            }
            else if (rule == Rule.FirstSeen)
            {
                continue;
            }
            else if (rule is Rule.Latest or Rule.Min)
            {
                ring.Clear();
            }

            ring.AddRange(pair.Value);
            if (ring.Count > cap) ring.RemoveRange(0, ring.Count - cap);
        }
    }

    private static int Rank(string status)
    {
        if (status == RimRunnerOutcomes.Pass) return 0;
        if (status == RimRunnerOutcomes.NoAssertions) return 1;
        if (status == RimRunnerOutcomes.Fail) return 2;
        if (status == RimRunnerOutcomes.UnclaimedFault) return 3;
        return 4;
    }
}
