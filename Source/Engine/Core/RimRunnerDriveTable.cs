namespace RimRunner;

public static class RimRunnerDriveTable
{
    public sealed record Param(string Name, string Placeholder, bool Required, string Meaning);

    public sealed record Verb(string Name, string Summary, IReadOnlyList<Param> Params,
        Func<IReadOnlyDictionary<string, string>, string?>? ArgError = null,
        IReadOnlyList<string>? OneOf = null);

    public const int MaxStepTicksPerFrame = 5000;

    public const int TicksPerHour = 2500;
    public const int TicksPerDay = 60000;

    public const string UntilLetter = "letter";
    public const string UntilLetterTitle = "开始之后出现新信";

    public const string SpeedPaused = "paused";
    public const string SpeedNormal = "normal";
    public const string SpeedFast = "fast";
    public const string SpeedSuperfast = "superfast";
    public const string SpeedMax = "max";
    public static readonly IReadOnlyList<string> PlaySpeeds = [SpeedNormal, SpeedFast, SpeedSuperfast, SpeedMax];
    public static readonly IReadOnlyList<string> SetSpeeds = [SpeedPaused, .. PlaySpeeds];

    public const string Flags = "true|false";

    public const string PawnShape = "<昵称或 ThingID>";

    public const string CellShape = "<x,z>";

    public const string TileShape = "<地块号>";

    public const string ScreenPointShape = "<x,y>";

    private const string ScreenPointMeaning =
        "屏幕点：整屏 live screenshot 上的像素坐标，原点左上，与回执里的屏幕点同一坐标系；--rect 截的局部图上量出的坐标不能直接用。"
        + "点在窗口上时由该窗口接收，遮挡不影响接收；底栏、按钮栏不是窗口，点在上面时回执写其下的地图格或世界视图";

    private const string ScreenPointSeeClick = "屏幕点，坐标与 click 的 --at 相同";

    public static readonly Verb Camera = new("camera",
        "把相机对准当前地图的一格、一片格矩形或一个 pawn 所在格，或世界视图当前层的一个地块，相机落定后回报画面；不改选中",
        [
            new Param("target", "<x,z>|<x1,z1,x2,z2>", false, "一格，或两个对角格围成的矩形"),
            new Param("pawn", PawnShape, false, "对准它此刻所在的格"),
            new Param("tile", TileShape, false, "世界视图已打开时对准当前层的该地块，同原版跳到地块；不接受 zoom"),
            new Param("zoom", "<视距>", false,
                "屏幕高度覆盖的格数之半；缺省时一格保持当前视距，矩形按框住整片加四周 1 格取"),
        ],
        CameraArgError, ["target", "pawn", "tile"]);

    public static readonly Verb Sweep = new("sweep",
        "按视距把当前地图的一片格矩形（缺省全图）切成一屏一块，蛇形逐块对准，每块停若干帧",
        [
            new Param("area", "<x1,z1,x2,z2>", false, "两个对角格围成的矩形；缺省全图"),
            new Param("zoom", "<视距>", false, "屏幕高度覆盖的格数之半；缺省保持当前视距"),
            new Param("dwell-frames", "<N>", false, "每块停的帧数，缺省 1"),
        ],
        SweepArgError);

    public static readonly Verb Spawn = new("spawn",
        "在当前地图上铺出要测的局面：按 Def 名或整类放置若干物，回报成功放置的物与无法放置的物",
        [
            new Param("selector", "<Def名>|category:<类别>", true, "一个 ThingDef 的 defName，或 category: 后接 ThingCategory 枚举名"),
            new Param("area", "<x1,z1,x2,z2>", false, "落点搜索的格矩形；缺省全图"),
            new Param("count", "<N>", false, "物件的总个数；物品按件数分堆，缺省 1"),
            new Param("stuff", "<材料Def名>", false, "材料；只接受精确 Def 名，selector 为 category:<类别> 时不能传入"),
        ],
        SpawnArgError);

    public static readonly Verb Jump = new("jump",
        "把游戏时钟向前推进一段，不推进中间的 tick（同原版 dev 菜单的加时）",
        [new Param("duration", "<N>t|<N>h|<N>d", true, "游戏时长：t 为 tick，h 为游戏小时（2500 tick），d 为游戏日（60000 tick）")],
        JumpArgError);

    public static readonly Verb Step = new("step",
        "在暂停状态下连推 tick，每帧至多 " + MaxStepTicksPerFrame + "，推够即停，之后回到原来的速度",
        [
            new Param("ticks", "<N>", true, "推进的 tick 数"),
            new Param("per-frame", "true|false", false, "true 即每帧只推一 tick，缺省 false"),
        ],
        StepArgError);

