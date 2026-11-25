using System.Diagnostics;

namespace RimRunner;

internal static class RimRunnerStorytellerQuiesce
{
    private static IncidentQueue? installed;

    private static readonly Dictionary<QueuedIncident, Enqueue> enqueuedBy = [];

    private static readonly HashSet<QueuedIncident> heldDue = [];
    private const int CallerFrames = 2;
    private const int LogFrames = 8;

    private static readonly List<string> pendingBetween = [];

    private sealed class Enqueue
    {
        public string? CaseId;
        public bool Teardown;
        public required string Caller;
        public required string Stack;
    }

    internal static bool HoldsFiring(IncidentQueue queue) =>
        !DebugSettings.enableStoryteller && ReferenceEquals(queue, installed);

    internal static bool StillInEffect =>
        !DebugSettings.enableStoryteller
        && installed != null
        && ReferenceEquals(Find.Storyteller?.incidentQueue, installed);

    internal static string DescribeState()
    {
        string queue = installed == null ? "未安装"
            : ReferenceEquals(Find.Storyteller?.incidentQueue, installed) ? "在位" : "已不在位";
        return "enableStoryteller=" + DebugSettings.enableStoryteller + "，替换队列" + queue;
    }

    internal static bool LiveInEffect =>
        DebugSettings.enableStoryteller
        && Find.Storyteller is Storyteller storyteller
        && !ReferenceEquals(storyteller.incidentQueue, installed)
        && storyteller.storytellerComps.Count == EnabledCompCount(storyteller);

    private static int EnabledCompCount(Storyteller storyteller) => storyteller.def.comps.Count(props => props.Enabled);

    internal static string DescribeLiveState()
    {
        Storyteller storyteller = Find.Storyteller;
        if (storyteller == null)
        {
            return "enableStoryteller=" + DebugSettings.enableStoryteller + "，Find.Storyteller 为 null";
        }

        return "enableStoryteller=" + DebugSettings.enableStoryteller
               + "，队列" + (ReferenceEquals(storyteller.incidentQueue, installed) ? "是替换队列" : "不是替换队列")
               + "，comp " + storyteller.storytellerComps.Count + "/" + EnabledCompCount(storyteller) + "（def 里 Enabled 的条数）";
    }

    internal static void Ensure(string clearedAt)
    {
        DebugSettings.enableStoryteller = false;

        Storyteller storyteller = Find.Storyteller;
        if (storyteller == null)
        {
            return;
        }

        if (installed != null && ReferenceEquals(storyteller.incidentQueue, installed))
        {
            foreach (QueuedIncident queued in installed)
            {
                string line = DescribeAndLog(queued, null, out Enqueue? record);
                pendingBetween.Add("叙事者队列：" + line + "，" + clearedAt + " 开始执行时清除"
                                   + (record?.CaseId == null ? "" : "（入队用例收尾未清出）"));
            }

            installed.Clear();
            enqueuedBy.Clear();
            heldDue.Clear();
            return;
        }

        int dropped = storyteller.incidentQueue == null ? 0 : storyteller.incidentQueue.Count;
        enqueuedBy.Clear();
        heldDue.Clear();
        installed = new IncidentQueue();
        storyteller.incidentQueue = installed;

        if (dropped > 0)
        {
            RimRunnerEngineLog.Info("叙事者停用：丢弃已排队事件 " + dropped + " 条");
        }
    }

    internal static List<string> DrainQueued(string ownerId)
    {
        var drained = new List<string>();
        IncidentQueue? queue = Find.Storyteller?.incidentQueue;
        if (queue == null || !ReferenceEquals(queue, installed) || queue.Count == 0)
        {
            return drained;
        }

        foreach (QueuedIncident queued in queue)
        {
            string line = DescribeAndLog(queued, ownerId, out Enqueue? record);
            if (record != null && record.CaseId == null)
            {
                pendingBetween.Add("叙事者队列：" + line + "，" + ownerId + " 收尾时清除");
            }
            else
            {
                drained.Add(line);
            }
        }

        queue.Clear();
        enqueuedBy.Clear();
        heldDue.Clear();
        return drained;
    }

