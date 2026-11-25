using Waylith.Graphics;

namespace RimRunner;

internal static class RimRunnerPresentMode
{
    private const int CheckAfterFrames = 10;
    private const string Mailbox = "MailboxKHR";

    private static int requestedAtFrame = -1;
    private static bool pendingCheck;

    internal static void Request()
    {
        if (requestedAtFrame >= 0 || !RimRunnerHiddenWindowMouse.WindowHidden)
        {
            return;
        }

        requestedAtFrame = RimRunnerFrame.Current;
        pendingCheck = true;
        Waylith.EngineWindow.SetVSyncMode(VSyncMode.TripleBuffer);
    }

    internal static void Update()
    {
        if (!pendingCheck || RimRunnerFrame.Current < requestedAtFrame + CheckAfterFrames)
        {
            return;
        }

        pendingCheck = false;
        object? context = Waylith.EngineApplication.Instance?.RenderContext;
        string? mode = context == null
            ? null
            : AccessTools.PropertyGetter(context.GetType(), "PresentMode")?.Invoke(context, null)?.ToString();
        if (mode != Mailbox)
        {
            RimRunnerEngineLog.Warn("交换链未切换为 " + Mailbox + "（回读值：" + (mode ?? "无法读取") + "）：显示器熄屏时本实例的帧率会随垂直同步降低");
        }
    }
}
