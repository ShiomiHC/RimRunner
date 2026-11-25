namespace RimRunner;

/// <summary>出图的界面档：画到哪一层，逐档多画一些。</summary>
public enum RimRunnerUiLevel
{
    /// <summary>只留场面：基于原版截图模式，额外隐藏名牌血条、区域、指派标记与状态图标。</summary>
    /// <remarks>仍画出的：非 GUI 的 Mote（浮动文字、引线火花等），以及不认原版截图模式的外源界面。</remarks>
    Scene,

    /// <summary>原版截图模式原样：游戏与各 mod 自己在截图模式下藏什么就藏什么。</summary>
    /// <remarks>原版藏的：屏幕界面、选中框与选中才画的 overlay、规划、Area、工作进度条与屋顶、温度这类覆盖层。名牌血条、区域、指派标记、状态图标照画。</remarks>
    ScreenshotMode,

    /// <summary>隐藏屏幕界面：地图上画的照画，含选中框、规划、Area、工作进度条与屋顶、温度这类覆盖层。</summary>
    /// <remarks>在 OnGUI 里画、自己认截图模式的标注仍隐藏。</remarks>
    Map,

    /// <summary>不动界面：按实例里人看到的出，含检视面板、gizmo 栏与殖民者栏。</summary>
    Screen
}
