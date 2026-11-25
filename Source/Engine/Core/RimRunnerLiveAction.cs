namespace RimRunner;

/// <summary>作者具名动作：在运行中的实例上按名执行的一段正文，live do 调用。</summary>
/// <remarks>写在产品仓的 &lt;dir&gt;/live/&lt;Id&gt;.cs，一个文件恰好一个动作或条件类，每次调用时编译后执行。ctx 与场面相同：失败断言列进回执、动作继续，RimRunnerFault 中止；登记的复原与清理都不执行。</remarks>
public abstract class RimRunnerLiveAction
{
    internal string? ScriptName;

    /// <summary>动作 Id，live do 按它指定；缺省取脚本文件名。</summary>
    /// <remarks>不必重写；重写成与脚本文件名不同的值即拒。不得与内置原语重名。</remarks>
    public virtual string Id => ScriptName ?? "";

    /// <summary>列表里显示的短名。</summary>
    public abstract string Title { get; }

    /// <summary>参数表：live do 缺必填或给表外的参数都拒。</summary>
    public virtual IReadOnlyList<RimRunnerLiveParam> Params => [];

    /// <summary>动作正文：yield 等帧或原语，走完即动作完成。</summary>
    /// <remarks>ctx.Map 是当前地图；动作做什么经 ctx.AddDiagnostic 说明，进回执。等效果生效 yield RimRunnerTimeDriver.WaitUntil，不能按原版速度推（Ticks、RealSeconds）。</remarks>
    /// <param name="ctx">场面态上下文。</param>
    /// <param name="call">本次调用的参数。</param>
    /// <returns>正文的等待序列。</returns>
    public abstract IEnumerable<RimRunnerWait> Run(RimRunnerRegressionContext ctx, RimRunnerLiveCall call);
}

/// <summary>动作的一个参数：名字、是否必填与说明。</summary>
/// <param name="Name">参数名，写作 --名=值；字母开头，只用字母、数字、- 与 _。</param>
/// <param name="Required">必填的参数未指定就拒。</param>
/// <param name="Meaning">说明，名册列表里显示。</param>
public sealed record RimRunnerLiveParam(string Name, bool Required, string Meaning);

/// <summary>一次调用的参数：按名取值，带格、矩形、Def 与 pawn 的解析助手。</summary>
/// <remarks>动作与具名条件共用。未指定的可选参数取回 null；取表外的名字即抛；值解析失败即中止并指出参数名。</remarks>
public sealed class RimRunnerLiveCall
{
    private readonly string owner;
    private readonly IReadOnlyList<RimRunnerLiveParam> declared;
    private readonly IReadOnlyDictionary<string, string> args;

    internal RimRunnerLiveCall(string owner, IReadOnlyList<RimRunnerLiveParam> declared, IReadOnlyDictionary<string, string> args)
    {
        this.owner = owner;
        this.declared = declared;
        this.args = args;
    }

    internal static string? ArgRefusal(string owner, IReadOnlyList<RimRunnerLiveParam> declared,
        IReadOnlyDictionary<string, string> args)
    {
        string[] unknown = args.Keys.Where(name => declared.All(param => param.Name != name))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            return owner + " 未知参数：" + string.Join("、", unknown) + "。可用参数："
                   + (declared.Count == 0 ? "无" : string.Join("、", declared.Select(param => param.Name)));
        }

