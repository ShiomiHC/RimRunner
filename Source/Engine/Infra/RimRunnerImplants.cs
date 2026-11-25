namespace RimRunner;

/// <summary>植入体挂载：校验部位、配置与槽位后挂上，并登记移除。</summary>
/// <remarks>def 须有 addsHediff 为它的安装配方、部位在该配方的 appliedOnFixedBodyParts 内，且须配 addedPartProps；任一校验不过即抛。</remarks>
public static class RimRunnerImplants
{
    /// <summary>挂一个植入体并登记移除。</summary>
    /// <remarks>槽位校验同 RequireSlotAvailable；挂上的 hediff 经 ctx.TrackHediff 登记移除。</remarks>
    public static Hediff AttachTracked(
        RimRunnerContext ctx, Pawn pawn, HediffDef implantDef, BodyPartDef partDef)
    {
        if (ctx == null)
        {
            throw new ArgumentNullException(nameof(ctx));
        }

        BodyPartRecord part = ResolvePart(pawn, partDef);
        RequireRecipeAllowsPart(implantDef, partDef);
        RequireAddedPartProps(implantDef);
        RequireSlotAvailable(ctx, pawn, partDef);

        Hediff implant = pawn.health.AddHediff(implantDef, part) ?? throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 植入体 " + implantDef.defName + " 挂到 " + partDef.defName
                + " 时 AddHediff 返回 null：" + pawn.LabelShort);
        ctx.TrackHediff(pawn, implant);
        return implant;
    }

    /// <summary>部位解析：按 BodyPartDef 解出 pawn 上的具体部位。</summary>
    /// <remarks>同一 BodyPartDef 有多处时取 body 里排第一的那处；没有即抛。</remarks>
    public static BodyPartRecord ResolvePart(Pawn pawn, BodyPartDef partDef)
    {
        BodyPartRecord part = pawn.RaceProps.body.GetPartsWithDef(partDef).FirstOrDefault() ?? throw new InvalidOperationException(
                RimRunnerLog.Prefix + " " + pawn.LabelShort + " 的 body 里没有部位 " + partDef.defName);
        return part;
    }

    /// <summary>槽位校验：部位上只有伤口或缺失时先修复，有别的 hediff 即抛。</summary>
    /// <remarks>修复连子部位上的 hediff 一并清掉（keepOnBodyPartRestoration 的除外）。子部位上有伤口与缺失以外的 hediff 时保持原状并抛。修复时记一条 Trace 诊断，列出清掉的全部 hediff。</remarks>
    public static void RequireSlotAvailable(
        RimRunnerContext ctx, Pawn pawn, BodyPartDef partDef)
    {
        if (ctx == null)
        {
            throw new ArgumentNullException(nameof(ctx));
        }

        BodyPartRecord part = ResolvePart(pawn, partDef);
        List<Hediff> occupants = pawn.health.hediffSet.hediffs
            .Where(hediff => hediff.Part == part)
            .ToList();
        if (occupants.Count == 0)
        {
            return;
        }

        List<Hediff> restored = pawn.health.hediffSet.hediffs
            .Where(hediff => hediff.Part != null && InSubtree(hediff.Part, part)
                             && !hediff.def.keepOnBodyPartRestoration)
            .ToList();
        Hediff? blocker = occupants.FirstOrDefault(hediff => hediff is not (Hediff_Injury or Hediff_MissingPart))
                          ?? restored.FirstOrDefault(hediff => hediff is not (Hediff_Injury or Hediff_MissingPart));
        if (blocker == null)
        {
            string cleared = string.Join(", ",
                restored.Select(hediff => (hediff.def?.defName ?? "<unknown>") + "@" + hediff.Part.def.defName));
            pawn.health.RestorePart(part, checkStateChange: false);
            ctx.AddDiagnostic(RimRunnerDiagnosticKind.Trace,
                (pawn.Name is NameTriple triple ? triple.Nick : pawn.LabelShort)
                + "/" + partDef.defName,
                "cleared=[" + cleared + "]");
            occupants = pawn.health.hediffSet.hediffs
                .Where(hediff => hediff.Part == part)
                .ToList();
            if (occupants.Count == 0)
            {
                return;
            }

            blocker = occupants[0];
        }

        throw new InvalidOperationException(
            RimRunnerLog.Prefix + " " + pawn.LabelShort + " 的 " + partDef.defName
            + (blocker.Part == part
                ? " 槽位已有 hediff " + (blocker.def?.defName ?? "<unknown>") + "，拒绝叠挂"
                : " 槽位只有伤口或缺失，但修复会连带清掉子部位 " + blocker.Part.def.defName + " 上的 "
                  + (blocker.def?.defName ?? "<unknown>") + "，拒绝修复")
            + "；casterDmgLog=" + (RimRunnerDamageRecorder.HasWindow ? RimRunnerDamageRecorder.Dump() : "（未开受击监控）"));
    }

    private static bool InSubtree(BodyPartRecord candidate, BodyPartRecord root)
    {
        for (BodyPartRecord? current = candidate; current != null; current = current.parent)
        {
            if (current == root) return true;
        }

        return false;
    }

    private static void RequireRecipeAllowsPart(HediffDef implantDef, BodyPartDef partDef)
    {
        RecipeDef recipe = DefDatabase<RecipeDef>.AllDefsListForReading
            .FirstOrDefault(candidate => candidate.addsHediff == implantDef) ?? throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 没有任何安装配方 addsHediff=" + implantDef.defName
                + "，无法确认合法挂载部位；该 hediff 不是可植入体");
        if (recipe.appliedOnFixedBodyParts == null
            || !recipe.appliedOnFixedBodyParts.Contains(partDef))
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " " + implantDef.defName + " 的安装配方 " + recipe.defName
                + " 不允许部位 " + partDef.defName + "；允许集=["
                + string.Join(", ", (recipe.appliedOnFixedBodyParts
                                     ?? []).Select(def => def.defName)) + "]");
        }
    }

    private static void RequireAddedPartProps(HediffDef implantDef)
    {
        if (implantDef.addedPartProps == null)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " " + implantDef.defName
                + " 未配 addedPartProps，无法按植入体挂载");
        }
    }
}
