namespace RimRunner;

public sealed class RimRunnerOccludedRedrawMod : Mod
{
    public RimRunnerOccludedRedrawMod(ModContentPack content) : base(content)
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            return;
        }

        LongEventHandler.ExecuteWhenFinished(() => Waylith.EngineApplication.Instance.OnUpdate += RequestRedrawIfOccluded);
    }

    private static void RequestRedrawIfOccluded()
    {
        Waylith.Windowing.IWindow window = Waylith.EngineApplication.Instance.Window;
        if (!window.ShouldPresent)
        {
            window.RequestRedraw();
        }
    }
}
