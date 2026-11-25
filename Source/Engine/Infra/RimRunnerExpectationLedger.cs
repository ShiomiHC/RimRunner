namespace RimRunner;

public static class RimRunnerExpectationLedger
{
    private static object? scope;
    private static readonly HashSet<string> Loaded = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Consumed = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoadedParams = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ConsumedParams = new(StringComparer.Ordinal);

    public static void BeginScope(object entry)
    {
        if (ReferenceEquals(scope, entry))
        {
            return;
        }

        scope = entry;
        Loaded.Clear();
        Consumed.Clear();
        LoadedParams.Clear();
        ConsumedParams.Clear();
    }

    public static void NoteLoaded(string fileId, string caseId)
    {
        Loaded.Add(Key(fileId, caseId));
    }

    public static void NoteConsumed(RimRunnerExpectationCase expectationCase)
    {
        Consumed.Add(Key(expectationCase.fileId, expectationCase.caseId));
    }

    public static void NoteLoadedParam(string fileId, string name)
    {
        LoadedParams.Add(ParamKey(fileId, name));
    }

    public static void NoteConsumedParam(string fileId, string name)
    {
        ConsumedParams.Add(ParamKey(fileId, name));
    }

    public static List<string> Unconsumed()
    {
        List<string> merged = [.. Loaded.Except(Consumed), .. LoadedParams.Except(ConsumedParams)];
        merged.Sort(StringComparer.Ordinal);
        return merged;
    }

    private static string Key(string fileId, string caseId)
    {
        return fileId + "/" + caseId;
    }

    private static string ParamKey(string fileId, string name)
    {
        return fileId + "/params:" + name;
    }
}
