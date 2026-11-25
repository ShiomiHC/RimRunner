namespace RimRunner;

/// <summary>随机序列控制：seed 作用域 + 按阈值找 seed。</summary>
public static class RimRunnerRandControl
{
    private static int pendingAbilitySeed;

    private static readonly List<SeedScope> OpenScopes = [];

    private static readonly FieldInfo StateStackField =
        typeof(Rand).GetField("stateStack", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(RimRunnerLog.Prefix + " Verse.Rand 没有 private static 字段 stateStack，seed 作用域无法按栈深弹回");

    internal static int StackDepth => ((Stack<ulong>)StateStackField.GetValue(null)!).Count;

    /// <summary>seed 作用域：作用域内 Rand 从 seed 起，Dispose 时恢复进入前的随机状态。</summary>
    /// <remarks>先开的作用域先 Dispose 时，后开的一并关掉。用例末仍没 Dispose 的由引擎弹回并记 FIXTURE-RESIDUE。手写的 PushState/PopState 不进段末核对，漏配 PopState 不会复位。</remarks>
    public static IDisposable Seeded(int seed)
    {
        Rand.PushState(seed);
        SeedScope scope = new(StackDepth);
        OpenScopes.Add(scope);
        return scope;
    }

    internal static int OpenSeedScopes => OpenScopes.Count;

    internal static int CloseLeakedSeedScopes(int open)
    {
        int leaked = OpenScopes.Count - open;
        if (leaked <= 0)
        {
            return 0;
        }

        OpenScopes[open].Dispose();
        return leaked;
    }

    private sealed class SeedScope(int depth) : IDisposable
    {
        public void Dispose()
        {
            int index = OpenScopes.IndexOf(this);
            if (index < 0)
            {
                return;
            }

            OpenScopes.RemoveRange(index, OpenScopes.Count - index);
            while (StackDepth >= depth)
            {
                Rand.PopState();
            }
        }
    }

    /// <summary>找前 rolls 个 Rand.Value 全 ≥ threshold 的 seed。</summary>
    /// <exception cref="InvalidOperationException">0–4999 内没有满足的 seed 时抛。</exception>
    /// <param name="rolls">要盖住的抽数，取值须覆盖被测路径的全部 Rand 消费。</param>
    /// <param name="threshold">下界，不小于 p 时 Rand.Chance(p) 在这几抽上都落空。</param>
    public static int FindSeedAllAtLeast(int rolls, float threshold)
    {
        for (int seed = 0; seed < 5000; seed++)
        {
            Rand.PushState(seed);
            bool allAbove = true;
            for (int i = 0; i < rolls; i++)
            {
                if (Rand.Value < threshold)
                {
                    allAbove = false;
                    break;
                }
            }

            Rand.PopState();
            if (allAbove)
            {
                return seed;
            }
        }

        throw new InvalidOperationException(
            RimRunnerLog.Prefix + " 5000 个 seed 内未找到满足 " + rolls + "×Rand.Value≥" + threshold + " 的种子");
    }

    /// <summary>找前 rolls 个 Rand.Value 全 &lt; threshold 的 seed。</summary>
    /// <exception cref="InvalidOperationException">0–4999 内没有满足的 seed 时抛。</exception>
    /// <param name="rolls">要盖住的抽数，取值须覆盖被测路径的全部 Rand 消费。</param>
    /// <param name="threshold">上界，Rand.Value 全落在它之下。</param>
    public static int FindSeedAllBelow(int rolls, float threshold)
    {
        for (int seed = 0; seed < 5000; seed++)
        {
            Rand.PushState(seed);
            bool allBelow = true;
            for (int i = 0; i < rolls; i++)
            {
                if (Rand.Value >= threshold)
                {
                    allBelow = false;
                    break;
                }
            }

            Rand.PopState();
            if (allBelow)
            {
                return seed;
            }
        }

        throw new InvalidOperationException(
            RimRunnerLog.Prefix + " 5000 个 seed 内未找到满足 " + rolls + "×Rand.Value<" + threshold + " 的种子");
    }

    /// <summary>给某能力的下一次施放预排一个 seed。</summary>
    /// <remarks>只作用于该能力下一次 Activate(LocalTargetInfo, LocalTargetInfo) 之内，调用结束即还原，有未消费的再排即抛。用例末（多阶段为 phase 末）没消费的由引擎撤销并记 FIXTURE-RESIDUE。</remarks>
    public static void ScheduleAbilitySeed(Ability ability, int seed)
    {
        if (PendingAbility != null)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 已有待消费的 Ability.Activate seed："
                                    + PendingAbility.def?.defName);
        }

        PendingAbility = ability ?? throw new ArgumentNullException(nameof(ability));
        pendingAbilitySeed = seed;
    }

    internal static Ability? PendingAbility { get; private set; }

    internal static Ability? DropLeakedAbilitySeed(Ability? before)
    {
        Ability? pending = PendingAbility;
        if (pending == null || ReferenceEquals(pending, before))
        {
            return null;
        }

        PendingAbility = before;
        return pending;
    }

    internal static bool TryPushScheduledAbilitySeed(Ability ability)
    {
        if (!ReferenceEquals(PendingAbility, ability))
        {
            return false;
        }

        PendingAbility = null;
        Rand.PushState(pendingAbilitySeed);
        return true;
    }

    internal static void PopScheduledAbilitySeed(bool pushed)
    {
        if (pushed)
        {
            Rand.PopState();
        }
    }

    /// <summary>撤销某能力的预排 seed。</summary>
    public static void CancelAbilitySeed(Ability ability)
    {
        if (ReferenceEquals(PendingAbility, ability))
        {
            PendingAbility = null;
        }
    }
}
