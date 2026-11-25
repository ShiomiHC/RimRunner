namespace RimRunner;

internal static class RimRunnerWindowSize
{
    internal static void RequestPhysical(int width, int height)
    {
        Waylith.Windowing.IWindow window = Waylith.EngineApplication.Instance.Window;
        window.MaxSize = new Waylith.Windowing.LogicalSize(width / window.ScaleFactor, height / window.ScaleFactor);
        window.RequestSize(Waylith.Windowing.WindowSize.Physical(width, height));
    }

    internal static bool TryParseHostSize(string? raw, out int width, out int height)
    {
        width = 0;
        height = 0;
        string[]? parts = raw?.Split(',');
        return parts != null && parts.Length == 2
               && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height);
    }
}

public sealed class RimRunnerLockedWindowSizeMod : Mod
{
    public RimRunnerLockedWindowSizeMod(ModContentPack content) : base(content)
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable)
            || !GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.HostWindowSize, out string? raw)
            || !RimRunnerWindowSize.TryParseHostSize(raw, out int width, out int height))
        {
            return;
        }

        LongEventHandler.ExecuteWhenFinished(() =>
        {
            if (Waylith.EngineWindow.SizeLocked
                && (Waylith.EngineWindow.Width != width || Waylith.EngineWindow.Height != height))
            {
                RimRunnerWindowSize.RequestPhysical(width, height);
            }
        });
    }
}
