using System.Runtime.CompilerServices;
using RimWorld.Planet;

namespace RimRunner;

/// <summary>孤儿 pawn（未销毁、未 spawn、无 holder、不在世界池）的收集与清扫。</summary>
/// <remarks>收集面：与 PawnsFinder.All_AliveOrDead 里任一 pawn 有直接关系（任一方向）的孤儿，加上虚拟关系记录引用着的。</remarks>
public static class RimRunnerOrphanSweep
{
    internal static bool IsOrphan(Pawn p) =>
        !p.Destroyed && !p.Spawned && p.ParentHolder == null && !p.IsWorldPawn();

    /// <summary>收集当前全部孤儿。</summary>
    public static List<Pawn> Collect() => Collect(null);

    private static List<Pawn> Collect(HashSet<Pawn>? ignoredHolders)
    {
        List<Pawn> found = [];
        HashSet<Pawn> seen = [];
        List<Pawn> holders = Holders(ignoredHolders);
        CollectFromDirectRelations(holders, seen, found);
        CollectFromReverseIndex(holders, seen, found);
        CollectFromRelationshipRecords(seen, found);
        return found;
    }

    private static List<Pawn> Holders(HashSet<Pawn>? ignored = null) =>
        PawnsFinder.All_AliveOrDead.Distinct().Where(pawn => ignored == null || !ignored.Contains(pawn)).ToList();

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pawnsWithDirectRelationsWithMe")]
    private static extern ref HashSet<Pawn> ReverseIndex(Pawn_RelationsTracker tracker);

    private static void CollectFromDirectRelations(List<Pawn> holders, HashSet<Pawn> seen, List<Pawn> found)
    {
        foreach (Pawn holder in holders)
        {
            List<DirectPawnRelation>? relations = holder.relations?.DirectRelations;
            if (relations == null)
            {
                continue;
            }

            for (int i = 0; i < relations.Count; i++)
            {
                Consider(relations[i].otherPawn, seen, found);
            }
        }
    }

    private static void CollectFromReverseIndex(List<Pawn> holders, HashSet<Pawn> seen, List<Pawn> found)
    {
        foreach (Pawn holder in holders)
        {
            if (holder.relations == null)
            {
                continue;
            }

            foreach (Pawn other in ReverseIndex(holder.relations))
            {
                Consider(other, seen, found);
            }
        }
    }

    private static void CollectFromRelationshipRecords(HashSet<Pawn> seen, List<Pawn> found)
    {
        RelationshipRecords? records = Find.RelationshipRecords;
        if (records == null)
        {
            return;
        }

        foreach (KeyValuePair<int, RelationshipRecord> pair in records.Records)
        {
            foreach (Pawn reference in pair.Value.References)
            {
                Consider(reference, seen, found);
            }
        }
    }

    private static void Consider(Pawn? candidate, HashSet<Pawn> seen, List<Pawn> found)
    {
        if (candidate == null || !seen.Add(candidate))
        {
            return;
        }

        if (IsOrphan(candidate))
        {
            found.Add(candidate);
        }
    }

    internal static string DescribeCarriers(Pawn orphan)
    {
        List<string> parts =
        [
            orphan.ThingID + " kind=" + (orphan.kindDef?.defName ?? "null")
            + " faction=" + (orphan.Faction?.Name ?? "null")
            + " age=" + orphan.ageTracker?.AgeBiologicalYears
        ];

        foreach (Pawn holder in Holders())
        {
            List<DirectPawnRelation>? relations = holder.relations?.DirectRelations;
            if (relations == null) continue;
            foreach (DirectPawnRelation r in relations)
            {
                if (r.otherPawn == orphan)
                {
                    parts.Add("持有者 " + DescribePawn(holder) + " 视其为 " + r.def.defName);
                }
            }

            if (ReverseIndex(holder.relations!).Contains(orphan))
            {
                parts.Add("持有者 " + DescribePawn(holder) + " 的反向索引");
            }
        }

        RelationshipRecords? records = Find.RelationshipRecords;
        if (records != null)
        {
            foreach (KeyValuePair<int, RelationshipRecord> pair in records.Records)
            {
                if (pair.Value.References.Contains(orphan))
                {
                    parts.Add("虚拟关系记录 #" + pair.Key + "（被丢弃者 " + pair.Value.Name + "）");
                }
            }
        }

        List<DirectPawnRelation>? own = orphan.relations?.DirectRelations;
        if (own != null)
        {
            foreach (DirectPawnRelation r in own)
            {
                parts.Add("自身关系 " + r.def.defName + " → " + DescribePawn(r.otherPawn));
            }
        }

        return string.Join("；", parts);
    }

    private static string DescribePawn(Pawn? p)
    {
        if (p == null) return "null";
        string state = p.Discarded ? "已丢弃" : p.Destroyed ? "已销毁" : p.Dead ? "已死"
            : p.Spawned ? "在图" : p.IsWorldPawn() ? "世界池" : p.ParentHolder != null ? "容器内 " + p.ParentHolder : "孤儿";
        return p.LabelShort + "(" + p.ThingID + " " + (p.kindDef?.defName ?? "null") + " " + state + ")";
    }

    /// <summary>拍一份当前孤儿快照作基线。</summary>
    public static HashSet<Pawn> Snapshot() => [.. Collect()];

    internal static HashSet<Pawn> SnapshotIgnoringHolders(HashSet<Pawn> holders) =>
        [.. Collect(holders)];

    /// <summary>只清基线之后新增的孤儿，清扫途中新变得可达的一并清，返回清除的数量。</summary>
    /// <remarks>清除即销毁并丢弃。清除后删除引用不足两条的虚拟关系记录，连同剩余引用者身上的该条虚拟关系。</remarks>
    public static int SweepAddedSince(HashSet<Pawn> before) => DiscardUntilStable(before);

    internal static int SweepAtCaseEnd() => DiscardUntilStable(null);

    internal static int Sweep()
    {
        if (RimRunnerObservationBuffer.ActiveCaseId != null && !RimRunnerScenarioManager.Assembling)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " 用例 " + RimRunnerObservationBuffer.ActiveCaseId
                + " 执行期内禁止调用 RimRunnerOrphanSweep.Sweep()");
        }

        return DiscardUntilStable(null);
    }

    private static int DiscardUntilStable(HashSet<Pawn>? spared)
    {
        HashSet<Pawn> attempted = [];
        while (true)
        {
            int round = 0;
            foreach (Pawn orphan in Collect())
            {
                if ((spared != null && spared.Contains(orphan)) || !attempted.Add(orphan))
                {
                    continue;
                }

                RimRunnerPawns.DestroyAndDiscard(orphan);
                round++;
            }

            if (round == 0)
            {
                break;
            }
        }

        Find.RelationshipRecords?.CleanupUnusedRecords();
        return attempted.Count;
    }
}
