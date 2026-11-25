using System.IO;
using System.Text;

namespace RimRunner;

public static class RimRunnerPassRate
{
    private const int Cap = RimRunnerLedgerFold.PassRateCap;

    internal const string EpochMark = RimRunnerLedgerFold.EpochMark;

    internal const string SaveMark = RimRunnerLedgerFold.SaveMark;

    private const int FlushThreshold = 32;

    private static RimRunnerRingLedger? ledger;

    private static RimRunnerRingLedger? prints;

    private static string? saveFingerprint;

    internal static void NoteSaveLoad(string saveName)
    {
        if (!GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.Save, out string baseline)
            || !string.Equals(saveName, baseline, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using FileStream stream = File.OpenRead(GenFilePaths.FilePathForSavedGame(saveName));
            byte[] hash = System.Security.Cryptography.SHA256.HashData(stream);
            saveFingerprint = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            saveFingerprint = null;
            RimRunnerEngineLog.Warn("基线档指纹未算出，本批不记指纹：" + exception.Message);
        }
    }

    public static string? LedgerFileName()
    {
        if (RimRunnerSaveProbe.Armed)
        {
            return null;
        }

        EnsureLoaded();
        return ledger!.FileName;
    }

    public static string? ResolveModListName()
    {
        return GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.ModList, out string modList)
               && !string.IsNullOrEmpty(modList)
            ? modList
            : null;
    }

    public static string? ResolveCustomSave()
    {
        return GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.CustomSave, out string path)
               && !string.IsNullOrEmpty(path)
            ? path
            : null;
    }

    public static string? ResolveLedgerKey()
    {
        string? modList = ResolveModListName();
        return ResolveCustomSave() is string save ? RimRunnerDataLayout.SaveLedgerKey(modList, save) : modList;
    }

    public static void RecordAndAnnotate(RimRunnerResultEntry entry)
    {
        if (RimRunnerSaveProbe.Armed)
        {
            return;
        }

        EnsureLoaded();
        if (!ledger!.IsEligible(entry.id))
        {
            return;
        }

        IReadOnlyList<string> ring;
        if (entry.status is RimRunnerOutcomes.SentinelFault or RimRunnerOutcomes.EngineFault)
        {
            ring = ledger.Ring(entry.id);
        }
        else
        {
            NoteSave(entry.id);
            ring = ledger.Append(entry.id, entry.status!)!;
        }

        entry.passRate10 = Rate(ring, 10);
        entry.outcomeTail30 = Tail(ring, 30);
        if (entry.status == RimRunnerOutcomes.Fail)
        {
            entry.saveChangedSincePass = SaveChangedSincePass(ring);
            RimRunnerTestCase? testCase = RimRunnerRegistry.FindCase(entry.id);
            entry.onMainMap = testCase != null && testCase.Level == RimRunnerLevel.World && !testCase.OnIsolatedMap;
        }
    }

    private static void NoteSave(string caseId)
    {
        if (saveFingerprint == null)
        {
            return;
        }

        IReadOnlyList<string> known = prints!.Ring(caseId);
        string? last = known.Count > 0 ? known[known.Count - 1] : null;
        if (last == saveFingerprint)
        {
            return;
        }

        if (last != null)
        {
            ledger!.Append(caseId, SaveMark);
        }

        prints.Append(caseId, saveFingerprint);
    }

    private static bool SaveChangedSincePass(IReadOnlyList<string> ring)
    {
        bool marked = false;
        for (int i = ring.Count - 2; i >= 0; i--)
        {
            if (ring[i] == SaveMark)
            {
                marked = true;
            }
            else if (CountsAsPass(ring[i]))
            {
                return marked;
            }
        }

        return false;
    }

    public static void Flush()
    {
        ledger?.Flush();
        prints?.Flush();
    }

    private static List<string> LiveCells(IReadOnlyList<string> ring)
    {
        int start = 0;
        for (int i = ring.Count - 1; i >= 0; i--)
        {
            if (ring[i] == EpochMark)
            {
                start = i + 1;
                break;
            }
        }

        List<string> cells = new(ring.Count - start);
        for (int i = start; i < ring.Count; i++)
        {
            cells.Add(ring[i]);
        }

        return cells;
    }

    private static string Rate(IReadOnlyList<string> ring, int n)
    {
        List<string> samples = LiveCells(ring).Where(cell => cell != SaveMark).ToList();
        int take = Math.Min(n, samples.Count);
        if (take == 0)
        {
            return "0/0";
        }

        int pass = 0;
        for (int i = samples.Count - take; i < samples.Count; i++)
        {
            if (CountsAsPass(samples[i]))
            {
                pass++;
            }
        }

        return pass + "/" + take;
    }

    private static string Tail(IReadOnlyList<string> ring, int n)
    {
        List<string> cells = LiveCells(ring);
        StringBuilder builder = new();
        int taken = 0;
        for (int i = cells.Count - 1; i >= 0 && taken < n; i--)
        {
            if (cells[i] == SaveMark)
            {
                builder.Insert(0, RimRunnerLedgerFold.SaveGlyph);
            }
            else
            {
                builder.Insert(0, OutcomeChar(cells[i]));
                taken++;
            }
        }

        return builder.ToString();
    }

    private static bool CountsAsPass(string status)
    {
        return RimRunnerOutcomes.CountsAsPass(status);
    }

    private static char OutcomeChar(string status)
    {
        return RimRunnerOutcomes.GlyphOf(status);
    }

    private static void EnsureLoaded()
    {
        if (ledger != null)
        {
            return;
        }

        HashSet<string> eligible = new(
            RimRunnerRegistry.All
                .Where(testCase => testCase.Kind.IsRegression())
                .Select(testCase => testCase.Id),
            StringComparer.Ordinal);
        string? modList = ResolveLedgerKey();
        ledger = RimRunnerRingLedger.ForHistory(RimRunnerDataLayout.PassRateLedgerStem, modList, "通过率台账", Cap, FlushThreshold,
            eligible.Contains);
        prints = RimRunnerRingLedger.ForHistory(RimRunnerLedgerFold.SavePrintsStem, modList, "基线档指纹台账",
            RimRunnerLedgerFold.SavePrintsCap, FlushThreshold, eligible.Contains);
        if (ledger.FileName == null && !RimRunnerCompatOff.On)
        {
            RimRunnerEngineFault.Raise("mod 名单名含非法字符，本轮不写通过率台账文件：" + modList);
        }
    }
}
