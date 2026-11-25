using RimTooling.Processes;

namespace RimRunnerCli;

internal static class GameLauncher
{
    public static LaunchedProcess Start(string gameExe, string workingDirectory,
        IReadOnlyList<string> arguments, string logPath)
    {
        try
        {
            return RimTooling.RimWorld.GameLauncher.Start(new RimTooling.RimWorld.GameLaunchOptions
            {
                ExecutablePath = CommandContext.FullPath(gameExe),
                WorkingDirectory = CommandContext.FullPath(workingDirectory),
                Arguments = arguments,
                LogFilePath = CommandContext.FullPath(logPath),
                BaseEnvironment = CommandContext.EnvironmentSnapshot(),
                FailIfExitedImmediately = true,
            });
        }
        catch (ProcessLaunchException exception)
        {
#pragma warning disable CS8524
            throw exception.FailureKind switch
            {
                ProcessLaunchFailureKind.LogFileCreateFailed
                    => new StoreException(exception.Message, exception),
                ProcessLaunchFailureKind.ExecutableNotFound
                    or ProcessLaunchFailureKind.WorkingDirectoryNotFound
                    or ProcessLaunchFailureKind.StartFailed
                    or ProcessLaunchFailureKind.ExitedImmediately
                    => new GameUnavailableException(exception.Message, exception),
            };
#pragma warning restore CS8524
        }
    }

    internal static ProcessIdentity.Stamp StartDetached(string exe, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        try
        {
            ProcessStamp stamp = ProcessLauncher.StartDetached(new DetachedProcessOptions
            {
                ExecutablePath = CommandContext.FullPath(exe),
                WorkingDirectory = CommandContext.FullPath(workingDirectory),
                Arguments = arguments,
                BaseEnvironment = CommandContext.EnvironmentSnapshot(),
                EnvironmentOverrides = environment,
            });
            return new ProcessIdentity.Stamp(stamp.Pid, stamp.StartedAt);
        }
        catch (ProcessLaunchException exception)
        {
            throw new StoreException(exception.Message, exception);
        }
    }
}
