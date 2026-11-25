using System.Diagnostics;

namespace RimRunner;

internal static class RimRunnerBattleLogDiscardProbe
{
    private const int FramesShown = 8;

    private static readonly Type[] Plumbing =
        [typeof(RimRunnerBattleLogDiscardProbe), typeof(Patch_BattleLog_Add_RimRunnerBattleLogDiscardProbe), typeof(BattleLog)];

    private static readonly HashSet<string> seen = [];

    internal static void ResetForRun()
    {
        seen.Clear();
    }

    internal static void Note(LogEntry entry)
    {
        RimRunnerContext? ctx = RimRunnerContext.Active;
        if (ctx == null || RimRunnerContext.AmbientPhase != RimRunnerPhase.Case)
        {
            return;
        }

        string caseId = RimRunnerObservationBuffer.ActiveCaseId ?? "";
        List<Thing> concerns = entry.GetConcerns().ToList();
        foreach (Thing concern in concerns)
        {
            if (concern is not Pawn { Discarded: true } pawn
                || !seen.Add(caseId + "|" + entry.GetType().Name + "|" + pawn.ThingID))
            {
                continue;
            }

            List<string> frames = [];
            foreach (StackFrame frame in new StackTrace(0, false).GetFrames() ?? [])
            {
                MethodBase? method = RimRunnerCallSite.MethodOf(frame);
                if (method?.DeclaringType == null || Plumbing.Contains(method.DeclaringType))
                {
                    continue;
                }

                frames.Add(method.DeclaringType.Name + "." + method.Name);
                if (frames.Count == FramesShown)
                {
                    break;
                }
            }

            string text = entry.GetType().Name + " 写给已丢弃的 " + pawn.ThingID + "；concerns="
                          + string.Join(",", concerns.Select(thing => thing.ThingID + (thing is Pawn { Discarded: true } ? "(丢弃)" : "")))
                          + "：" + string.Join(" ← ", frames);
            ctx.AddDiagnostic(RimRunnerDiagnosticKind.Trace, "BattleLog 写给已丢弃的 pawn", text);
        }
    }
}

[HarmonyPatch(typeof(BattleLog), nameof(BattleLog.Add))]
internal static class Patch_BattleLog_Add_RimRunnerBattleLogDiscardProbe
{
    private static bool Prepare() => RimRunnerTraceTier.On;

    private static void Postfix(LogEntry entry)
    {
        RimRunnerBattleLogDiscardProbe.Note(entry);
    }
}
