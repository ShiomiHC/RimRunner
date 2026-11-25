namespace RimRunner;

internal static class RimRunnerRetentionProbe
{
    private const int SweepEvery = 32;
    private const int CheckEvery = 500;
    private const int GrowthFloor = 32;
    private const int MaxScratchCuts = 4;

    private struct Recycled
    {
        internal WeakReference Ref;
        internal int Seq;
    }

    private static readonly List<Recycled> recycled = [];
    private static int noted;
    private static int cohortEdge;
    private static int peak;
    private static bool lastGrew;
    private static bool warned;
    private static string? stepSubject;
    private static bool raised;
    private static int scratchCuts;

    internal enum Verdict
    {
        None,
        Step,
        Sustained,
    }

    internal static void BeginCase()
    {
        recycled.Clear();
        noted = 0;
        cohortEdge = 0;
        peak = -1;
        lastGrew = false;
        warned = false;
        stepSubject = null;
        raised = false;
        scratchCuts = 0;
    }

    internal static void NoteRecycled(Pawn pawn)
    {
        bool counts = RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Case => !RimRunnerScenarioManager.Assembling,
            RimRunnerPhase.Scene or RimRunnerPhase.Enumeration or RimRunnerPhase.None => false
        };
        if (!counts)
        {
            return;
        }

        noted++;
        recycled.Add(new Recycled { Ref = new WeakReference(pawn), Seq = noted });
        if (noted % SweepEvery == 0)
        {
            RimRunnerStaleReferenceSweep.Sweep();
        }

