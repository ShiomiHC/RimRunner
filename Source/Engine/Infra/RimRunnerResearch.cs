namespace RimRunner;

/// <summary>把研究项目的进度钉成给定值，本 phase 末还原。</summary>
/// <remarks>只改 IsFinished 读的那份进度，前置、技术图纸、分析进度与研究效果保持原值，不发完成信。要验研究完成带来的效果，走 vanilla 的完成流程并自己还原。</remarks>
public static class RimRunnerResearch
{
    internal static readonly AccessTools.FieldRef<ResearchManager, Dictionary<ResearchProjectDef, float>> ProgressRef =
        AccessTools.FieldRefAccess<ResearchManager, Dictionary<ResearchProjectDef, float>>("progress");
    internal static readonly AccessTools.FieldRef<ResearchManager, Dictionary<ResearchProjectDef, float>> KnowledgeRef =
        AccessTools.FieldRefAccess<ResearchManager, Dictionary<ResearchProjectDef, float>>("anomalyKnowledge");

    /// <summary>把研究项目的进度钉成给定值，本 phase 末还原到钉之前。</summary>
    /// <remarks>研发点项目钉研发点，暗知识项目钉知识量，与 vanilla 读进度的分支一致。同一 phase 内多次钉同一项目，末态回到第一次钉之前。</remarks>
    /// <param name="ctx">登记还原的上下文。</param>
    /// <param name="project">研究项目。</param>
    /// <param name="value">进度值；不小于项目的 Cost 即算完成。</param>
    /// <exception cref="InvalidOperationException">项目既无研发点花费，也不是 Anomaly 在场时的暗知识项目，进度恒为 0，无法钉住。</exception>
    public static void Pin(RimRunnerContext ctx, ResearchProjectDef project, float value)
    {
        if (ctx == null)
        {
            throw new ArgumentNullException(nameof(ctx));
        }

        if (project == null)
        {
            throw new ArgumentNullException(nameof(project));
        }

        Pin(project, value, ctx.TrackRestore);
    }

    internal static void Pin(ResearchProjectDef project, float value, Action<Action, string> trackRestore)
    {
        Func<ResearchManager, Dictionary<ResearchProjectDef, float>> table = TableOf(project);
        Dictionary<ResearchProjectDef, float> pinned = table(Find.ResearchManager);
        bool had = pinned.TryGetValue(project, out float original);
        pinned[project] = value;
        trackRestore(() =>
        {
            Dictionary<ResearchProjectDef, float> current = table(Find.ResearchManager);
            if (had)
            {
                current[project] = original;
            }
            else
            {
                current.Remove(project);
            }
        }, "还原研究进度 " + project.defName);
    }

    /// <summary>把研究项目钉成已完成（进度 = Cost），本 phase 末还原。</summary>
    /// <param name="ctx">登记还原的上下文。</param>
    /// <param name="project">研究项目。</param>
    public static void PinFinished(RimRunnerContext ctx, ResearchProjectDef project) => Pin(ctx, project, project?.Cost ?? 0f);

    /// <summary>把研究项目钉成未开始（进度 = 0），本 phase 末还原。</summary>
    /// <param name="ctx">登记还原的上下文。</param>
    /// <param name="project">研究项目。</param>
    public static void PinUnfinished(RimRunnerContext ctx, ResearchProjectDef project) => Pin(ctx, project, 0f);

    private static Func<ResearchManager, Dictionary<ResearchProjectDef, float>> TableOf(ResearchProjectDef project)
    {
        if (project.baseCost > 0f)
        {
            return manager => ProgressRef(manager);
        }

        if (ModsConfig.AnomalyActive && project.knowledgeCost > 0f)
        {
            return manager => KnowledgeRef(manager);
        }

        throw new InvalidOperationException(RimRunnerLog.Prefix + " RimRunnerResearch.Pin：" + project.defName
            + " 没有研发点花费，也不是 Anomaly 在场时的暗知识项目，vanilla 读它的进度恒为 0");
    }
}
