using RimWorld.Planet;

namespace RimRunner;

internal static class RimRunnerDanglingRefProbe
{
    private const int MaxDiscardedRecorded = 20;

    private static List<string>? path;
    private static Dictionary<string, string>? suspects;
    private static HashSet<string>? deepSaved;
    private static List<string>? discarded;
    private static int discardedCount;
    private static int referencedCount;
    private static int destroyedCheckCount;
    private static int maxDepth;

    internal static int ArmCount { get; private set; }

    internal static void ResetArmCount()
    {
        ArmCount = 0;
    }

    internal static void Arm()
    {
        ArmCount++;
        path = [];
        suspects = new Dictionary<string, string>(StringComparer.Ordinal);
        deepSaved = new HashSet<string>(StringComparer.Ordinal);
        discarded = [];
        discardedCount = 0;
        referencedCount = 0;
        destroyedCheckCount = 0;
        maxDepth = 0;
    }

    private const string ProbeBroken = "[engine] 探针失效：";

    internal const string FaultReason = "[engine] 存盘悬空引用探针未生效";

    internal sealed record Unreached(string ThingId, string Path);

    internal sealed class Reading
    {
        internal readonly List<string> Faults = [];
        internal readonly List<string> Discarded = [];
        internal List<Unreached> Unreached = [];

        internal IEnumerable<string> Detections =>
            Discarded.Concat(Unreached.Select(reference => reference.ThingId + " 写在 " + reference.Path));
    }

    internal static Reading Disarm()
    {
        Reading reading = new();
        if (deepSaved != null)
        {
            if (deepSaved.Count == 0)
            {
                reading.Faults.Add(ProbeBroken + "一个深存对象都没记到（RegisterDeepSaved 的补丁）");
            }

            if (referencedCount == 0)
            {
                reading.Faults.Add(ProbeBroken + "一条引用都没记到（RegisterReferenced 的补丁）");
            }

            if (maxDepth == 0)
            {
                reading.Faults.Add(ProbeBroken + "路径栈从没进过一层（EnterNode 的补丁）");
            }

            if (destroyedCheckCount == 0)
            {
                reading.Faults.Add(ProbeBroken + "已销毁引用检查一次都没进"
                                   + "（CheckSaveReferenceToDestroyedThing 的补丁）");
            }
        }

        if (discarded != null)
        {
            reading.Discarded.AddRange(discarded);
            if (discardedCount > discarded.Count)
            {
                reading.Discarded.Add("其余 " + (discardedCount - discarded.Count) + " 处已丢弃引用未记");
            }
        }

        if (suspects != null)
        {
            reading.Faults.AddRange(RunSentinel());
            reading.Unreached = Report(suspects, deepSaved!);
        }

        path = null;
        suspects = null;
        deepSaved = null;
        discarded = null;
        return reading;
    }

    internal static void DisarmAndLog()
    {
        Reading reading = Disarm();
        foreach (string fault in reading.Faults)
        {
            RimRunnerSentinel.Raise(FaultReason, fault);
        }

        foreach (string detection in reading.Detections)
        {
            RimRunnerSentinel.Raise("存盘检出悬空引用（读档侧会丢这条引用）", detection);
        }
    }

    private static List<Unreached> Report(
        Dictionary<string, string> referenced, HashSet<string> saved)
    {
        List<Unreached> found = [];
        foreach (KeyValuePair<string, string> pair in referenced)
        {
            if (saved.Contains(pair.Key))
            {
                continue;
            }

            found.Add(new Unreached(pair.Key, pair.Value));
        }

        return found;
    }

    private static List<string> RunSentinel()
    {
        List<string> faults = [];

        Thing bait = ThingMaker.MakeThing(ThingDefOf.Steel);
        string baitId = SafeLoadId(bait);
        if (!LooksUnreachable(bait))
        {
            faults.Add("[engine] 哨兵失效①：生成未落图、未进容器的 Thing，判据未识别");
        }

        Thing? control = RimRunnerCurrentMapProbe.EngineCurrentMap()?.listerThings?.AllThings?
            .FirstOrDefault(thing => thing.Spawned);
        if (control == null)
        {
            faults.Add("[engine] 哨兵失效②：地图上没有任何已落图的物，对照面失效");
        }
        else if (LooksUnreachable(control))
        {
            faults.Add("[engine] 哨兵失效②：地图上已落图的 " + control.LabelShort + " 被判成不可达");
        }

        Dictionary<string, string> scratch = new(StringComparer.Ordinal)
        {
            [baitId] = "<哨兵>",
        };
        if (Report(scratch, new HashSet<string>(StringComparer.Ordinal)).Count != 1)
        {
            faults.Add("[engine] 哨兵失效③：靶不在深存集里，未被报出");
        }

        if (Report(scratch, new HashSet<string>(StringComparer.Ordinal) { baitId }).Count != 0)
        {
            faults.Add("[engine] 哨兵失效③：靶已在深存集里，仍被报出");
        }

        if (!ShouldRecord(new Verb_BeatFire()))
        {
            faults.Add("[engine] 哨兵失效④：Verb 被预筛挡掉，非 Thing 的悬空引用整类不记账");
        }

        return faults;
    }

