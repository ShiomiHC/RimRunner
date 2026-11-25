namespace RimRunnerCli;

public static class Junction
{
    public static bool Exists(string path) =>
        RimTooling.Windows.Junction.IsReparsePoint(CommandContext.FullPath(path));

    public static void Create(string junctionPath, string targetPath)
    {
        try
        {
            RimTooling.Windows.Junction.Create(
                CommandContext.FullPath(junctionPath),
                CommandContext.FullPath(targetPath),
                OnSharingViolation);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or RimTooling.Windows.JunctionException)
        {
            throw new StoreException(exception.Message, exception);
        }
    }

    public static bool Remove(string path)
    {
        try
        {
            return RimTooling.Windows.Junction.Remove(CommandContext.FullPath(path));
        }
        catch (RimTooling.Windows.JunctionException exception)
        {
            throw new StoreException(exception.Message, exception);
        }
    }

    public static RimTooling.Windows.ReparseLinkInfo GetLinkInfo(string path)
    {
        try
        {
            return RimTooling.Windows.Junction.GetLinkInfo(CommandContext.FullPath(path), OnSharingViolation);
        }
        catch (RimTooling.Windows.JunctionException exception)
        {
            throw new StoreException(exception.Message, exception);
        }
    }

    private static void OnSharingViolation(string path, int _) =>
        ObservationSignatures.Mark(RimRunner.RimRunnerObservationKeys.SharingViolation, "共享冲突，无法打开 " + path);
}