    public static readonly Verb Speed = new("speed",
        "设游戏速度并回读；max 打开最高档加速，原版不再自动降回常速",
        [new Param("speed", string.Join("|", SetSpeeds), true, "目标速度")],
        SpeedArgError);

    public static readonly Verb Play = new("play",
        "按原版速度逐帧运行一段墙钟时长，达到时长即暂停",
        [
            new Param("duration", "<N>s|<N>m", true, "墙钟时长：s 为秒，m 为分钟"),
            new Param("speed", string.Join("|", PlaySpeeds), false, "运行速度，缺省 normal"),
        ],
        PlayArgError);

    public static readonly Verb Select = new("select",
        "选中当前地图上的 pawn，同玩家左键选中；缺省先清除原先的选中",
        [
            new Param("pawn", PawnShape + "[,…]", true, "要选中的 pawn，逗号分隔"),
            new Param("append", Flags, false, "true 即加进原先的选中，缺省 false"),
        ],
        SelectArgError);

    public static readonly Verb Deselect = new("deselect",
        "取消选中；未指定 pawn 时清除全部选中",
        [new Param("pawn", PawnShape + "[,…]", false, "要取消选中的 pawn，逗号分隔，须处于选中状态")],
        DeselectArgError);

    public static readonly Verb Draft = new("draft",
        "征召或解除征召一个玩家派系的 pawn，设置后回读",
        [
            new Param("pawn", PawnShape, true, "要征召或解除征召的 pawn"),
            new Param("on", Flags, true, "true 征召，false 解除"),
        ],
        DraftArgError);

    public static readonly Verb Gizmo = new("gizmo",
        "点当前选中那一栏上的一个按钮，同原版按钮栏的一次左键；按钮栏用 live gizmos 看",
        [new Param("gizmo", "<序号>|<按钮文字>", true, "live gizmos 列的序号，或按钮上的完整文字")]);

    public static readonly Verb Dev = new("dev",
        "执行原版 dev 菜单里的一项，与在菜单里点它同一个入口；菜单用 live dev 看",
        [
            new Param("path", @"<路径，以 \ 分隔>", true, @"live dev 列的路径，如 Settings\God mode"),
            new Param("pawn", PawnShape, false, "对 pawn 的地图工具必填，对这个 pawn 执行"),
            new Param("on", Flags, false, "设置项必填：目标值；已是该值时不操作"),
            new Param("cell", CellShape, false, "地图工具必填：用鼠标点这一格"),
            new Param("tile", TileShape, false, "世界工具必填：用鼠标点世界视图当前层的这个地块，点之前核对鼠标下正是该地块"),
        ],
        DevArgError);

    public static readonly Verb DevExpand = new("dev-expand",
        "展开原版 dev 菜单里的一个生成子菜单：可能消耗随机流、改动状态",
        [new Param("path", @"<路径，以 \ 分隔>", true, "live dev 标为未展开的那个路径")]);

    public static readonly Verb Click = new("click",
        "鼠标移到一格、一个 pawn 或一个屏幕点上左键单击，游戏按真实点击处理；格与 pawn 须在画面内、未被窗口遮挡",
        [
            new Param("cell", CellShape, false, "点这一格的中心"),
            new Param("pawn", PawnShape, false, "点它画出的位置"),
            new Param("at", ScreenPointShape, false, ScreenPointMeaning),
        ],
        PointArgError, ["cell", "pawn", "at"]);

    public static readonly Verb RightClick = new("rclick",
        "鼠标移到一格、一个 pawn 或一个屏幕点上右键单击；选中 pawn 时同玩家右键下指令，弹出的菜单用 live menu 看；格与 pawn 须在画面内、未被窗口遮挡",
        [
            new Param("cell", CellShape, false, "点这一格的中心"),
            new Param("pawn", PawnShape, false, "点它画出的位置"),
            new Param("at", ScreenPointShape, false, ScreenPointSeeClick),
        ],
        PointArgError, ["cell", "pawn", "at"]);

    public static readonly Verb Drag = new("drag",
        "鼠标左键按住从一格拖到另一格再松开，同玩家框选；两格须在画面内、未被窗口遮挡",
        [
            new Param("from", CellShape, true, "按下的格"),
            new Param("to", CellShape, true, "松开的格"),
        ],
        DragArgError);

