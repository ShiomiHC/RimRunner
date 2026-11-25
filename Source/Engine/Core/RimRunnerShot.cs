namespace RimRunner;

/// <summary>场面的一个镜头：一张静帧或一段片段。</summary>
/// <remarks>由 rimrunner film 拍，产出 PNG、GIF 或 MP4。每个镜头从刚载入的基线档出发，开拍前钉 Rand 种子。</remarks>
public sealed class RimRunnerShot
{
    /// <summary>镜头名，也是产出文件名。</summary>
    /// <remarks>同一场面内唯一，只用文件名允许的字符。</remarks>
    public string Name { get; }

    /// <summary>开拍前执行到的步骤名；null 即立场之后直接开拍。</summary>
    /// <remarks>按步骤树语义执行：前驱按序补执行；这一段不出帧。</remarks>
    public string? Setup { get; }

    /// <summary>机位与界面档。</summary>
    /// <remarks>静帧恒是静止机位；片段可以是摇镜。</remarks>
    public RimRunnerClipCamera Camera { get; }

    /// <summary>输出尺寸与体积上限。</summary>
    public RimRunnerShotOutput Output { get; }

    /// <summary>片段的产出格式；静帧恒出 PNG，此项为 0。</summary>
    public RimRunnerClipFormat ClipFormats { get; }

    /// <summary>镜头种子：第一帧前钉 Rand，定片段里发生的事。</summary>
    /// <remarks>null 取场面 Id 与镜头名的稳定哈希；立场与装配由场面的场面种子决定。</remarks>
    public int? Seed { get; }

    /// <summary>片段：逐帧推进。</summary>
    public bool IsClip { get; }

    /// <summary>片段拍到哪一步为止；null 即立场之后只有前导与收尾。</summary>
    /// <remarks>拍的是从 Setup 之后到这一步为止的整条路径，前驱按序补执行。</remarks>
    public string? Through { get; }

    /// <summary>片段帧率；静帧为 0。</summary>
    public int Fps { get; }

    /// <summary>片段的缺省倍速：每秒推进该倍速 × 60 tick。</summary>
    /// <remarks>0 即世界保持不动，只有机位移动。</remarks>
    public int Speed { get; }

    /// <summary>开录后的前导时长，秒；Setup 之后的步骤在前导结束后开始执行。</summary>
    public float LeadSeconds { get; }

    /// <summary>路径走完后的收尾时长，秒。</summary>
    public float TailSeconds { get; }

    /// <summary>片段从第一帧起从头播的曲目，SongDef 的 defName；null 即不放音乐。</summary>
    /// <remarks>只进 Mp4 的音轨。镜头里不切歌、不淡出，曲子比片段短则播完即止；没有这个 defName 的曲目时拒拍。</remarks>
    public string? Music { get; }

    internal bool IsPath { get; }

    private RimRunnerShot(string name, string? setup, RimRunnerClipCamera? camera, RimRunnerShotOutput output,
        RimRunnerClipFormat clipFormats, int? seed, bool isClip, string? through, int fps, int speed,
        float leadSeconds, float tailSeconds, bool isPath, string? music = null)
    {
        Name = name;
        Setup = setup;
        Camera = camera ?? RimRunnerShotCamera.Focus();
        Output = output;
        ClipFormats = clipFormats;
        Seed = seed;
        IsClip = isClip;
        Through = through;
        Fps = fps;
        Speed = speed;
        LeadSeconds = leadSeconds;
        TailSeconds = tailSeconds;
        IsPath = isPath;
        Music = music;
    }

    /// <summary>创建静帧镜头：Setup 之后在暂停态出一张 PNG。</summary>
    /// <remarks>camera 取静止机位，缺省聚焦 Assemble 返回的对象。</remarks>
    public static RimRunnerShot Still(string name, RimRunnerShotOutput output, RimRunnerShotCamera? camera = null,
        string? setup = null, int? seed = null)
    {
        return new RimRunnerShot(name, setup, camera, output, 0, seed, isClip: false, through: null, fps: 0, speed: 0,
            0f, 0f, isPath: false);
    }

