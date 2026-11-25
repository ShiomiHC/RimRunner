namespace RimRunner;

/// <summary>自动实例与分片实例的窗口尺寸由 -WindowSize 建窗即锁，帧缓冲与屏幕尺寸等于请求值。</summary>
/// <remarks>只验本实例命令行 -WindowSize 的「宽,高」解析与宿主锁定标志，及 EngineWindow 帧缓冲与 Screen 的宽高。</remarks>
public class Case_WindowSizeLock : RimRunnerRegression
{
    private const string Source = "引擎承诺：自动实例与分片实例的窗口尺寸恒为编排描述符的 windowSize（未指定时为 1920×1080），启动即锁定；"
                                  + "不成立时截图尺寸与界面元素的像素位置随机器与 Prefs 改变，按像素写的断言与截图基线在另一台机器上即不匹配";

    public override string Id => "WINSIZE1";
    public override RimRunnerLevel Level => RimRunnerLevel.Assembly;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        bool passed = GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.HostWindowSize, out string? raw);
        bool parsed = RimRunnerWindowSize.TryParseHostSize(raw, out int width, out int height);
        ctx.AssertTrue("WINSIZE1-Locked", parsed && Waylith.EngineWindow.SizeLocked,
            !passed ? "实例启动时未带 -" + RimRunnerEngineArgs.HostWindowSize
            : !parsed ? "-" + RimRunnerEngineArgs.HostWindowSize + "=" + raw + " 不是「宽,高」，宿主按未传入处理"
            : "带 -" + RimRunnerEngineArgs.HostWindowSize + "=" + raw + "，窗口尺寸未锁定",
            RimRunnerExpectationSource.Literal(Source));
        if (!parsed)
        {
            return;
        }

        int framebufferWidth = Waylith.EngineWindow.Width;
        int framebufferHeight = Waylith.EngineWindow.Height;
        ctx.AssertTrue("WINSIZE1-Size",
            framebufferWidth == width && framebufferHeight == height && Screen.width == width && Screen.height == height,
            "请求 " + width + "×" + height + "，帧缓冲 " + framebufferWidth + "×" + framebufferHeight
            + "，界面排版用的屏幕尺寸 " + Screen.width + "×" + Screen.height,
            RimRunnerExpectationSource.Literal(Source));
    }
}
