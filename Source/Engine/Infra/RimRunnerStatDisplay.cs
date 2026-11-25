namespace RimRunner;

/// <summary>stat 呈现面两条取数路线：复刻侧点判，反射侧取真实行集。</summary>
public static class RimRunnerStatDisplay
{
    private static MethodInfo? statsToDrawForThing;

    /// <summary>复刻侧：该 stat 会不会出现在 thing 的信息卡上。</summary>
    public static bool WouldShowInInfoCard(Thing thing, StatDef stat)
    {
        StatRequest req = StatRequest.For(thing);
        if (!stat.Worker.ShouldShowFor(req)) return false;
        if (!stat.showNonAbstract) return false;

        StatDrawEntry entry;
        if (stat.Worker.IsDisabledFor(thing))
        {
            entry = new StatDrawEntry(stat.category, stat);
        }
        else
        {
            float value = thing.GetStatValue(stat);
            if (!stat.showOnDefaultValue && value == stat.defaultBaseValue) return false;
            entry = new StatDrawEntry(stat.category, stat, value, req);
        }

        return entry.ShouldDisplay(thing);
    }

    /// <summary>反射侧：信息卡上实际的行集合。</summary>
    /// <remarks>含 def 与 thing 级 SpecialDisplayStats 追加的行，顺序不保证。def.hideStats 为真且 showHiddenInfo 关时只剩 overridesHideStats 的行。</remarks>
    /// <exception cref="InvalidOperationException">无法解析 vanilla 信息卡的行集合时抛。</exception>
    public static List<StatDrawEntry> EnumerateInfoCardRows(Thing thing)
    {
        object? raw = StatsToDrawForThing().Invoke(null, [thing]);
        if (raw is not IEnumerable<StatDrawEntry> rows)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " [engine] StatsReportUtility.StatsToDraw(Thing) 的返回值不是 "
                                    + "IEnumerable<StatDrawEntry>，实际=" + (raw?.GetType().FullName ?? "<null>")
                                    + "；反射侧取数已失效，凡「不出现任何 X」的全称断言都不再有意义");
        }

        List<StatDrawEntry> entries = [.. thing.def.SpecialDisplayStats(StatRequest.For(thing)), .. rows];
        entries.RemoveAll(entry =>
            (entry.stat != null && !entry.stat.showNonAbstract) || !entry.ShouldDisplay(thing));
        return entries;
    }

    private static MethodInfo StatsToDrawForThing()
    {
        if (statsToDrawForThing != null) return statsToDrawForThing;

        MethodInfo method = typeof(StatsReportUtility).GetMethod(
            "StatsToDraw",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(Thing)],
            modifiers: null) ?? throw new InvalidOperationException(
                RimRunnerLog.Prefix + " [engine] 无法解析 StatsReportUtility.StatsToDraw(Thing)："
                                    + "无法取得信息卡真实行集合，"
                                    + "RimRunnerStatDisplay 的反射侧断言全部失去意义");
        if (!typeof(IEnumerable<StatDrawEntry>).IsAssignableFrom(method.ReturnType))
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " [engine] StatsReportUtility.StatsToDraw(Thing) 的返回类型不再是 "
                                    + "IEnumerable<StatDrawEntry>，实际=" + method.ReturnType.FullName);
        }

        return statsToDrawForThing = method;
    }

    /// <summary>断言信息卡不隐藏 stat：thing 的 def.hideStats 为真即红。</summary>
    public static void AssertStatsNotHidden(
        RimRunnerRegressionContext ctx, string label, Thing thing)
    {
        ctx.AssertTrue(label, !thing.def.hideStats,
            thing.def.defName + " 的 ThingDef.hideStats 为真：信息卡只显示 overridesHideStats 的 stat 行", RimRunnerExpectationSource.Literal("hideStats 为真且 showHiddenInfo 关时，"
                + "ShouldDisplay(thing) 只对 overridesHideStats 的行返 true；对该 thing 做的否定式全称断言恒真"));
    }
}
