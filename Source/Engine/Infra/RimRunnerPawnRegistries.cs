namespace RimRunner;

internal static class RimRunnerPawnRegistries
{
    internal static void OnGameLoaded(bool checkpointLoad)
    {
        if (checkpointLoad)
        {
            RimRunnerThreatDisable.ReapplyAfterCheckpointLoad();
            return;
        }

        RimRunnerAgencyPin.ClearForNewWorld();
        RimRunnerNeedsFreeze.ClearForNewWorld();
        RimRunnerInvulnerability.ClearForNewWorld();
        RimRunnerThreatDisable.Clear();
        RimRunnerHediffBreakBlock.ClearForNewWorld();
    }
}
