namespace RimRunner;

public sealed class RimRunnerWindowTitleMod : Mod
{
    public RimRunnerWindowTitleMod(ModContentPack content) : base(content)
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable)
            || !GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.WindowTitle, out string title)
            || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        LongEventHandler.ExecuteWhenFinished(() => Waylith.EngineApplication.Instance.Window.Title = title);
    }
}
