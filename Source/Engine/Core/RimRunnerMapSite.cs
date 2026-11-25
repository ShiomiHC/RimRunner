namespace RimRunner;

/// <summary>用例或场面在哪张图上执行：缺省、独立小图，或主图（带理由）。</summary>
/// <remarks>独立小图是一张空的玩家殖民地图：全图一种地面、无屋顶，天气恒晴，不刷野生动植物；用例在小图上执行时主图冻结，Find.CurrentMap 仍指主图。与缺省等价的声明登记期出 INERT-DECLARATION 警告。</remarks>
public readonly struct RimRunnerMapSite
{
    internal enum Choice
    {
        Default,
        Isolated,
        Main
    }

    internal readonly Choice Kind;
    internal readonly RimRunnerMapGround Ground;
    internal readonly string? Reason;
    private readonly IntVec2 declaredSize;

    private RimRunnerMapSite(Choice kind, IntVec2 size, RimRunnerMapGround ground, string? reason)
    {
        Kind = kind;
        declaredSize = size;
        Ground = ground;
        Reason = reason;
    }

    internal IntVec2 Size => Kind == Choice.Isolated ? declaredSize : RimRunnerIsolatedMap.SizeOf(RimRunnerMapTier.Default);

    /// <summary>缺省：World 级的回归用例与读数用例，以及场面上的独立小图（缺省档、混凝土）；其余用例留主图。</summary>
    public static RimRunnerMapSite Default => default;

    /// <summary>在独立小图上执行，给定档位与地面。</summary>
    /// <remarks>用例须 Level=World。用例的小图不是当前地图，真实帧用例与场面的小图是当前地图；视野矩形取自主图相机。按相机判档的物与子弹每 15 tick 一批，替补层上同样；ctx.PinTickRate 钉 1–5 时物按钉档、子弹取 1。</remarks>
    /// <param name="tier">档位：Default 40² / Large 60² / Huge 100²。</param>
    /// <param name="ground">地面，缺省混凝土。</param>
    public static RimRunnerMapSite Isolated(RimRunnerMapTier tier = RimRunnerMapTier.Default,
        RimRunnerMapGround ground = RimRunnerMapGround.Concrete) =>
        new(Choice.Isolated, RimRunnerIsolatedMap.SizeOf(tier), ground, null);

    /// <summary>在给定宽高的独立小图上执行。</summary>
    /// <remarks>用例须 Level=World，宽高须为正。与某档等大即与该档同池。长条图的长边放 x：vanilla 有几处由图心外扩的格子搜索以宽为上限，长边在 z 上时搜不到远端。</remarks>
    /// <param name="width">宽，x 方向格数。</param>
    /// <param name="height">高，z 方向格数。</param>
    /// <param name="ground">地面，缺省混凝土。</param>
    public static RimRunnerMapSite Isolated(int width, int height,
        RimRunnerMapGround ground = RimRunnerMapGround.Concrete) =>
        new(Choice.Isolated, new IntVec2(width, height), ground, null);

    /// <summary>留在主图上执行，World 级用例的理由进报告；场面立在主图上。</summary>
    /// <remarks>理由进报告 mainMap 行，只记 World 级的回归用例与读数用例；图面来自基线档。按相机判档的物视野内按缩放档 1–5、视野外或不画地图时 15 tick 一批；子弹视野内 1、否则 15；PinTickRate 钉住后恒定。</remarks>
    /// <param name="reason">留主图的原因；World 级用例写空串或全空白时登记期出 EMPTY-REASON 警告。</param>
    public static RimRunnerMapSite Main(string reason) => new(Choice.Main, default, default, reason);

    internal bool IsDefault => Kind == Choice.Default;
}

/// <summary>独立小图的地面；同档位同地面的图在执行槽内复用。</summary>
public enum RimRunnerMapGround
{
    /// <summary>混凝土：全图铺混凝土地板，肥力为零；缺省。</summary>
    Concrete,

    /// <summary>泥土：全图铺 vanilla Soil，可种植、可挖，且 under 层为空。</summary>
    Soil
}