        if (noted % CheckEvery == 0)
        {
            Check(RimRunnerContext.Active, "第 " + noted + " 次回收", true);
        }
    }

    internal static void EndCase(RimRunnerContext ctx)
    {
        if (noted == 0)
        {
            return;
        }

        RimRunnerStaleReferenceSweep.ClearQueryScratch();
        if (cohortEdge > 0 && noted > cohortEdge)
        {
            Check(ctx, "用例收尾", false);
        }
        else
        {
            RimRunnerStaleReferenceSweep.Sweep();
        }

        recycled.Clear();
    }

    private static void Check(RimRunnerContext? ctx, string moment, bool caseBodyLive)
    {
        RimRunnerStaleReferenceSweep.Sweep();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int edge = cohortEdge;
        int aliveAll = 0;
        List<object> survivors = [];
        recycled.RemoveAll(entry =>
        {
            object? target = entry.Ref.Target;
            if (target == null)
            {
                return true;
            }

            aliveAll++;
            if (entry.Seq <= edge)
            {
                survivors.Add(target);
            }

            return false;
        });
        int interval = noted - edge;
        int baseline = edge == 0 ? -1 : peak;
        cohortEdge = noted;

        ctx?.AddDiagnostic(RimRunnerDiagnosticKind.Measurement, "回收后仍存活的 pawn（" + moment + "）",
            "早于上次计数回收的 " + survivors.Count + "/" + edge + " · 全部 " + aliveAll + "/" + noted
            + " · 托管堆 " + (GC.GetTotalMemory(false) / 1048576) + "MB");

        KeyValuePair<string, object?>[] extraRoots = [new("RimRunnerContext.Active", ctx)];

        Verdict verdict = Verdict.None;
        string? uncut = null;
        if (edge != 0)
        {
            verdict = Judge(peak, lastGrew, survivors.Count, interval);
            bool acts = !raised && (verdict == Verdict.Sustained || (verdict == Verdict.Step && !warned));
            if (acts && caseBodyLive)
            {
                uncut = scratchCuts >= MaxScratchCuts ? "本用例的 " + MaxScratchCuts + " 次切表重数已用完"
                    : !RimRunnerClassInit.Bound ? "无法读取静态根"
                    : null;
                if (uncut == null)
                {
                    bool? onlyScratch = OnlyQueryScratch(ctx, moment, survivors, interval, extraRoots);
                    if (onlyScratch == true)
                    {
                        verdict = Verdict.None;
                    }
                    else if (onlyScratch == null)
                    {
                        uncut = "全图对象数超过扫描上限，扫描尚未走完";
                    }
                }
            }

            Settle(ref peak, ref lastGrew, survivors.Count, verdict);
        }

        string? previousStep = stepSubject;
        stepSubject = null;
        if (verdict == Verdict.None || raised || (verdict == Verdict.Step && warned))
        {
            return;
        }

        int unreadRemainder = 0;
        string? holders = RimRunnerClassInit.Bound ? RimRunnerHolderShapes.ExplainKnown(survivors, extraRoots) : UnscannedHolders();
        if (holders == null)
        {
            RimRunnerHolderScan.Result scan = RimRunnerHolderScan.Scan(survivors, extraRoots);
            RimRunnerHolderShapes.Remember(scan);
            holders = RimRunnerHolderScan.Describe(scan, caseBodyLive);
            unreadRemainder = RimRunnerHolderScan.UnreadRootRemainder(scan, caseBodyLive);
        }

        string subject = moment + " 早于上次计数回收的存活 " + survivors.Count + "（此前最高 " + baseline + "，本间隔回收 " + interval + " 个）："
                         + holders;
        if (verdict == Verdict.Step)
        {
            warned = true;
            stepSubject = subject;
            ctx?.AddDiagnostic(RimRunnerDiagnosticKind.Measurement, "回收滞留台阶（单次台阶不出滞留警告）", subject);
            return;
        }

        if (previousStep != null)
        {
            subject += "；前一次计数（台阶）的持有路径：" + previousStep;
        }

        raised = true;
        if (uncut != null)
        {
            RimRunnerSentinel.Warn(RimRunnerWarningKey.Retention, "已回收的 pawn 连续两次计数越过此前最高值。未能切掉逐次查询结果表重数（"
                                    + uncut + "），越过的这一段未区分滞留与结果表随图里的 pawn 数变大",
                subject);
            return;
        }

        if (unreadRemainder > 0)
        {
            RimRunnerEngineLog.Mark(RimRunnerObservationKeys.RetentionRemainder, "收尾计数有 " + unreadRemainder + " 个已回收的 pawn，中途计数与收尾计数两段都未到达");
        }

        RimRunnerSentinel.Warn(RimRunnerWarningKey.Retention, "已回收的 pawn 仍被引用、无法被 GC 回收，长用例的托管内存随回收数线性增长",
            subject);
    }

    private static string UnscannedHolders()
    {
        return "持有路径未扫描：" + RimRunnerClassInit.BindFault + "，无法判断类型的静态构造器是否运行过，读静态根会替游戏运行构造器";
    }

    private static bool? OnlyQueryScratch(RimRunnerContext? ctx, string moment, List<object> survivors, int interval,
        KeyValuePair<string, object?>[] extraRoots)
    {
        scratchCuts++;
        RimRunnerHolderScan.Result? cut = RimRunnerHolderScan.ReachableWithout(survivors, extraRoots,
            RimRunnerStaleReferenceSweep.QueryScratchTables());
        if (cut == null)
        {
            return null;
        }

        if (Judge(peak, lastGrew, cut.Reached, interval) != Verdict.None)
        {
            return false;
        }

        ctx?.AddDiagnostic(RimRunnerDiagnosticKind.Measurement, "回收存活越过最高值，切除逐次查询结果表后不过门槛（不出滞留警告）",
            moment + " 早于上次计数回收的存活 " + survivors.Count + "（此前最高 " + peak + "，本间隔回收 " + interval + " 个），"
            + "切掉逐次查询结果表后从静态根到达 " + cut.Reached + "（遍历 " + cut.Visited + " 个对象，" + cut.ElapsedMs + "ms）；"
            + "最高值记为 " + Math.Max(peak, survivors.Count));
        return true;
    }

    internal static Verdict Advance(ref int peak, ref bool previousGrew, int survivors, int interval)
    {
        Verdict verdict = Judge(peak, previousGrew, survivors, interval);
        Settle(ref peak, ref previousGrew, survivors, verdict);
        return verdict;
    }

    private static void Settle(ref int peak, ref bool previousGrew, int survivors, Verdict verdict)
    {
        if (peak >= 0)
        {
            previousGrew = verdict != Verdict.None;
        }

        peak = Math.Max(peak, survivors);
    }

    internal static Verdict Judge(int baseline, bool previousGrew, int survivors, int interval)
    {
        int growth = survivors - baseline;
        if (baseline < 0 || growth <= GrowthFloor || growth * 20 <= interval)
        {
            return Verdict.None;
        }

        return previousGrew ? Verdict.Sustained : Verdict.Step;
    }
}
