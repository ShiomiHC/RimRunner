using System.Globalization;

namespace RimRunnerCli;

public static class ShardPlan
{
    public sealed record Cell(string Key, double Weight);

    public sealed record Count(int N, string BoundBy);

    public const string BoundBySlots = "空闲名额";
    public const string BoundByMemory = "内存";
    public const string BoundByCpu = "CPU";
    public const string BoundByCells = "格数";
    public const string BoundByWeight = "权重下限";

    public static string? Target(StructuredRunRequest request, CaseManifest manifest)
    {
        List<CaseManifest.CaseEntry> selected =
        [
            .. manifest.SelectedBy(request),
        ];
        return selected.Count == 1 && selected[0].Shardable && !selected[0].QuietMachine
            ? selected[0].Id
            : null;
    }

    public static int SlotBudget(int freeSlots, bool unlimited, int physical) =>
        unlimited ? Math.Min(freeSlots, Math.Max(1, physical)) : freeSlots;

    public static Count Decide(int freeSlots, int cells, double totalWeight, double minWeight, int? memoryFit = null,
        int? cpuFit = null)
    {
        var terms = new List<(int Value, string Name)> { (freeSlots, BoundBySlots) };
        if (memoryFit is int fit) terms.Add((fit, BoundByMemory));
        if (cpuFit is int cpu) terms.Add((cpu, BoundByCpu));
        terms.Add((cells, BoundByCells));
        if (minWeight > 0)
        {
            double ratio = Math.Floor(totalWeight / minWeight);
            terms.Add(((int)Math.Min(ratio, int.MaxValue), BoundByWeight));
        }

        (int value, string name) = terms[0];
        foreach ((int Value, string Name) term in terms.Skip(1))
        {
            if (term.Value < value) (value, name) = term;
        }

        return new Count(Math.Max(1, value), name);
    }

    public static int MemoryFit(long need, long budget, long vramNeed, long vramOutside, long vramCap)
    {
        if (need <= 0) return int.MaxValue;
        long before = Math.Max(0, vramOutside - vramCap);
        int fit = 0;
        while (fit < MaxMemoryFit)
        {
            long next = fit + 1L;
            long spill = Math.Max(0, vramOutside + next * vramNeed - vramCap) - before;
            if (next * need + spill > budget) break;
            fit++;
        }

        return fit;
    }

    public static int CpuFit(double idle, double reserve, double need = CoresPerShard) =>
        need <= 0 ? MaxMemoryFit
            : (int)Math.Min(Math.Floor(Math.Max(0, idle - reserve) / need), MaxMemoryFit);

    public const double CoresPerShard = 1;

    private const int MaxMemoryFit = 256;

    public static List<Cell> Order(IReadOnlyList<Cell> cells) =>
        [.. cells.OrderByDescending(cell => cell.Weight).ThenBy(cell => cell.Key, StringComparer.Ordinal)];

    public static string NewGroupId(DateTimeOffset now) =>
        now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-"
        + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    public static string FormatWeight(double weight) =>
        weight.ToString("0.##", CultureInfo.InvariantCulture);
}
