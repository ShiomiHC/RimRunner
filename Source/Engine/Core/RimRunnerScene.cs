namespace RimRunner;

/// <summary>场面声明基类：装配一个场面、相机聚焦并暂停。步骤表逐步推进。</summary>
/// <remarks>实现 IRimRunnerShowcase 进展台面板，实现 IRimRunnerFilm 可拍镜头；任何场面都能按步骤表拍路径录像。</remarks>
public abstract class RimRunnerScene
{
    /// <summary>场面 Id，在场面之间唯一。</summary>
    /// <remarks>不得为空或取 step、teardown、stop、list；可与用例 Id 同名。</remarks>
    public abstract string Id { get; }

    /// <summary>面板上显示的短名。</summary>
    public abstract string Title { get; }

    /// <summary>立场时段，缺省上午。</summary>
    /// <remarks>立场时先把游戏时间往前跳到这个时段，再装配。</remarks>
    public virtual RimRunnerSceneTime StartTime => RimRunnerSceneTime.Morning;

    /// <summary>对世界的影响范围，缺省局部。</summary>
    /// <remarks>Local 换场执行清理栈，清理有失败或全局指纹未回到场面装配前即回载基线档。指纹含研究进度、物品发现态、游戏条件、非隐藏派系关系与描述符 WorldPrint 登记的项。</remarks>
    public virtual RimRunnerSceneScope Scope => RimRunnerSceneScope.Local;

    /// <summary>立场选中后自动打开的检视页类型，null = 不开。</summary>
    /// <remarks>须是 InspectTabBase 子类；立场时聚焦对象打不开这一页即中止立场。</remarks>
    public virtual Type? InspectTab => null;

    /// <summary>场面立在哪张图上，缺省独立小图（缺省档、混凝土）。</summary>
    /// <remarks>缺省与 Isolated(档位, 地面) 立场时领取一张独立小图设为当前地图，图上只有场面自己装配的。Main(理由) 立在基线档主图上。展台换场清理后图面复原即留给下一场同档同地面的场面，否则回载基线档。拍摄时镜头之间世界动过即先回载。</remarks>
    public virtual RimRunnerMapSite MapSite => RimRunnerMapSite.Default;

    /// <summary>装配场面，返回相机聚焦并选中的对象（须已在当前地图上）。</summary>
    public abstract Thing Assemble(RimRunnerRegressionContext ctx);

    /// <summary>步骤表：装好之后人逐步推进的动作，同一场面内步骤名不得重复。</summary>
    /// <remarks>步骤要取装配时生成的对象：装配时 ctx.RegisterPawn 或 RegisterThing，步骤里 GetPawn 或 GetThing&lt;T&gt;。</remarks>
    public virtual IReadOnlyList<RimRunnerSceneStep> Steps => [];

    /// <summary>带完成条件、未指定 withinTicks 的步骤取的时限，tick。</summary>
    public virtual int DefaultWithinTicks => 2500;

    /// <summary>场面种子：立场前钉 Rand，定装配生成的对象。</summary>
    /// <remarks>null 取场面 Id 的稳定哈希；面板立场与拍摄用同一种子；装配消耗的随机数已改变，同一种子出的对象随之变。</remarks>
    public virtual int? SceneSeed => null;

    internal int ResolveSceneSeed()
    {
        return SceneSeed ?? RimRunnerSceneSeed.StableHash(Id);
    }
}

/// <summary>展台角色：实现才进展台名册、能在展台实例的面板里立场。</summary>
public interface IRimRunnerShowcase
{
}

/// <summary>拍摄角色：实现才进拍摄名册的镜头部分，rimrunner film 拍它的镜头。</summary>
public interface IRimRunnerFilm
{
    /// <summary>镜头表：rimrunner film 从本场面拍的静帧与片段。</summary>
    /// <remarks>同一场面内镜头名唯一；拍摄时每个镜头都在基线档的世界上立场，自载档以来世界动过即先回载。</remarks>
    IReadOnlyList<RimRunnerShot> Shots { get; }
}

/// <summary>场面对世界的影响范围。</summary>
public enum RimRunnerSceneScope
{
    /// <summary>局部：只生成自己的对象并登记清理栈。换场执行清理栈，清理失败或指纹未复原时另回载。</summary>
    Local,

    /// <summary>改动无法撤回的全局状态。立过之后下一场先回载。</summary>
    Global
}

/// <summary>场面时段：立场时段与面板跳时按钮共用。</summary>
public enum RimRunnerSceneTime
{
    /// <summary>上午 10 时。</summary>
    Morning,

    /// <summary>黄昏：此后天光首次降到昏暗段中点的时刻，随地图纬度与季节。</summary>
    Dusk,

    /// <summary>深夜 0 时。</summary>
    Midnight
}

/// <summary>场面的一步：显示名、动作、前驱、完成条件与观察时长。</summary>
/// <remarks>点一步时先按序补执行该步路径上本场面尚未成功执行过的前驱，再执行该步本身。不在路径上的已执行步骤不撤销。动作与 Assemble 同一套场面态语义。动作里同步推 tick 的夹具不出帧，拍镜头与路径录像时结果里记一条拍摄提示。</remarks>
public sealed class RimRunnerSceneStep
{
    /// <summary>after 取此值 = 直接接在立场之后。</summary>
    public const string StageRoot = "";

    /// <summary>显示名，也是 `rimrunner showcase step &lt;名&gt;` 的指定键。</summary>
    public string Name { get; }

    /// <summary>动作。参数是本场面的场面 ctx。null 即只等、不执行动作。</summary>
    public Action<RimRunnerRegressionContext>? Action { get; }

    /// <summary>前驱步骤名；null = 表里上一步，StageRoot = 立场。</summary>
    /// <remarks>须是表里排在本步之前的步骤名。</remarks>
    public string? After { get; }

    /// <summary>完成之后再推进的 tick 数，0 即完成后不推；负数登记期拒。</summary>
    public int DwellTicks { get; }

    /// <summary>完成条件：每推进一 tick 判一次，action 执行后当即先判一次。</summary>
    public Func<RimRunnerRegressionContext, bool>? Until { get; }

    /// <summary>从 action 执行完起算的时限，tick；0 取场面的 DefaultWithinTicks。</summary>
    public int WithinTicks { get; }

    /// <summary>录制这一步时的倍速，0 取所在片段的缺省，只在拍摄时起作用。</summary>
    public int FilmSpeed { get; }

    /// <summary>构造一步；名字不得为空，动作可为 null。</summary>
    public RimRunnerSceneStep(string name, Action<RimRunnerRegressionContext>? action, string? after = null,
        int dwellTicks = 0, Func<RimRunnerRegressionContext, bool>? until = null, int withinTicks = 0,
        int filmSpeed = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " 场面步骤须有名字", nameof(name));
        }

        Name = name;
        Action = action;
        After = after;
        DwellTicks = dwellTicks;
        Until = until;
        WithinTicks = withinTicks;
        FilmSpeed = filmSpeed;
    }
}
