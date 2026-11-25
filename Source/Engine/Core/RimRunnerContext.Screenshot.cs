namespace RimRunner;

internal sealed class RimRunnerScreenshotRequest
{
    internal required string Caption;
    internal required RimRunnerCropSpec Crop;
    internal required string Label;
    internal bool ClockSky;
    internal Action? Prepare;
}

/// <summary>读数用例的上下文：共用入口加读数正文、截图与样本。</summary>
public sealed partial class RimRunnerReadoutContext : RimRunnerContext
{
    internal RimRunnerReadoutContext()
    {
    }

    internal List<RimRunnerScreenshotRequest> ScreenshotRequests { get; } = [];

    internal const int MaxCaptionChars = 40;

    private static void WarnLongCaption(string what, string caption)
    {
        int chars = new System.Globalization.StringInfo(caption).LengthInTextElements;
        if (chars > MaxCaptionChars)
        {
            RimRunnerWarnings.Warn(RimRunnerWarningKey.LongCaption,
                what + " " + chars + " 字，超过 " + MaxCaptionChars + " 字：" + caption);
        }
    }

    /// <summary>截图入口：按取景登记一张图，连同说明进报告。</summary>
    /// <remarks>在 phase 返回后、Defer 清理前统一拍：同一 phase 的多张是同一时刻的不同取景。在真实帧用例里调用即抛。拍时切到目标图会重建已打开的主 tab，窗口在 prepare 里开才能进图。</remarks>
    /// <param name="caption">拍摄内容，用于辨别这一张图，随截图进报告；超过 40 字出 LONG-CAPTION 警告。</param>
    /// <param name="crop">取景，从 RimRunnerCrop.On、Cells、Gui、Full 起手。</param>
    /// <param name="prepare">拍前准备：切到目标图、设好相机与选中之后调，只负责开窗与选分页；里面推 tick 或交截图即抛，打开的窗口拍完即关。</param>
    public void AddScreenshot(string caption, RimRunnerCrop crop, Action? prepare = null)
    {
        if (RimRunnerScreenshotDriver.Preparing)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 拍前准备里调用 AddScreenshot（" + caption + "）：准备只摆界面");
        }

        if (string.IsNullOrWhiteSpace(caption))
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " AddScreenshot 须写 caption（拍的是什么）", nameof(caption));
        }

        if (crop == null)
        {
            throw new ArgumentNullException(nameof(crop), RimRunnerLog.Prefix + " AddScreenshot 须给取景");
        }

        string id = Current != null ? Current.id : "<未知>";
        RimRunnerTestCase? declared = RimRunnerRegistry.FindCase(id);
        if (declared == null || declared.Kind != RimRunnerKind.Readout)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 用例 " + id + " 不是 Readout 档，调用 AddScreenshot（" + caption.Trim()
                + "）");
        }

        if (RimRunnerSession.RealFrameActive)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 真实帧用例调用 AddScreenshot（" + caption.Trim()
                + "）：截图排在用例体返回之后出图，真实帧用例收尾即回载基线档，没有这个时机");
        }

        if (RimRunnerTextureTier.IsPlaceholder)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 本实例贴图档位是 placeholder，截图是占位像素：" + caption.Trim());
        }

        RequireOneMap(crop.Spec);
        WarnLongCaption("AddScreenshot 的说明", caption.Trim());
        var request = new RimRunnerScreenshotRequest
        {
            Caption = caption.Trim(),
            Crop = crop.Spec,
            Label = "screenshot-" + (ScreenshotRequests.Count + 1),
            ClockSky = RimRunnerDaylightPin.Suspended,
            Prepare = prepare
        };
        WarnSameShot(request);
        ScreenshotRequests.Add(request);
    }

    private static void RequireOneMap(RimRunnerCropSpec crop)
    {
        if (crop.Area != RimRunnerCropArea.Things)
        {
            return;
        }

        Thing? homeless = crop.Things.FirstOrDefault(thing => thing.MapHeld == null);
        if (homeless != null)
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " RimRunnerCrop.On 的 " + homeless.ThingID + " 不在任何地图上，无法取景", "crop");
        }

        Map map = crop.Things[0].MapHeld;
        Thing? elsewhere = crop.Things.FirstOrDefault(thing => thing.MapHeld != map);
        if (elsewhere != null)
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " RimRunnerCrop.On 的对象不在同一张地图上：" + crop.Things[0].ThingID + " 在 "
                + map + "，" + elsewhere.ThingID + " 在 " + elsewhere.MapHeld, "crop");
        }
    }

    private void WarnSameShot(RimRunnerScreenshotRequest request)
    {
        RimRunnerScreenshotRequest? same = ScreenshotRequests.FirstOrDefault(earlier => SameShot(earlier, request));
        if (same != null)
        {
            RimRunnerWarnings.Warn(RimRunnerWarningKey.ScreenshotSameShot,
                same.Label + " 与 " + request.Label + " 取景全同，同在 phase 返回时拍，是同一张图：「" + same.Caption + "」「"
                + request.Caption + "」");
        }
    }

    internal static bool SameShot(RimRunnerScreenshotRequest a, RimRunnerScreenshotRequest b) =>
        a.Prepare == null && b.Prepare == null && a.Crop.Equals(b.Crop) && a.ClockSky == b.ClockSky;
}