    internal static void DrainBetween(List<string> sink)
    {
        sink.AddRange(pendingBetween);
        pendingBetween.Clear();
    }

    internal static void NoteHeldDue(List<QueuedIncident> queued)
    {
        int now = Find.TickManager.TicksGame;
        foreach (QueuedIncident incident in queued)
        {
            if (incident != null && !incident.TriedToFire && incident.FireTick <= now)
            {
                heldDue.Add(incident);
            }
        }
    }

    internal static string Fate(QueuedIncident queued)
    {
        int now = Find.TickManager?.TicksGame ?? -1;
        return heldDue.Contains(queued) ? "被扣住未发"
            : queued.FireTick > now ? "清出时未到期"
            : "到期后未再推 tick";
    }

    private static string DescribeAndLog(QueuedIncident queued, string? ownerId, out Enqueue? record)
    {
        string def = queued.FiringIncident?.def?.defName ?? "<def?>";
        enqueuedBy.TryGetValue(queued, out record);
        string window = record == null ? "?"
            : record.CaseId == null ? "用例间"
            : (record.CaseId == ownerId ? "" : record.CaseId + " ") + (record.Teardown ? "收尾" : "体内");
        int now = Find.TickManager?.TicksGame ?? -1;
        string line = def + " 于" + window + "入队←" + (record?.Caller ?? "<入队方?>");

        RimRunnerEngineLog.Info("叙事者队列清除：" + def
                                + " fireTick=" + queued.FireTick + "（现 " + now + "）" + Fate(queued)
                                + " retry=" + queued.RetryDurationTicks
                                + " triedToFire=" + queued.TriedToFire
                                + " target=" + DescribeTarget(queued.FiringIncident?.parms?.target)
                                + " 入队窗=" + window + " 栈=" + (record?.Stack ?? "<未记>"));
        return line;
    }

    private static string DescribeTarget(IIncidentTarget? target) => target switch
    {
        null => "null",
        Map map => "Map#" + map.uniqueID + (map.Disposed ? "(已销毁)" : "") + (map.IsPlayerHome ? "(玩家家园)" : ""),
        _ => target.GetType().Name + ":" + target
    };

    internal static void NoteEnqueued(IncidentQueue queue)
    {
        if (!ReferenceEquals(queue, installed))
        {
            return;
        }

        string? caller = null;
        if (RimRunnerProcessMode.Stages)
        {
            foreach (QueuedIncident queued in queue)
            {
                caller ??= DescribeCaller(new StackTrace(1, false), CallerFrames);
                RimRunnerSceneHost.NoteDroppedIncident(
                    (queued?.FiringIncident?.def?.defName ?? "<def?>") + "←" + caller);
            }

            queue.Clear();
            return;
        }

        StackTrace? trace = null;
        RimRunnerContext? active = RimRunnerContext.Active;
        bool attributed = RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Case or RimRunnerPhase.Scene => true,
            RimRunnerPhase.Enumeration or RimRunnerPhase.None => false
        };
        RimRunnerContext? attributedCtx = attributed ? active! : null;
        foreach (QueuedIncident queued in queue)
        {
            if (queued == null || enqueuedBy.ContainsKey(queued))
            {
                continue;
            }

            trace ??= new StackTrace(1, false);
            enqueuedBy[queued] = new Enqueue
            {
                CaseId = attributedCtx?.Current?.id,
                Teardown = attributedCtx != null && attributedCtx.TearingDown,
                Caller = DescribeCaller(trace, CallerFrames),
                Stack = DescribeCaller(trace, LogFrames)
            };
        }
    }

    private static string DescribeCaller(StackTrace trace, int maxFrames) =>
        RimRunnerCallSite.Describe(trace, maxFrames, type => type == typeof(IncidentQueue)
                                                            || type == typeof(RimRunnerStorytellerQuiesce)
                                                            || type == typeof(Patch_IncidentQueue_Add_RimRunnerEnqueuer)
                                                            || type == typeof(Patch_IncidentQueue_AddDef_RimRunnerEnqueuer))
        ?? "<入队方?>";
}