    /// <summary>创建片段镜头：从 Setup 之后拍到 through 为止。</summary>
    /// <remarks>每秒 fps 帧，按 speed × 60 tick 推进；步骤写 filmSpeed 时那一步用它。speed × 60 须被 fps 整除；formats 取 Gif / Mp4 的组合；camera 可取 PanTo 创建的摇镜。</remarks>
    public static RimRunnerShot Clip(string name, RimRunnerShotOutput output, RimRunnerClipFormat formats,
        string? through, int fps = 30, int speed = 1, RimRunnerClipCamera? camera = null, string? setup = null,
        int? seed = null, float leadSeconds = 1f, float tailSeconds = 0f, string? music = null)
    {
        return new RimRunnerShot(name, setup, camera, output, formats, seed, isClip: true, through, fps, speed,
            leadSeconds, tailSeconds, isPath: false, music);
    }

    internal const float PathsLeadSeconds = 1f;

    internal static bool PathsFpsValid(int fps) => fps > 0 && GenTicks.TicksPerRealSecond % fps == 0;

    internal static RimRunnerShot Paths(RimRunnerScene scene, string? leaf, int fps)
    {
        int stepCount = leaf == null ? 0 : RimRunnerSceneStepTree.PathTo(scene.Steps, leaf).Count;
        string suffix = leaf ?? "";
        return new RimRunnerShot("paths/" + suffix, setup: null,
            RimRunnerShotCamera.Focus().WithUi(RimRunnerUiLevel.Screen),
            new RimRunnerShotOutput(),
            stepCount == 0 ? 0 : RimRunnerClipFormat.Mp4,
            RimRunnerSceneSeed.StableHash(scene.Id + "/paths/" + suffix),
            isClip: true, through: leaf, fps: fps, speed: 1,
            leadSeconds: PathsLeadSeconds, tailSeconds: 0f, isPath: true);
    }

    internal int LeadFrameCount => Mathf.RoundToInt(LeadSeconds * Fps);

    internal int TailFrameCount => Mathf.RoundToInt(TailSeconds * Fps);

    internal int SpeedForStep(RimRunnerSceneStep step)
    {
        return step.FilmSpeed > 0 ? step.FilmSpeed : Speed;
    }

    internal int TicksPerFrame(int multiplier)
    {
        return Fps > 0 ? multiplier * GenTicks.TicksPerRealSecond / Fps : 0;
    }

    internal int ResolveSeed(string showcaseId)
    {
        return Seed ?? RimRunnerSceneSeed.StableHash(showcaseId + "/" + Name);
    }
}

/// <summary>片段镜头的机位：静止机位或摇镜。</summary>
public abstract class RimRunnerClipCamera
{
    private protected RimRunnerClipCamera()
    {
    }

    /// <summary>界面档，缺省 Scene。</summary>
    public RimRunnerUiLevel Ui { get; private protected set; }

    internal abstract RimRunnerShotCamera Start { get; }

    internal abstract RimRunnerShotCamera? End { get; }
}

/// <summary>镜头机位：对准哪里、视距多大、出哪一档界面。</summary>
/// <remarks>Focus 与 On 逐帧跟随对象当前位置。PanTo 从第一帧起在给定秒数内线性移到终点，之后停在终点。</remarks>
public sealed class RimRunnerShotCamera : RimRunnerClipCamera
{
    internal enum TargetKind
    {
        Focus,
        Key,
        Cell
    }

    internal TargetKind Kind { get; private set; }
    internal string? Key { get; private set; }
    internal IntVec3 Cell { get; private set; }
    internal float Size { get; private set; }

    internal override RimRunnerShotCamera Start => this;

    internal override RimRunnerShotCamera? End => null;

    private RimRunnerShotCamera()
    {
    }

    /// <summary>聚焦 Assemble 返回的对象，四周留 padCells 格。</summary>
    public static RimRunnerShotCamera Focus(float padCells = 3f)
    {
        return new RimRunnerShotCamera { Kind = TargetKind.Focus, Size = padCells };
    }

