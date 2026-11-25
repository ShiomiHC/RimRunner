using System.Collections;
using System.Runtime.CompilerServices;

namespace RimRunner;

internal static class RimRunnerLongEventDrain
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "eventTextKey")]
    private static extern ref string TextKey(
        [UnsafeAccessorType("Verse.LongEventHandler+QueuedLongEvent, Assembly-CSharp")] object queuedEvent);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "doAsynchronously")]
    private static extern ref bool Asynchronous(
        [UnsafeAccessorType("Verse.LongEventHandler+QueuedLongEvent, Assembly-CSharp")] object queuedEvent);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "eventActionEnumerator")]
    private static extern ref IEnumerator Enumerator(
        [UnsafeAccessorType("Verse.LongEventHandler+QueuedLongEvent, Assembly-CSharp")] object queuedEvent);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "levelToLoad")]
    private static extern ref string LevelToLoad(
        [UnsafeAccessorType("Verse.LongEventHandler+QueuedLongEvent, Assembly-CSharp")] object queuedEvent);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "eventText")]
    private static extern ref string Text(
        [UnsafeAccessorType("Verse.LongEventHandler+QueuedLongEvent, Assembly-CSharp")] object queuedEvent);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "eventAction")]
    private static extern ref Action Action(
        [UnsafeAccessorType("Verse.LongEventHandler+QueuedLongEvent, Assembly-CSharp")] object queuedEvent);

    private static readonly FieldInfo? CurrentEvent = AccessTools.Field(typeof(LongEventHandler), "currentEvent");

    private static readonly FieldInfo? EventQueue = AccessTools.Field(typeof(LongEventHandler), "eventQueue");

    private static readonly bool unavailable = ReportUnavailable();

    private static bool ReportUnavailable()
    {
        List<string> missing = [];
        if (CurrentEvent == null)
            missing.Add("LongEventHandler.currentEvent");
        if (EventQueue == null)
            missing.Add("LongEventHandler.eventQueue");
        if (missing.Count == 0)
        {
            return false;
        }

        RimRunnerEngineFault.Raise("无法解析 " + string.Join("、", missing) + "：不补执行长事件，交给真实帧");
        return true;
    }

    private const int MaxEventsPerDrain = 10000;

    internal static void Drain()
    {
        if (unavailable || !LongEventHandler.AnyEventNowOrWaiting)
        {
            return;
        }

        for (int executed = 0; executed < MaxEventsPerDrain;)
        {
            object? current = CurrentEvent!.GetValue(null);
            object? next = current ?? Head();
            if (next == null || !IsBackground(next))
            {
                return;
            }

            LongEventHandler.LongEventsUpdate(out _);
            if (current != null)
            {
                executed++;
            }
        }

        throw new InvalidOperationException(
            RimRunnerLog.Prefix + " 长事件队列一次补处理 " + MaxEventsPerDrain
                                + " 个后台事件仍未排空：某个事件的动作持续排入新的后台事件");
    }

    internal static bool IsBackground(object queuedEvent)
    {
        return TextKey(queuedEvent).NullOrEmpty()
               && !Asynchronous(queuedEvent)
               && Enumerator(queuedEvent) == null
               && LevelToLoad(queuedEvent).NullOrEmpty();
    }

    internal static List<object> Pending()
    {
        List<object> pending = [];
        if (unavailable)
        {
            return pending;
        }

        object? current = CurrentEvent!.GetValue(null);
        if (current != null)
        {
            pending.Add(current);
        }

        pending.AddRange(((IEnumerable)EventQueue!.GetValue(null)!).Cast<object>());
        return pending;
    }

    internal static string Describe()
    {
        if (unavailable)
        {
            return "无法读取长事件队列：无法解析 LongEventHandler 的字段";
        }

        List<object> pending = Pending();
        if (pending.Count == 0)
        {
            return "队列已空";
        }

        const int Shown = 3;
        string described = string.Join("、", pending.Take(Shown).Select(DescribeOne));
        return pending.Count > Shown ? described + " 等 " + pending.Count + " 个" : described;
    }

    private static string DescribeOne(object queuedEvent)
    {
        if (!Text(queuedEvent).NullOrEmpty())
        {
            return "「" + Text(queuedEvent) + "」";
        }

        if (!TextKey(queuedEvent).NullOrEmpty())
        {
            return "文案键 " + TextKey(queuedEvent);
        }

        MethodInfo? method = Enumerator(queuedEvent)?.GetType().GetMethod("MoveNext") ?? Action(queuedEvent)?.Method;
        return method == null ? "无文案的事件" : "无文案的事件 " + method.DeclaringType?.FullName + "." + method.Name;
    }

    internal static void Withdraw(ICollection<object?> events)
    {
        if (unavailable)
        {
            return;
        }

        if (events.Contains(CurrentEvent!.GetValue(null)))
        {
            CurrentEvent.SetValue(null, null);
        }

        object queue = EventQueue!.GetValue(null)!;
        List<object> kept = ((IEnumerable)queue).Cast<object>().Where(queued => !events.Contains(queued)).ToList();
        MethodInfo? clear = AccessTools.Method(queue.GetType(), "Clear");
        MethodInfo? enqueue = AccessTools.Method(queue.GetType(), "Enqueue");
        if (clear == null || enqueue == null)
        {
            RimRunnerEngineFault.Raise("无法解析 " + queue.GetType().FullName + "."
                                       + (clear == null ? "Clear" : "Enqueue")
                                       + "：撤回长事件未执行，排进去的事件留在队列里仍会执行");
            return;
        }

        clear.Invoke(queue, null);
        foreach (object queued in kept)
        {
            enqueue.Invoke(queue, [queued]);
        }
    }

    private static object? Head()
    {
        if (unavailable)
        {
            return null;
        }

        IEnumerator queued = ((IEnumerable)EventQueue!.GetValue(null)!).GetEnumerator();
        return queued.MoveNext() ? queued.Current : null;
    }
}
