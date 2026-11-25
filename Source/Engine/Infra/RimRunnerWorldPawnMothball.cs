using RimWorld.Planet;

namespace RimRunner;

internal static class RimRunnerWorldPawnMothball
{
    private static readonly FieldInfo PawnsAliveField = AccessTools.Field(typeof(WorldPawns), "pawnsAlive");
    private static readonly FieldInfo PawnsMothballedField = AccessTools.Field(typeof(WorldPawns), "pawnsMothballed");
    private static readonly MethodInfo ShouldMothballMethod = AccessTools.Method(typeof(WorldPawns), "ShouldMothball");

    internal static void OnGameLoaded(bool checkpointLoad)
    {
        if (checkpointLoad || !GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            return;
        }

        WorldPawns worldPawns = Find.WorldPawns;
        if (worldPawns == null)
        {
            return;
        }

        if (PawnsAliveField == null || PawnsMothballedField == null || ShouldMothballMethod == null)
        {
            RimRunnerEngineFault.Raise("载档后冬眠 world pawn：WorldPawns 上无法解析 pawnsAlive / pawnsMothballed / ShouldMothball，"
                      + "本轮 world pawn 照 vanilla 等到第一个 15000 整点才冬眠");
            return;
        }

        HashSet<Pawn> alive = (HashSet<Pawn>)PawnsAliveField.GetValue(worldPawns)!;
        HashSet<Pawn> mothballed = (HashSet<Pawn>)PawnsMothballedField.GetValue(worldPawns)!;
        List<Pawn> eligible = [];
        foreach (Pawn pawn in alive)
        {
            try
            {
                if ((bool)ShouldMothballMethod.Invoke(worldPawns, [pawn])!)
                {
                    eligible.Add(pawn);
                }
            }
            catch (Exception exception)
            {
                RimRunnerEngineFault.Raise("载档后冬眠 world pawn：判定 " + pawn.ToStringSafe() + " 抛出异常，留在 pawnsAlive —— "
                          + (exception.InnerException ?? exception));
            }
        }

        foreach (Pawn pawn in eligible)
        {
            alive.Remove(pawn);
            mothballed.Add(pawn);
        }
    }
}
