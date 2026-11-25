namespace RimRunner;

internal static class RimRunnerMapTicks
{
    private static readonly Dictionary<int, int> ticks = [];
    private static Dictionary<int, int>? atCheckpointSave;

    internal static void Count(Map map)
    {
        ticks.TryGetValue(map.uniqueID, out int count);
        ticks[map.uniqueID] = count + 1;
    }

    internal static int Of(Map map) => ticks.TryGetValue(map.uniqueID, out int count) ? count : 0;

    internal static void NoteCheckpointSave()
    {
        atCheckpointSave = new Dictionary<int, int>(ticks);
    }

    internal static void OnGameLoaded(bool checkpointLoad)
    {
        if (!checkpointLoad || atCheckpointSave == null)
        {
            return;
        }

        ticks.Clear();
        foreach (KeyValuePair<int, int> pair in atCheckpointSave)
        {
            ticks[pair.Key] = pair.Value;
        }
    }
}
