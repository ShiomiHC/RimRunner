namespace RimRunner;

public sealed partial class RimRunnerRegressionContext
{
    internal const string FilmOnlyReason = "悬停只在拍摄实例里生效";

    /// <summary>拍摄实例里悬停在物体上，逐帧跟随物体；持续到另设悬停或 EndHover。</summary>
    /// <remarks>展台实例不生效，只落一条面板消息；自动实例里调用即抛。</remarks>
    public void HoverOn(Thing thing)
    {
        RequireHoverPhase(nameof(HoverOn));
        if (thing == null)
        {
            throw new ArgumentNullException(nameof(thing));
        }

        switch (RimRunnerProcessMode.Current)
        {
            case RimRunnerInstanceMode.Film:
                RimRunnerHiddenWindowMouse.SetTarget(thing);
                break;
            case RimRunnerInstanceMode.Showcase:
                RimRunnerSceneHost.NoteHoverIgnored();
                break;
            default:
                throw new InvalidOperationException(RimRunnerLog.Prefix + " " + FilmOnlyReason + "，展台实例里调用只落一条面板消息");
        }
    }

    /// <summary>拍摄实例里悬停在格中心；持续到另设悬停或 EndHover。</summary>
    /// <remarks>同 HoverOn。</remarks>
    public void HoverAt(IntVec3 cell)
    {
        RequireHoverPhase(nameof(HoverAt));
        switch (RimRunnerProcessMode.Current)
        {
            case RimRunnerInstanceMode.Film:
                RimRunnerHiddenWindowMouse.SetTarget(cell);
                break;
            case RimRunnerInstanceMode.Showcase:
                RimRunnerSceneHost.NoteHoverIgnored();
                break;
            default:
                throw new InvalidOperationException(RimRunnerLog.Prefix + " " + FilmOnlyReason + "，展台实例里调用只落一条面板消息");
        }
    }

    /// <summary>撤销悬停，鼠标停回画面外。</summary>
    public void EndHover()
    {
        RequireHoverPhase(nameof(EndHover));
        switch (RimRunnerProcessMode.Current)
        {
            case RimRunnerInstanceMode.Film:
                RimRunnerHiddenWindowMouse.ClearTarget();
                break;
            case RimRunnerInstanceMode.Showcase:
                RimRunnerSceneHost.NoteHoverIgnored();
                break;
            default:
                throw new InvalidOperationException(RimRunnerLog.Prefix + " " + FilmOnlyReason + "，展台实例里调用只落一条面板消息");
        }
    }

    private void RequireHoverPhase(string entryPoint)
    {
        string? refusal = Phase switch
        {
            RimRunnerPhase.Scene => null,
            RimRunnerPhase.Case => "悬停只在场面里可用",
            RimRunnerPhase.Enumeration or RimRunnerPhase.None => throw new InvalidOperationException(
                RimRunnerLog.Prefix + " " + entryPoint + " 只在场面阶段可调，阶段不该是 " + Phase)
        };
        if (refusal != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + refusal);
        }
    }
}