    /// <summary>聚焦 ctx 按 key 登记的 pawn 或物件，四周留 padCells 格。</summary>
    /// <remarks>key 在装配里经 ctx.RegisterPawn / RegisterThing 登记。</remarks>
    public static RimRunnerShotCamera On(string key, float padCells = 3f)
    {
        return new RimRunnerShotCamera { Kind = TargetKind.Key, Key = key, Size = padCells };
    }

    /// <summary>固定对准格 (x, z)，屏幕高度覆盖 2 × halfHeightCells 格。</summary>
    public static RimRunnerShotCamera At(int x, int z, float halfHeightCells)
    {
        return new RimRunnerShotCamera { Kind = TargetKind.Cell, Cell = new IntVec3(x, 0, z), Size = halfHeightCells };
    }

    /// <summary>返回从本机位在 seconds 秒内线性移到 end 的摇镜。</summary>
    /// <remarks>只交给 Clip。界面档沿用本机位的。seconds 须为正，登记期闸判。</remarks>
    public RimRunnerShotPan PanTo(RimRunnerShotCamera end, float seconds)
    {
        return new RimRunnerShotPan(this, end ?? throw new ArgumentNullException(nameof(end)), seconds);
    }

    /// <summary>返回已换界面档的机位副本。</summary>
    public RimRunnerShotCamera WithUi(RimRunnerUiLevel ui)
    {
        RimRunnerShotCamera copy = Copy();
        copy.Ui = ui;
        return copy;
    }

    private RimRunnerShotCamera Copy()
    {
        return (RimRunnerShotCamera)MemberwiseClone();
    }
}

/// <summary>摇镜：片段开头 seconds 秒内从起点机位线性移到终点机位。</summary>
public sealed class RimRunnerShotPan : RimRunnerClipCamera
{
    private readonly RimRunnerShotCamera start;
    private readonly RimRunnerShotCamera end;

    internal RimRunnerShotPan(RimRunnerShotCamera start, RimRunnerShotCamera end, float seconds)
    {
        this.start = start;
        this.end = end;
        Seconds = seconds;
        Ui = start.Ui;
    }

    internal float Seconds { get; }

    internal override RimRunnerShotCamera Start => start;

    internal override RimRunnerShotCamera? End => end;
}

/// <summary>片段的产出格式，可按位组合。</summary>
[Flags]
public enum RimRunnerClipFormat
{
    /// <summary>GIF 动图。</summary>
    Gif = 1,

    /// <summary>MP4（H.264 yuv420p）。</summary>
    /// <remarks>宽高取偶数。</remarks>
    Mp4 = 2
}

/// <summary>镜头的输出规格：像素尺寸与可选的体积上限。</summary>
/// <remarks>尺寸即拍摄时的窗口分辨率，未指定取描述符 windowSize。不得宽 &lt; 1024 且高 &lt; 768。</remarks>
public sealed class RimRunnerShotOutput(int? width = null, int? height = null, long? maxBytes = null)
{
    /// <summary>宽，像素。未指定即 windowSize 的宽。</summary>
    public int Width => width ?? DefaultSize.Width;

    /// <summary>高，像素。未指定即 windowSize 的高。</summary>
    public int Height => height ?? DefaultSize.Height;

    /// <summary>写宽高为 true，取 windowSize 为 false。</summary>
    public bool Declared => width != null || height != null;

    internal bool Partial => (width == null) != (height == null);

    private static (int Width, int Height)? defaultSize;

    private static (int Width, int Height) DefaultSize => defaultSize ??=
        GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.WindowSize, out string? raw)
        && raw.Split(',') is [string w, string h] && int.TryParse(w, out int parsedWidth) && int.TryParse(h, out int parsedHeight)
            ? (parsedWidth, parsedHeight)
            : throw new InvalidOperationException(RimRunnerLog.Prefix + " 实例启动时未带 -" + RimRunnerEngineArgs.WindowSize
                                                  + "=宽,高（收到 " + (raw ?? "无") + "），镜头未指定输出尺寸时无法取得 windowSize");

    /// <summary>每个产出文件的体积上限，字节。null 即不设上限。</summary>
    /// <remarks>超出即镜头判红，产物保留。</remarks>
    public long? MaxBytes { get; } = maxBytes;
}