    public static readonly Verb Hover = new("hover",
        "鼠标移到一格、一个 pawn 或一个屏幕点上悬停，不按键；格与 pawn 须在画面内、未被窗口遮挡；--off=true 撤销驱动设置的鼠标位置",
        [
            new Param("cell", CellShape, false, "悬停在这一格的中心"),
            new Param("pawn", PawnShape, false, "悬停在它画出的位置"),
            new Param("at", ScreenPointShape, false, ScreenPointSeeClick),
            new Param("off", "true", false, "撤销驱动设置的鼠标位置，窗口隐藏的实例回到画面外"),
        ],
        HoverArgError, ["cell", "pawn", "at", "off"]);

    public static readonly Verb MenuPick = new("menu-pick",
        "鼠标点最上层右键菜单里的一项，同玩家点选项；菜单用 live menu 看",
        [new Param("option", "<序号>|<选项文字>", true, "live menu 列的序号，或选项的完整文字")]);

    public static readonly Verb MenuClose = new("menu-close",
        "关闭最上层右键菜单，同按 Esc 关窗的入口，不执行任何选项",
        []);

    public static readonly Verb Order = new("order",
        "选中一个 pawn，鼠标右键点一格，弹出菜单时再点 --option 指定的项，未指定时保留菜单：同玩家右键下指令",
        [
            new Param("pawn", PawnShape, true, "下指令的 pawn"),
            new Param("cell", CellShape, true, "右键点的格"),
            new Param("option", "<序号>|<选项文字>", false, "弹出菜单时点该项；未指定时保留菜单"),
        ],
        OrderArgError);

    public static readonly Verb Key = new("key",
        "按下并松开一个键，游戏按真实按键处理；Esc 关窗、回车确认、空格暂停都经这个原语",
        [
            new Param("key", "<KeyCode 名>", true, "Unity KeyCode 的枚举名，如 Escape、Return、Space、F1、A、Alpha1"),
            new Param("shift", Flags, false, "true 即按住左 Shift"),
            new Param("ctrl", Flags, false, "true 即按住左 Ctrl"),
            new Param("alt", Flags, false, "true 即按住左 Alt"),
        ],
        KeyArgError);

    public static readonly Verb WindowClose = new("window-close",
        "关闭窗口栈里的一个窗口，同点它的关闭按钮；窗口用 live windows 看",
        [new Param("window", "<序号>|<窗口类型名>", true, "live windows 列的序号，或窗口的类型名")]);

    public static readonly Verb Tab = new("tab",
        "鼠标点底栏的一个主标签页按钮，同玩家点它：未打开时打开，已打开时关闭",
        [new Param("name", "<defName>|<按钮文字>", true, "MainButtonDef 的 defName，或按钮上的文字")]);

    public sealed record VerbGroup(string Title, IReadOnlyList<Verb> Verbs);

    public static readonly IReadOnlyList<VerbGroup> Groups =
    [
        new("相机", [Camera, Sweep]),
        new("局面", [Spawn]),
        new("时间与速度", [Jump, Step, Speed, Play]),
        new("选中、征召与按钮栏", [Select, Deselect, Draft, Gizmo]),
        new("dev 菜单", [Dev, DevExpand]),
        new("鼠标", [Click, RightClick, Drag, Hover, Order, Tab]),
        new("右键菜单、窗口与按键", [MenuPick, MenuClose, WindowClose, Key]),
    ];

    public static readonly IReadOnlyList<Verb> All = [.. Groups.SelectMany(group => group.Verbs)];

    public static Verb? Find(string name) => All.FirstOrDefault(verb => verb.Name == name);

    public static readonly Verb Until = new("until",
        "判条件成立与否：--within=0 只判此刻；游戏时长在暂停状态下连推，每 tick 判一次；墙钟时长按原版速度运行并等待；成立即停",
        [
            new Param("within", "0|<N>t|<N>h|<N>d|<N>s|<N>m", true,
                "0 只判此刻；t / h / d 是游戏时长，在暂停状态下连推；s / m 是墙钟时长，按原版速度运行"),
            new Param("speed", string.Join("|", PlaySpeeds), false, "墙钟时长时的运行速度，缺省 normal"),
        ],
        UntilArgError);

    public static int? ParseGameTicks(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length < 2)
        {
            return null;
        }

        char unit = trimmed[^1];
        string number = trimmed[..^1];
        if (unit == 't')
        {
            return ParsePositiveInt(number);
        }

        int scale = unit switch { 'h' => TicksPerHour, 'd' => TicksPerDay, _ => 0 };
        if (scale == 0 || ParsePositive(number) is not { } value || value * scale > int.MaxValue / 2)
        {
            return null;
        }

