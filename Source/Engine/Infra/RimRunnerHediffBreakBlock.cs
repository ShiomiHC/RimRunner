namespace RimRunner;

/// <summary>hediff 阶段崩溃屏蔽：被挡的 pawn 按 hediff 阶段掷中崩溃后取消发起。</summary>
/// <remarks>只挡 hediff 阶段 mentalBreakMtbDays 掷中的崩溃：掷骰照付，挑崩溃那次随机数不抽，开与不开之后随机流不同。按 pawn 挡到用例末或回收，整场挡到用例末。</remarks>
public static class RimRunnerHediffBreakBlock
{
    private static readonly RimRunnerPawnFixtureSet Blocked = new("ctx.BlockHediffBreaks");

    /// <summary>整场屏蔽是否生效。</summary>
    public static bool CaseWide { get; private set; }

    /// <summary>本段（用例或其一个 phase）起点以来掷中后被取消发起的次数。</summary>
    /// <remarks>撤登记不清零；本段登记过屏蔽或有过取消时，引擎在段末把它记成一条 Measurement 诊断。</remarks>
    public static int CancelledCount { get; private set; }

    /// <summary>该 pawn 的 hediff 阶段崩溃是否被挡。</summary>
    public static bool IsBlocked(Pawn? pawn) => CaseWide || (pawn != null && Blocked.Count > 0 && Blocked.Contains(pawn));

    internal static bool RegisteredThisSegment { get; private set; }

    internal static void Add(Pawn pawn)
    {
        if (pawn == null) throw new ArgumentNullException(nameof(pawn));
        Blocked.Add(pawn);
        RegisteredThisSegment = true;
        RequireOurPrefix();
    }

    internal static void Remove(Pawn? pawn)
    {
        if (pawn != null) Blocked.Remove(pawn);
    }

    internal static void SetCaseWide(bool on)
    {
        CaseWide = on;
        if (on)
        {
            RegisteredThisSegment = true;
            RequireOurPrefix();
        }
    }

    internal static bool HasOurPrefix(Patches? patches)
    {
        return patches != null && patches.Prefixes.Any(patch => patch.PatchMethod != null
            && patch.PatchMethod.DeclaringType == typeof(Patch_Hediff_TryDoRandomMentalBreak_RimRunnerBlock));
    }

    private static void RequireOurPrefix()
    {
        if (!HasOurPrefix(Harmony.GetPatchInfo(AccessTools.Method(typeof(Hediff), "TryDoRandomMentalBreak"))))
        {
            RimRunnerEngineFault.Raise(
                "Hediff.TryDoRandomMentalBreak 上没有本引擎的前缀：BlockHediffBreaks 的屏蔽不生效");
        }
    }

    internal static void BeginCase()
    {
        CancelledCount = 0;
        RegisteredThisSegment = false;
    }

    internal static void ClearForNewWorld()
    {
        Blocked.Clear();
        CaseWide = false;
        RegisteredThisSegment = false;
    }

    internal static bool AllowBreak(Pawn? pawn)
    {
        if (!IsBlocked(pawn)) return true;
        CancelledCount++;
        return false;
    }
}

[HarmonyPatch(typeof(Hediff), "TryDoRandomMentalBreak")]
internal static class Patch_Hediff_TryDoRandomMentalBreak_RimRunnerBlock
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static bool Prefix(Hediff __instance)
    {
        return RimRunnerHediffBreakBlock.AllowBreak(__instance.pawn);
    }
}
