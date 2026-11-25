namespace RimRunnerCli;

public static class FileSystemSafety
{
    public static void CopyDirectory(string sourceDir, string destDir, string refusedAction)
    {
        try
        {
            RimTooling.IO.FileSystemSafety.CopyDirectorySafe(
                CommandContext.FullPath(sourceDir),
                CommandContext.FullPath(destDir),
                refusedAction);
        }
        catch (IOException exception)
        {
            throw new StoreException(exception.Message, exception);
        }
    }

    public static void AssertNoReparseInside(string rootDir, string refusedAction = "删除")
    {
        try
        {
            RimTooling.IO.FileSystemSafety.AssertNoReparseInside(CommandContext.FullPath(rootDir), refusedAction);
        }
        catch (IOException exception)
        {
            throw new StoreException(exception.Message, exception);
        }
    }

    public static void DeleteTreeJunctionSafe(string rootDir) =>
        RimTooling.IO.FileSystemSafety.DeleteTreeJunctionSafe(CommandContext.FullPath(rootDir));
}
