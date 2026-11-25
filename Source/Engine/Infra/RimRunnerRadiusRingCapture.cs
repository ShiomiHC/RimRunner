using System.Threading;

namespace RimRunner;

/// <summary>半径环记录器：记录期间收下游戏要画的每个半径环的半径。</summary>
/// <remarks>记录期间（Begin 到 End）收下的环不画出。用例收尾时没结束的记录由引擎结束、读数丢弃。</remarks>
public static class RimRunnerRadiusRingCapture
{
    private static readonly Lock Sync = new();
    private static readonly List<float> Radii = [];
    private static bool capturing;

    /// <summary>开始记录，并清空上次收下的读数。</summary>
    public static void Begin()
    {
        lock (Sync)
        {
            Radii.Clear();
            capturing = true;
        }
    }

    /// <summary>停止记录并交回本次收下的半径序列。</summary>
    public static List<float> End()
    {
        lock (Sync)
        {
            capturing = false;
            return [.. Radii];
        }
    }

    internal static void ResetForCaseEnd()
    {
        lock (Sync)
        {
            capturing = false;
            Radii.Clear();
        }
    }

    internal static bool Record(IntVec3 center, float radius)
    {
        lock (Sync)
        {
            if (!capturing)
            {
                return false;
            }

            Radii.Add(radius);
            return true;
        }
    }

    /// <summary>把半径序列排成一行逗号分隔的文本。</summary>
    public static string Describe(List<float> radii)
    {
        return string.Join(",", radii.Select(radius => radius.ToString("0.###")));
    }
}
