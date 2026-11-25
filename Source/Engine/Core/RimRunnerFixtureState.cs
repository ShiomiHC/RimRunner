namespace RimRunner;

internal static class RimRunnerFixtureState
{
    private static readonly List<RimRunnerPawnFixtureTable> Tables = [];

    private static readonly WholeState[] WholeStates =
    [
        new("RimRunnerRandControl.Seeded",
            () => RimRunnerRandControl.OpenSeedScopes,
            snapshot => RimRunnerRandControl.CloseLeakedSeedScopes((int)snapshot!) is var leaked and > 0
                ? leaked + " 个 seed 作用域未 Dispose，随机流钉在它的 seed 上；已逐层关闭"
                : null),
        new("RimRunnerRandControl.ScheduleAbilitySeed",
            () => RimRunnerRandControl.PendingAbility,
            snapshot => RimRunnerRandControl.DropLeakedAbilitySeed(snapshot as Ability) is { } ability
                ? "预排给 " + (ability.def?.defName ?? "<null>") + " 的 seed 没被施放消费；已撤销"
                : null),
        new("RimRunnerPainControl.SuppressOthers",
            RimRunnerPainControl.Snapshot,
            snapshot => RimRunnerPainControl.RestoreSnapshot(snapshot!) is { } leaked
                ? leaked + " 的痛觉压制没撤；已撤下"
                : null),
        new("RimRunnerDamageRecorder",
            () => RimRunnerDamageRecorder.IsWatching,
            snapshot => RimRunnerDamageRecorder.ResetAfterSegment((bool)snapshot!) is var watched and > 0
                ? "监控集有 " + watched + " 个 pawn；已停止监控"
                : null),
        new("ctx.BlockHediffBreaksForCase",
            () => RimRunnerHediffBreakBlock.CaseWide,
            snapshot => RestoreFlag((bool)snapshot!, RimRunnerHediffBreakBlock.CaseWide, RimRunnerHediffBreakBlock.SetCaseWide)),
        new("ctx.SuppressRandomMentalStates",
            () => RimRunnerAgencyPin.RandomMentalStatesSuppressed,
            snapshot => RestoreFlag((bool)snapshot!, RimRunnerAgencyPin.RandomMentalStatesSuppressed,
                RimRunnerAgencyPin.SetRandomMentalStatesSuppressed)),
    ];

    internal static void Register(RimRunnerPawnFixtureTable table) => Tables.Add(table);

    internal static RimRunnerResultEntry? CurrentOwner => RimRunnerContext.Active?.Current;

    internal static object?[] Capture()
    {
        object?[] snapshot = new object?[WholeStates.Length];
        for (int i = 0; i < WholeStates.Length; i++)
        {
            snapshot[i] = WholeStates[i].Capture();
        }

        return snapshot;
    }

    internal static List<string> Release(RimRunnerResultEntry owner, object?[]? snapshot, Func<Pawn, bool>? keep = null)
    {
        List<string> lines = [];
        foreach (RimRunnerPawnFixtureTable table in Tables)
        {
            List<Pawn> leaked = table.TakeLeaks(owner, keep);
            if (leaked.Count > 0)
            {
                lines.Add(table.Name + " 有 " + leaked.Count + " 个 pawn 没撤（" + string.Join("、", leaked.Select(Describe))
                          + "）；已撤下");
            }
        }

        if (snapshot == null)
        {
            return lines;
        }

        for (int i = 0; i < WholeStates.Length; i++)
        {
            string? leaked = WholeStates[i].Restore(snapshot[i]);
            if (leaked != null)
            {
                lines.Add(WholeStates[i].Name + "：" + leaked);
            }
        }

        return lines;
    }

    internal static string Describe(Pawn pawn) =>
        pawn.Discarded ? pawn.ThingID + "（已丢弃）" : pawn.LabelShort + "[" + pawn.ThingID + "]";

    private static string? RestoreFlag(bool before, bool now, Action<bool> set)
    {
        if (before == now)
        {
            return null;
        }

        set(before);
        return "整场开关没撤；已复位";
    }

    private sealed class WholeState(string name, Func<object?> capture, Func<object?, string?> restore)
    {
        internal string Name { get; } = name;
        internal object? Capture() => capture();

        internal string? Restore(object? snapshot) => restore(snapshot);
    }
}

internal abstract class RimRunnerPawnFixtureTable
{
    private readonly Dictionary<Pawn, RimRunnerResultEntry?> owners = [];
    private readonly Action<Pawn>? onLeak;

    protected RimRunnerPawnFixtureTable(string name, Action<Pawn>? onLeak)
    {
        Name = name;
        this.onLeak = onLeak;
        RimRunnerFixtureState.Register(this);
    }

    internal string Name { get; }

    protected void Own(Pawn pawn)
    {
        RimRunnerResultEntry? owner = RimRunnerFixtureState.CurrentOwner;
        if (owner != null || !owners.ContainsKey(pawn))
        {
            owners[pawn] = owner;
        }
    }

    internal bool IsOwnedBy(Pawn pawn, RimRunnerResultEntry? owner)
        => owners.TryGetValue(pawn, out RimRunnerResultEntry? recorded) && ReferenceEquals(recorded, owner);

    protected void Disown(Pawn pawn) => owners.Remove(pawn);

    protected void DisownAll() => owners.Clear();

    protected abstract void Drop(Pawn pawn);

    internal List<Pawn> TakeLeaks(RimRunnerResultEntry owner, Func<Pawn, bool>? keep)
    {
        List<Pawn> leaked = owners
            .Where(pair => ReferenceEquals(pair.Value, owner) && (keep == null || !keep(pair.Key)))
            .Select(pair => pair.Key)
            .ToList();
        foreach (Pawn pawn in leaked)
        {
            owners.Remove(pawn);
            Drop(pawn);
            onLeak?.Invoke(pawn);
        }

        return leaked;
    }
}

internal sealed class RimRunnerPawnFixtureSet(string name, Action<Pawn>? onLeak = null)
    : RimRunnerPawnFixtureTable(name, onLeak)
{
    private readonly HashSet<Pawn> pawns = [];

    internal int Count => pawns.Count;

    internal bool Contains(Pawn pawn) => pawns.Contains(pawn);

    internal bool Add(Pawn pawn)
    {
        Own(pawn);
        return pawns.Add(pawn);
    }

    internal bool Remove(Pawn pawn)
    {
        Disown(pawn);
        return pawns.Remove(pawn);
    }

    internal void Clear()
    {
        DisownAll();
        pawns.Clear();
    }

    internal IEnumerable<Pawn> All => pawns;

    protected override void Drop(Pawn pawn) => pawns.Remove(pawn);
}

internal sealed class RimRunnerPawnFixtureMap<TValue>(string name, Action<Pawn>? onLeak = null)
    : RimRunnerPawnFixtureTable(name, onLeak)
{
    private readonly Dictionary<Pawn, TValue> values = [];

    internal bool TryGetValue(Pawn pawn, out TValue value) => values.TryGetValue(pawn, out value!);

    internal void Set(Pawn pawn, TValue value)
    {
        Own(pawn);
        values[pawn] = value;
    }

    internal bool Remove(Pawn pawn)
    {
        Disown(pawn);
        return values.Remove(pawn);
    }

    internal void Clear()
    {
        DisownAll();
        values.Clear();
    }

    protected override void Drop(Pawn pawn) => values.Remove(pawn);
}