        string[] missing = declared.Where(param => param.Required && !args.ContainsKey(param.Name))
            .Select(param => param.Name + "（" + param.Meaning + "）").ToArray();
        return missing.Length > 0 ? owner + " 缺少必填参数：" + string.Join("、", missing) : null;
    }

    /// <summary>参数的原文；未指定为 null。</summary>
    /// <param name="name">参数表里的名字。</param>
    /// <returns>原文或 null。</returns>
    public string? Text(string name)
    {
        if (declared.All(param => param.Name != name))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + owner + " 的参数表里没有 " + name);
        }

        return args.TryGetValue(name, out string? value) ? value : null;
    }

    /// <summary>格，写作 x,z；未指定为 null。</summary>
    /// <param name="name">参数表里的名字。</param>
    /// <returns>格或 null。</returns>
    public IntVec3? Cell(string name) =>
        Text(name) is { } text
            ? RimRunnerDriveTable.ParseInts(text, 2) is { } v ? new IntVec3(v[0], 0, v[1]) : throw Bad(name, "写作 x,z", text)
            : null;

    /// <summary>格矩形，写作 x1,z1,x2,z2，两个对角格不分先后；未指定为 null。</summary>
    /// <param name="name">参数表里的名字。</param>
    /// <returns>矩形或 null。</returns>
    public CellRect? Rect(string name)
    {
        if (Text(name) is not { } text)
        {
            return null;
        }

        int[] v = RimRunnerDriveTable.ParseInts(text, 4) ?? throw Bad(name, "写作 x1,z1,x2,z2", text);
        return CellRect.FromLimits(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }

    /// <summary>按 defName 取 Def；未指定为 null，找不到即中止。</summary>
    /// <param name="name">参数表里的名字。</param>
    /// <returns>Def 或 null。</returns>
    public T? Def<T>(string name) where T : Def =>
        Text(name) is { } text
            ? DefDatabase<T>.GetNamedSilentFail(text) ?? throw Bad(name, "须是 " + typeof(T).Name + " 的 defName", text)
            : null;

    /// <summary>当前地图上按昵称或 ThingID 取 pawn；未指定为 null，找不到即中止。</summary>
    /// <param name="name">参数表里的名字。</param>
    /// <returns>pawn 或 null。</returns>
    public Pawn? Pawn(string name)
    {
        if (Text(name) is not { } text)
        {
            return null;
        }

        return RimRunnerCurrentMapProbe.EngineCurrentMap()?.mapPawns.AllPawnsSpawned.FirstOrDefault(pawn =>
                   string.Equals(RimRunnerLiveServer.GetPawnNick(pawn), text, StringComparison.Ordinal)
                   || string.Equals(pawn.ThingID, text, StringComparison.Ordinal))
               ?? throw Bad(name, "当前地图上没有昵称或 ThingID 为该值的 pawn", text);
    }

    private static RimRunnerLiveArgException Bad(string name, string expectation, string text) =>
        new("参数 " + name + " " + expectation + "，收到：" + text);
}

internal sealed class RimRunnerLiveArgException(string message) : Exception(message);

/// <summary>作者具名条件：live until 按 Id 判定该条件。</summary>
/// <remarks>写在产品仓的 &lt;dir&gt;/live/&lt;Id&gt;.cs，与动作同样每次调用时编译。ctx 与场面态相同；参数写 --名=值，Begin 在判之前调一次，之后每次判调 Met。</remarks>
public abstract class RimRunnerLiveCondition
{
    internal string? ScriptName;

    /// <summary>条件 Id，live until 按它指定；缺省取脚本文件名。</summary>
    /// <remarks>不必重写；重写成与脚本文件名不同的值即拒。不得是内置条件 letter。</remarks>
    public virtual string Id => ScriptName ?? "";

    /// <summary>列表里显示的短名。</summary>
    public abstract string Title { get; }

    /// <summary>参数表：缺必填或给表外的参数都拒；within 与 speed 是保留名。</summary>
    public virtual IReadOnlyList<RimRunnerLiveParam> Params => [];

    /// <summary>判之前调一次：取参数、记下判定所需的基线。</summary>
    /// <param name="ctx">场面态上下文。</param>
    /// <param name="call">本次给的参数。</param>
    public virtual void Begin(RimRunnerRegressionContext ctx, RimRunnerLiveCall call)
    {
    }

    /// <summary>条件是否成立。</summary>
    /// <param name="ctx">场面态上下文，与 Begin 拿到的是同一个。</param>
    /// <returns>成立即停止等待。</returns>
    public abstract bool Met(RimRunnerRegressionContext ctx);
}