    internal static void PushNode(string nodeName)
    {
        if (path == null)
        {
            return;
        }

        path.Add(nodeName);
        if (path.Count > maxDepth)
        {
            maxDepth = path.Count;
        }
    }

    internal static void PopNode()
    {
        if (path != null && path.Count > 0)
        {
            path.RemoveAt(path.Count - 1);
        }
    }

    internal static void NoteDeepSaved(object obj)
    {
        if (deepSaved != null && obj is ILoadReferenceable referenceable)
        {
            deepSaved.Add(SafeLoadId(referenceable));
        }
    }

    internal static void NoteReferenced(ILoadReferenceable obj, string label)
    {
        if (suspects == null)
        {
            return;
        }

        referencedCount++;
        if (!ShouldRecord(obj))
        {
            return;
        }

        string id = SafeLoadId(obj);
        if (!suspects.ContainsKey(id))
        {
            suspects[id] = string.Join("/", path!) + "/" + label + "（" + Describe(obj) + "）";
        }
    }

    private static string Describe(ILoadReferenceable obj)
    {
        if (obj is Verb { caster: { } caster })
        {
            return obj.GetType().Name + " · 施放者 " + caster.LabelShort + " " + caster.ThingID
                   + (caster.Spawned ? " 在图上" : " 未落图");
        }

        return obj.GetType().Name;
    }

    internal static void NoteDiscardedRef(Thing thing, string label)
    {
        if (discarded == null)
        {
            return;
        }

        discardedCount++;
        if (discarded.Count >= MaxDiscardedRecorded)
        {
            return;
        }

        discarded.Add("已丢弃的 " + thing.GetType().Name + " " + thing
                      + " 被 " + string.Join("/", path!) + "/" + label
                      + " 以 saveDestroyedThings=true 引用（vanilla 写出 null，读档即丢）");
    }

    internal static void NoteDestroyedCheck()
    {
        if (discarded != null)
        {
            destroyedCheckCount++;
        }
    }

    private static bool ShouldRecord(ILoadReferenceable obj)
    {
        return obj is not Thing thing || LooksUnreachable(thing);
    }

    private static bool LooksUnreachable(Thing thing)
    {
        if (thing.Spawned || thing.ParentHolder != null)
        {
            return false;
        }

        return thing is not Pawn pawn || !pawn.IsWorldPawn();
    }

    private static string SafeLoadId(ILoadReferenceable obj)
    {
        try
        {
            return obj.GetUniqueLoadID();
        }
        catch (Exception exception)
        {
            return "<GetUniqueLoadID 抛出：" + exception.GetType().Name + ">";
        }
    }
}

[HarmonyPatch(typeof(ScribeSaver), nameof(ScribeSaver.EnterNode))]
internal static class Patch_ScribeSaver_EnterNode_RimRunnerProbe
{
    private static void Postfix(string nodeName, bool __result)
    {
        if (__result)
        {
            RimRunnerDanglingRefProbe.PushNode(nodeName);
        }
    }
}

[HarmonyPatch(typeof(ScribeSaver), nameof(ScribeSaver.ExitNode))]
internal static class Patch_ScribeSaver_ExitNode_RimRunnerProbe
{
    private static void Postfix()
    {
        RimRunnerDanglingRefProbe.PopNode();
    }
}

[HarmonyPatch(typeof(DebugLoadIDsSavingErrorsChecker),
    nameof(DebugLoadIDsSavingErrorsChecker.RegisterDeepSaved))]
internal static class Patch_LoadIDsChecker_RegisterDeepSaved_RimRunnerProbe
{
    private static void Postfix(object obj)
    {
        RimRunnerDanglingRefProbe.NoteDeepSaved(obj);
    }
}

[HarmonyPatch(typeof(DebugLoadIDsSavingErrorsChecker),
    nameof(DebugLoadIDsSavingErrorsChecker.RegisterReferenced))]
internal static class Patch_LoadIDsChecker_RegisterReferenced_RimRunnerProbe
{
    private static void Postfix(ILoadReferenceable obj, string label)
    {
        if (obj != null)
        {
            RimRunnerDanglingRefProbe.NoteReferenced(obj, label);
        }
    }
}

[HarmonyPatch(typeof(Scribe_References),
    nameof(Scribe_References.CheckSaveReferenceToDestroyedThing))]
internal static class Patch_ScribeReferences_CheckDestroyed_RimRunnerProbe
{
    private static void Postfix(Thing th, string label, bool saveDestroyedThings, bool __result)
    {
        RimRunnerDanglingRefProbe.NoteDestroyedCheck();
        if (__result && saveDestroyedThings && th.Discarded)
        {
            RimRunnerDanglingRefProbe.NoteDiscardedRef(th, label);
        }
    }
}
