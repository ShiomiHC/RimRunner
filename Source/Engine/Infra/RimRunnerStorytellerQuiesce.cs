using System.Diagnostics;

namespace RimRunner;

internal static class RimRunnerStorytellerQuiesce
{
    private static IncidentQueue? installed;

    private static readonly Dictionary<QueuedIncident, Enqueue> enqueuedBy = [];

    private static readonly HashSet<QueuedIncident> heldDue = [];
    private const int CallerFrames = 2;
    private const int LogFrames = 8;

    private static readonly List<BetweenClear> pendingBetween = [];

    private sealed class Enqueue
    {
        public string? CaseId;
        public bool Teardown;
        public required string Caller;
        public required string Stack;
    }

    internal sealed class ClearedIncident
    {
        internal bool Recorded;
        internal required string DefName;
        internal string? CaseId;
        internal bool Teardown;
        internal required string Caller;

        internal string Window(string? ownerId) =>
            !Recorded ? "?"
            : CaseId == null ? "用例间"
            : (CaseId == ownerId ? "" : CaseId + " ") + (Teardown ? "收尾" : "体内");

        internal string Text(string? ownerId) => DefName + " 于" + Window(ownerId) + "入队←" + Caller;
    }

    internal sealed class BetweenClear
    {
        internal required ClearedIncident Incident;
        internal string? WindowOwner;
        internal required string ClearedAt;
        internal bool AtStart;
        internal bool TeardownMissed;

        internal string Text => "叙事者队列：" + Incident.Text(WindowOwner) + "，" + ClearedAt
                                + (AtStart ? " 开始执行时清除" : " 收尾时清除")
                                + (TeardownMissed ? "（入队用例收尾未清出）" : "");
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
                ClearedIncident incident = DescribeAndLog(queued, null);
                pendingBetween.Add(new BetweenClear
                {
                    Incident = incident,
                    WindowOwner = null,
                    ClearedAt = clearedAt,
                    AtStart = true,
                    TeardownMissed = incident.CaseId != null
                });
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

    internal static List<ClearedIncident> DrainQueued(string ownerId)
    {
        var drained = new List<ClearedIncident>();
        IncidentQueue? queue = Find.Storyteller?.incidentQueue;
        if (queue == null || !ReferenceEquals(queue, installed) || queue.Count == 0)
        {
            return drained;
        }

        foreach (QueuedIncident queued in queue)
        {
            ClearedIncident incident = DescribeAndLog(queued, ownerId);
            if (incident.Recorded && incident.CaseId == null)
            {
                pendingBetween.Add(new BetweenClear
                {
                    Incident = incident,
                    WindowOwner = ownerId,
                    ClearedAt = ownerId,
                    AtStart = false
                });
            }
            else
            {
                drained.Add(incident);
            }
        }

        queue.Clear();
        enqueuedBy.Clear();
        heldDue.Clear();
        return drained;
    }

    internal static List<BetweenClear> DrainBetween()
    {
        List<BetweenClear> drained = [.. pendingBetween];
        pendingBetween.Clear();
        return drained;
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

    internal enum FateKind
    {
        HeldDue,
        NotYetDue,
        NoTickSinceDue
    }

    internal static FateKind Fate(QueuedIncident queued)
    {
        int now = Find.TickManager?.TicksGame ?? -1;
        return heldDue.Contains(queued) ? FateKind.HeldDue
            : queued.FireTick > now ? FateKind.NotYetDue
            : FateKind.NoTickSinceDue;
    }

    private static string FateText(FateKind fate) => fate switch
    {
        FateKind.HeldDue => "被扣住未发",
        FateKind.NotYetDue => "清出时未到期",
        _ => "到期后未再推 tick"
    };

    private static ClearedIncident DescribeAndLog(QueuedIncident queued, string? ownerId)
    {
        string def = queued.FiringIncident?.def?.defName ?? "<def?>";
        bool recorded = enqueuedBy.TryGetValue(queued, out Enqueue? record);
        var incident = new ClearedIncident
        {
            Recorded = recorded,
            DefName = def,
            CaseId = record?.CaseId,
            Teardown = record != null && record.Teardown,
            Caller = record?.Caller ?? "<入队方?>"
        };
        int now = Find.TickManager?.TicksGame ?? -1;

        RimRunnerEngineLog.Info("叙事者队列清除：" + def
                                + " fireTick=" + queued.FireTick + "（现 " + now + "）" + FateText(Fate(queued))
                                + " retry=" + queued.RetryDurationTicks
                                + " triedToFire=" + queued.TriedToFire
                                + " target=" + DescribeTarget(queued.FiringIncident?.parms?.target)
                                + " 入队窗=" + incident.Window(ownerId) + " 栈=" + (record?.Stack ?? "<未记>"));
        return incident;
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