        int ticks = (int)Math.Round(value * scale);
        return ticks > 0 ? ticks : null;
    }

    public static float? ParseWallSeconds(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length < 2)
        {
            return null;
        }

        float scale = trimmed[^1] switch { 's' => 1f, 'm' => 60f, _ => 0f };
        return scale > 0f && ParsePositive(trimmed[..^1]) is { } value ? value * scale : null;
    }

    public static int[]? ParseInts(string text, params int[] counts)
    {
        string[] parts = text.Split(',');
        if (!counts.Contains(parts.Length))
        {
            return null;
        }

        int[] values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        return values;
    }

    public static float? ParsePositive(string text) =>
        float.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value) && value > 0f && float.IsFinite(value)
            ? value
            : null;

    private static string? CameraArgError(IReadOnlyDictionary<string, string> args)
    {
        if (new[] { "target", "pawn", "tile" }.Count(args.ContainsKey) != 1)
        {
            return "target、pawn 与 tile 须恰好指定一个";
        }

        if (args.ContainsKey("tile"))
        {
            return args.ContainsKey("zoom") ? "tile 不接受 zoom" : TileError(args);
        }

        if (args.TryGetValue("target", out string? target) && ParseInts(target, 2, 4) == null)
        {
            return "target 写作 x,z 或 x1,z1,x2,z2（整数格坐标），收到：" + target;
        }

        return args.TryGetValue("zoom", out string? zoom) && ParsePositive(zoom) == null
            ? "zoom 须为正数，收到：" + zoom
            : null;
    }

    public static string[]? ParsePawnList(string text)
    {
        string[] names = [.. text.Split(',').Select(name => name.Trim())];
        return names.Any(name => name.Length == 0) ? null : names;
    }

    public static bool? ParseFlag(string text) => text switch { "true" => true, "false" => false, _ => null };

    private static string? PawnListError(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("pawn", out string? pawns))
        {
            return null;
        }

        if (ParsePawnList(pawns) is not { } names)
        {
            return "pawn 写作逗号分隔的昵称或 ThingID，不得有空项，收到：" + pawns;
        }

        return names.GroupBy(name => name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice
            ? "pawn " + twice.Key + " 指定不止一次"
            : null;
    }

    private static string? FlagError(IReadOnlyDictionary<string, string> args, string name) =>
        args.TryGetValue(name, out string? value) && ParseFlag(value) == null ? name + " 只接受 true 或 false，收到：" + value : null;

    private static string? DevArgError(IReadOnlyDictionary<string, string> args) =>
        args["path"].Length == 0 ? "path 不得为空"
        : args.ContainsKey("cell") && args.ContainsKey("tile") ? "cell 与 tile 不能同时传入"
        : FlagError(args, "on") ?? CellError(args, "cell") ?? TileError(args);

    public static int? ParseTile(string text) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    private static string? TileError(IReadOnlyDictionary<string, string> args) =>
        args.TryGetValue("tile", out string? value) && ParseTile(value) == null ? "tile 写作非负整数（地块号），收到：" + value : null;

    private static string? CellError(IReadOnlyDictionary<string, string> args, string name) =>
        args.TryGetValue(name, out string? value) && ParseInts(value, 2) == null ? name + " 写作 x,z（整数格坐标），收到：" + value : null;

    private static string? PointArgError(IReadOnlyDictionary<string, string> args) =>
        new[] { "cell", "pawn", "at" }.Count(args.ContainsKey) != 1 ? "cell、pawn 与 at 须恰好指定一个"
        : CellError(args, "cell") ?? ScreenPointError(args);

    public static int[]? ParseScreenPoint(string text) =>
        ParseInts(text, 2) is { } v && v[0] >= 0 && v[1] >= 0 ? v : null;

    private static string? ScreenPointError(IReadOnlyDictionary<string, string> args) =>
        args.TryGetValue("at", out string? value) && ParseScreenPoint(value) == null
            ? "at 写作 x,y（非负整数像素，原点左上），收到：" + value
            : null;

    private static string? DragArgError(IReadOnlyDictionary<string, string> args) =>
        CellError(args, "from") ?? CellError(args, "to");

    private static string? HoverArgError(IReadOnlyDictionary<string, string> args)
    {
        if (args.TryGetValue("off", out string? off) && off != "true")
        {
            return "off 只接受 true，收到：" + off;
        }

        return new[] { "cell", "pawn", "at", "off" }.Count(args.ContainsKey) != 1 ? "cell、pawn、at 与 off 须恰好指定一个"
            : CellError(args, "cell") ?? ScreenPointError(args);
    }

    private static string? KeyArgError(IReadOnlyDictionary<string, string> args) =>
        args["key"].Trim().Length == 0 ? "key 不得为空" : FlagError(args, "shift") ?? FlagError(args, "ctrl") ?? FlagError(args, "alt");

    private static string? OrderArgError(IReadOnlyDictionary<string, string> args) =>
        args["pawn"].Trim().Length == 0 ? "pawn 不得为空" : CellError(args, "cell");

    private static string? SelectArgError(IReadOnlyDictionary<string, string> args) => PawnListError(args) ?? FlagError(args, "append");

    private static string? DeselectArgError(IReadOnlyDictionary<string, string> args) => PawnListError(args);

    private static string? DraftArgError(IReadOnlyDictionary<string, string> args) =>
        args["pawn"].Trim().Length == 0 ? "pawn 不得为空" : FlagError(args, "on");

    public static int? ParsePositiveInt(string text) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : null;

    private static string? SweepArgError(IReadOnlyDictionary<string, string> args)
    {
        if (args.TryGetValue("area", out string? area) && ParseInts(area, 4) == null)
        {
            return "area 写作 x1,z1,x2,z2（整数格坐标），收到：" + area;
        }

        if (args.TryGetValue("zoom", out string? zoom) && ParsePositive(zoom) == null)
        {
            return "zoom 须为正数，收到：" + zoom;
        }

        return args.TryGetValue("dwell-frames", out string? dwell) && ParsePositiveInt(dwell) == null
            ? "dwell-frames 须为正整数，收到：" + dwell
            : null;
    }

    private static string? SpawnArgError(IReadOnlyDictionary<string, string> args)
    {
        if (args.TryGetValue("selector", out string? selector) && string.IsNullOrWhiteSpace(selector))
        {
            return "selector 须是 Def 名或 category:<类别>";
        }

        if (args.TryGetValue("area", out string? area) && ParseInts(area, 4) == null)
        {
            return "area 写作 x1,z1,x2,z2（整数格坐标），收到：" + area;
        }

        if (args.TryGetValue("count", out string? count) && ParsePositiveInt(count) == null)
        {
            return "count 须为正整数，收到：" + count;
        }

        if (selector != null && selector.StartsWith("category:", StringComparison.OrdinalIgnoreCase))
        {
            if (selector["category:".Length..].Trim().Length == 0)
            {
                return "category: 后须指定 ThingCategory 枚举名";
            }

            if (args.ContainsKey("stuff"))
            {
                return "selector 为 category:<类别> 时不能传入 stuff";
            }
        }

        return null;
    }

    private static string? JumpArgError(IReadOnlyDictionary<string, string> args) =>
        args.TryGetValue("duration", out string? duration) && ParseGameTicks(duration) == null
            ? "duration 写作 <N>t、<N>h 或 <N>d，须为正，收到：" + duration
            : null;

    private static string? StepArgError(IReadOnlyDictionary<string, string> args)
    {
        if (args.TryGetValue("ticks", out string? ticks) && ParsePositiveInt(ticks) == null)
        {
            return "ticks 须为正整数，收到：" + ticks;
        }

        if (args.TryGetValue("per-frame", out string? perFrame) && perFrame is not ("true" or "false"))
        {
            return "per-frame 写 true 或 false，收到：" + perFrame;
        }

        return null;
    }

    private static string? SpeedArgError(IReadOnlyDictionary<string, string> args) =>
        args.TryGetValue("speed", out string? speed) && !SetSpeeds.Contains(speed)
            ? "speed 可用：" + string.Join("、", SetSpeeds) + "，收到：" + speed
            : null;

    private static string? PlayArgError(IReadOnlyDictionary<string, string> args)
    {
        if (args.TryGetValue("duration", out string? duration) && ParseWallSeconds(duration) == null)
        {
            return "duration 写作 <N>s 或 <N>m，须为正，收到：" + duration;
        }

        if (args.TryGetValue("speed", out string? speed) && !PlaySpeeds.Contains(speed))
        {
            return "speed 可用：" + string.Join("、", PlaySpeeds) + "，收到：" + speed;
        }

        return null;
    }

    private static string? UntilArgError(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("within", out string? within))
        {
            return null;
        }

        bool wall = within.Trim() != "0" && ParseWallSeconds(within) != null;
        if (within.Trim() != "0" && !wall && ParseGameTicks(within) == null)
        {
            return "within 写作 0、<N>t、<N>h、<N>d、<N>s 或 <N>m，须为正，收到：" + within;
        }

        if (args.TryGetValue("speed", out string? speed))
        {
            if (!wall)
            {
                return "speed 只接受墙钟时长（<N>s 或 <N>m）";
            }

            if (!PlaySpeeds.Contains(speed))
            {
                return "speed 可用：" + string.Join("、", PlaySpeeds) + "，收到：" + speed;
            }
        }

        return null;
    }
}
