namespace RimRunner;

internal static class RimRunnerForeignDamage
{
    private sealed class Source
    {
        internal required string Label;
        internal int Hits;
        internal float Dealt;
    }

    private sealed class Victim
    {
        internal required string Label;
        internal readonly Dictionary<string, Source> Sources = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, HashSet<string>> preCaseByCase = new(StringComparer.Ordinal);
    private static HashSet<string>? preCase;
    private static bool firstPhase;
    private static readonly Dictionary<string, Victim> victims = new(StringComparer.Ordinal);

    internal static void BeginPhase(string caseId, bool isFirstPhase, IEnumerable<Thing> onMap)
    {
        victims.Clear();
        firstPhase = isFirstPhase;
        if (isFirstPhase || !preCaseByCase.TryGetValue(caseId, out preCase))
        {
            preCase = new HashSet<string>(StringComparer.Ordinal);
            preCaseByCase[caseId] = preCase;
        }
        if (isFirstPhase) AddPreCase(onMap);
    }

    internal static void AddPreCase(IEnumerable<Thing> onMap)
    {
        if (!firstPhase || preCase == null) return;
        foreach (Thing thing in onMap)
        {
            if (thing?.ThingID != null) preCase.Add(thing.ThingID);
        }
    }

    internal static void Note(Pawn victim, DamageInfo dinfo, float dealt)
    {
        Thing instigator = dinfo.Instigator;
        if (dealt <= 0f || instigator?.ThingID == null || preCase == null || !preCase.Contains(instigator.ThingID)
            || !RimRunnerPawns.TryGetSpecTarget(victim, out _))
        {
            return;
        }

        if (!victims.TryGetValue(victim.ThingID, out Victim? entry))
        {
            entry = new Victim { Label = victim.LabelShort + "（" + victim.ThingID + "）" };
            victims[victim.ThingID] = entry;
        }
        if (!entry.Sources.TryGetValue(instigator.ThingID, out Source? source))
        {
            source = new Source { Label = instigator.LabelShort + "（" + instigator.ThingID + "）" };
            entry.Sources[instigator.ThingID] = source;
        }
        source.Hits++;
        source.Dealt += dealt;
    }

    internal static IEnumerable<string> Lines
        => victims.Values.Select(victim =>
            "靶 " + victim.Label + "受到 " + victim.Sources.Values.Sum(source => source.Hits) + " 次开始执行前已在图上的来源的伤害，计 "
            + victim.Sources.Values.Sum(source => source.Dealt).ToString("0.##") + "："
            + string.Join("、", victim.Sources.Values.Select(source =>
                source.Label + " ×" + source.Hits + "（" + source.Dealt.ToString("0.##") + "）")));
}
