using System.Text;
using Verse.AI.Group;

namespace RimRunner;

/// <summary>敌队构成：一支可重复部署的敌队，由成员的 kind 与 xenotype 名单，以及派系组成。</summary>
public sealed class RimRunnerRaidSquad
{
    /// <summary>标签。</summary>
    public string Label { get; }
    /// <summary>袭击点数：按点数取 vanilla 折算值，按名单取成员 combatPower×异种系数之和。</summary>
    public float AdjustedPoints { get; }
    /// <summary>所属派系。</summary>
    public Faction Faction { get; }
    /// <summary>构成与部署用的 seed。</summary>
    public int Seed { get; }
    /// <summary>成员名单。</summary>
    public IReadOnlyList<RimRunnerRaidMember> Members { get; }
    internal int ComposedAtTicksAbs { get; }

    internal RimRunnerRaidSquad(
        string label,
        float adjustedPoints,
        Faction faction,
        int seed,
        IReadOnlyList<RimRunnerRaidMember> members,
        int composedAtTicksAbs)
    {
        Label = label;
        AdjustedPoints = adjustedPoints;
        Faction = faction;
        Seed = seed;
        Members = members;
        ComposedAtTicksAbs = composedAtTicksAbs;
    }
}

/// <summary>敌队成员构成：一个成员的 kind 与 xenotype。</summary>
public sealed class RimRunnerRaidMember
{
    /// <summary>兵种。</summary>
    public PawnKindDef Kind { get; }
    /// <summary>成员的异种；null 即未指定。</summary>
    public XenotypeDef? Xenotype { get; }
    internal int? Seed { get; }

    internal RimRunnerRaidMember(PawnKindDef kind, XenotypeDef? xenotype, int? seed = null)
    {
        Kind = kind;
        Xenotype = xenotype;
        Seed = seed;
    }
}

/// <summary>会还手、会推进的一支敌队：GenerateSquad 只算构成，Deploy 才生成 pawn。</summary>
public static class RimRunnerRaid
{
    /// <summary>按袭击点数走 vanilla 抽签，只算一支敌队的构成。</summary>
    /// <remarks>factionDef 缺省时按 vanilla 袭击权重抽派系，权重随点数变；同一 label 在不同月份各算一次，衣物与装备可以不同。抽中的派系构成为空时剔除重抽，剔除项进「袭击构成」日志；计算用自有 seed，不改调用方随机流。</remarks>
    /// <exception cref="InvalidOperationException">指定的派系不满足可用判据或构成为空时抛。抽签权重全为 0 也抛。</exception>
    public static RimRunnerRaidSquad GenerateSquad(RimRunnerContext ctx, float points, string label,
        FactionDef? factionDef = null)
    {
        if (factionDef == null)
        {
            RequireSquadInputs(ctx, label);
            int seed = SquadSeed(label, points);
            Map map = ctx.Map!;
            Faction faction;
            Composition composition;
            List<string> skipped = [];
            using (RimRunnerRandControl.Seeded(seed))
            {
                List<Faction> usable = UsableRaidFactions(points);
                RimRunnerFault.Require(usable.Count > 0, NoUsableFactionReason, "点数=" + points);

                while (true)
                {
                    if (!usable.TryRandomElementByWeight(
                            f => f.def.RaidCommonalityFromPoints(points), out faction))
                    {
                        throw new InvalidOperationException(
                            RimRunnerLog.Prefix + " [" + label + "] 候选派系 " + usable.Count + " 个，但在点数 " + points
                            + " 下袭击权重全为 0"
                            + (skipped.Count == 0 ? "" : "；已剔除构成为空的 " + string.Join("、", skipped)));
                    }

                    composition = Compose(map, faction, points, seed);
                    if (composition.EmptyReason == null)
                    {
                        break;
                    }

                    skipped.Add(faction.def.defName + "（" + composition.EmptyReason + "）");
                    usable.Remove(faction);
                }
            }

            return LogSquad(label, faction, seed, composition,
                skipped.Count == 0 ? "" : " skipped=[" + string.Join("；", skipped) + "]");
        }

        RequireFactionInSave(factionDef);
        if (!TryGenerateSquad(ctx, points, label, factionDef, out RimRunnerRaidSquad? squad, out string? rejection))
        {
            throw new InvalidOperationException(rejection);
        }

        return squad;
    }

    internal static bool TryGenerateSquad(RimRunnerContext ctx, float points, string label, FactionDef factionDef,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RimRunnerRaidSquad? squad,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? rejection)
    {
        RequireSquadInputs(ctx, label);
        int seed = SquadSeed(label, points);
        Map map = ctx.Map!;
        squad = null;
        string? unusable = PinnedRejection(factionDef, points, allowHidden: false, allowNonHumanlike: false);
        if (unusable != null)
        {
            rejection = RimRunnerLog.Prefix + " " + unusable;
            return false;
        }

        Faction faction = Find.FactionManager.FirstFactionOfDef(factionDef)!;

        Composition composition;
        using (RimRunnerRandControl.Seeded(seed))
        {
            composition = Compose(map, faction, points, seed);
        }

        if (composition.EmptyReason != null)
        {
            rejection = RimRunnerLog.Prefix + " [" + label + "] 派系 " + factionDef.defName + " " + composition.EmptyReason;
            return false;
        }

        rejection = null;
        squad = LogSquad(label, faction, seed, composition, "(pinned)");
        return true;
    }

    internal static List<Faction> UsableRaidFactions(float points)
    {
        List<Faction> usable = [];
        List<Faction> all = Find.FactionManager.AllFactionsListForReading;
        for (int i = 0; i < all.Count; i++)
        {
            Faction f = all[i];
            if (RaidFactionRejection(f, points, allowHidden: false, allowNonHumanlike: false) == null)
            {
                usable.Add(f);
            }
        }

        return usable;
    }

    private readonly struct Composition(float adjustedPoints, List<RimRunnerRaidMember>? members, string? emptyReason)
    {
        public readonly float AdjustedPoints = adjustedPoints;
        public readonly List<RimRunnerRaidMember>? Members = members;
        public readonly string? EmptyReason = emptyReason;
    }

    private static Composition Compose(Map map, Faction faction, float points, int seed)
    {
        PawnGroupKindDef combat = PawnGroupKindDefOf.Combat;
        RaidStrategyDef immediate = RaidStrategyDefOf.ImmediateAttack;
        float adjustedPoints = AdjustRaidPoints(points, PawnsArrivalModeDefOf.EdgeWalkIn, immediate, faction, combat, map);
        PawnGroupMakerParms parms = MakeGroupParms(map, faction, adjustedPoints, seed, combat, immediate);
        if (!PawnGroupMakerUtility.TryGetRandomPawnGroupMaker(parms, out PawnGroupMaker groupMaker))
        {
            return new Composition(adjustedPoints, null, "在点数 " + adjustedPoints + " 下没有可用的 Combat PawnGroupMaker");
        }

        List<RimRunnerRaidMember> members = [];
        foreach (PawnGenOptionWithXenotype option in PawnGroupMakerUtility.ChoosePawnGenOptionsByPoints(
                     adjustedPoints, groupMaker.options, parms))
        {
            members.Add(new RimRunnerRaidMember(option.Option.kind, option.Xenotype));
        }

        return members.Count == 0
            ? new Composition(adjustedPoints, null,
                "在点数 " + adjustedPoints + " 下构成清单为空（vanilla 会输出错误级日志，其后不产出 pawn）")
            : new Composition(adjustedPoints, members, null);
    }

    private static RimRunnerRaidSquad LogSquad(string label, Faction faction, int seed, Composition composition, string note)
    {
        List<RimRunnerRaidMember> members = composition.Members!;
        RimRunnerEngineLog.Info("袭击构成 " + label
                                + " faction=" + faction.def.defName + note
                                + " points=" + composition.AdjustedPoints
                                + " seed=" + seed
                                + " " + FormatComposition(members));
        return new RimRunnerRaidSquad(label, composition.AdjustedPoints, faction, seed, members, GenTicks.TicksAbs);
    }

    private static void RequireSquadInputs(RimRunnerContext ctx, string label)
    {
        if (ctx?.Map == null)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " GenerateSquad 需要已装配的 ctx.Map（tile 与星球层系数来自该地图）");
        }

        if (string.IsNullOrEmpty(label))
        {
            throw new ArgumentException("袭击构成 label 不能为空", nameof(label));
        }
    }

    private static int SquadSeed(string label, float points) =>
        Gen.HashCombineInt(GenText.StableStringHash(label), points.GetHashCode());

    /// <summary>按指定的兵种名单算一支敌队的构成。</summary>
    /// <remarks>缺省 seed 由 label 与名单定，名单一经改动，全体即成另一批人；perMemberSeed 见该参数。xenotype 逐成员施加；allowHidden 与 allowNonHumanlike 只放开对应的一道。</remarks>
    /// <param name="ctx">用例上下文，须已装配地图。</param>
    /// <param name="kinds">兵种名单，顺序即成员顺序。</param>
    /// <param name="xenotype">整队施加的异种；null 不强制。</param>
    /// <param name="factionDef">指定的派系，须过可用判据。</param>
    /// <param name="label">队标签，定 seed 与「袭击构成」日志。</param>
    /// <param name="allowHidden">隐藏派系开关：放开隐藏派系一道；缺省挡住。</param>
    /// <param name="allowNonHumanlike">非人形派系开关：放开非人形派系一道；缺省挡住。</param>
    /// <param name="perMemberSeed">逐成员定 seed；第 k 个某兵种只由 label、兵种与 k 定。</param>
    /// <exception cref="ArgumentException">xenotype 非 null 而名单含非人形兵种时抛。</exception>
    public static RimRunnerRaidSquad GenerateSquad(RimRunnerContext ctx, IReadOnlyList<PawnKindDef> kinds,
        XenotypeDef? xenotype, FactionDef factionDef, string label,
        bool allowHidden = false, bool allowNonHumanlike = false, bool perMemberSeed = false)
    {
        if (ctx?.Map == null)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " GenerateSquad 需要已装配的 ctx.Map");
        }

        if (kinds == null || kinds.Count == 0)
        {
            throw new ArgumentException("兵种名单不能为空", nameof(kinds));
        }

        if (factionDef == null)
        {
            throw new ArgumentNullException(nameof(factionDef), "按名单算构成须钉派系");
        }

        if (string.IsNullOrEmpty(label))
        {
            throw new ArgumentException("袭击构成 label 不能为空", nameof(label));
        }

        float factor = xenotype == null ? 1f : xenotype.combatPowerFactor;
        float points = 0f;
        int labelHash = GenText.StableStringHash(label);
        int seed = labelHash;
        Dictionary<PawnKindDef, int> kindOrdinals = [];
        List<RimRunnerRaidMember> members = new(kinds.Count);
        for (int i = 0; i < kinds.Count; i++)
        {
            PawnKindDef kind = kinds[i] ?? throw new ArgumentException("兵种名单第 " + i + " 项为 null", nameof(kinds));
            if (xenotype != null && kind.RaceProps?.Humanlike != true)
            {
                throw new ArgumentException(RimRunnerLog.Prefix + " GenerateSquad 的 xenotype " + xenotype.defName
                    + " 对整队每个成员施加，名单第 " + i + " 项 " + kind.defName + " 不是人形", nameof(xenotype));
            }

            points += kind.combatPower * factor;
            int kindHash = GenText.StableStringHash(kind.defName);
            seed = Gen.HashCombineInt(seed, kindHash);
            int? memberSeed = null;
            if (perMemberSeed)
            {
                kindOrdinals.TryGetValue(kind, out int ordinal);
                kindOrdinals[kind] = ordinal + 1;
                memberSeed = Gen.HashCombineInt(Gen.HashCombineInt(labelHash, kindHash), ordinal);
            }

            members.Add(new RimRunnerRaidMember(kind, xenotype, memberSeed));
        }

        Faction faction = PinnedRaidFaction(factionDef, points, allowHidden, allowNonHumanlike);
        RimRunnerEngineLog.Info("袭击构成 " + label
                                + " faction=" + faction.def.defName + "(pinned)"
                                + " points=" + points + "(名单合计)"
                                + " seed=" + seed + (perMemberSeed ? "(逐成员)" : "")
                                + " " + FormatComposition(members));
        return new RimRunnerRaidSquad(label, points, faction, seed, members, GenTicks.TicksAbs);
    }

    /// <summary>按 squad 生成 pawn，落在地图边缘并编进 LordJob_AssaultColony。</summary>
    /// <returns>本次生成的 pawn，按 squad 成员顺序。</returns>
    /// <exception cref="InvalidOperationException">地图不可外出时抛。</exception>
    public static IReadOnlyList<Pawn> Deploy(RimRunnerContext ctx, RimRunnerRaidSquad squad, Map map)
    {
        RequireDeployInputs(ctx, squad, map);
        if (!map.CanEverExit)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " [" + squad.Label + "] Deploy 的地图不可外出"
                + "（袋装图或 Biome.canExitMap=false）。边缘步入在不可外出的地图上不成立");
        }

        return DeployCore(ctx, squad, map, pawns =>
        {
            RimRunnerFault.Require(RCellFinder.TryFindRandomPawnEntryCell(
                    out IntVec3 spawnCenter, map, CellFinder.EdgeRoadChance_Hostile),
                NoEntryCellReason, "[" + squad.Label + "] " + RimRunnerCellBlockers.MapLabel(map));

            Rot4 spawnRotation = Rot4.FromAngleFlat((map.Center - spawnCenter).AngleFlat);
            for (int i = 0; i < pawns.Count; i++)
            {
                IntVec3 loc = CellFinder.RandomClosewalkCellNear(spawnCenter, map, 8);
                RimRunnerFault.Require(loc.IsValid, NoSpreadCellReason, "[" + squad.Label + "] 进入格 " + spawnCenter);

                GenSpawn.Spawn(pawns[i], loc, map, spawnRotation);
                RimRunnerPawns.RunSpawnHook(pawns[i]);
            }

            return new LordJob_AssaultColony(
                squad.Faction,
                canKidnap: false,
                canTimeoutOrFlee: false,
                sappers: false,
                useAvoidGridSmart: false,
                canSteal: false);
        });
    }

    /// <summary>按格部署：按 squad 构成生成 pawn，逐个落在给定格并编进给定编队，返回这一批。</summary>
    /// <remarks>落格当刻即以 lordJob 建编队，成员随即按 lordJob 选 job。第 i 个成员落在 cells[i]。任一格不满足即抛，保持原格、不重试。lordJob 为 null 抛。</remarks>
    public static IReadOnlyList<Pawn> Deploy(RimRunnerContext ctx, RimRunnerRaidSquad squad, Map map,
        IReadOnlyList<IntVec3> cells, LordJob lordJob)
    {
        RequireDeployInputs(ctx, squad, map);
        if (lordJob == null)
        {
            throw new ArgumentNullException(nameof(lordJob), "按格落点须给编队");
        }

        string? rejection = CellsRejection(squad, map, cells)?.Text;
        if (rejection != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " [" + squad.Label + "] 按格落点：" + rejection);
        }

        return DeployCore(ctx, squad, map, pawns =>
        {
            for (int i = 0; i < pawns.Count; i++)
            {
                GenSpawn.Spawn(pawns[i], cells[i], map);
                RimRunnerPawns.RunSpawnHook(pawns[i]);
            }

            return lordJob;
        });
    }

    internal static Rejection? CellsRejection(RimRunnerRaidSquad squad, Map map, IReadOnlyList<IntVec3>? cells)
    {
        if (cells == null)
        {
            return new Rejection { Kind = RejectionKind.Null };
        }

        if (cells.Count != squad.Members.Count)
        {
            return new Rejection { Kind = RejectionKind.CountMismatch, Count = cells.Count, Expected = squad.Members.Count };
        }

        HashSet<IntVec3> seen = [];
        for (int i = 0; i < cells.Count; i++)
        {
            IntVec3 c = cells[i];
            if (!c.InBounds(map))
            {
                return new Rejection { Kind = RejectionKind.OutOfBounds, Index = i, Cell = c };
            }

            if (!c.Standable(map))
            {
                return new Rejection { Kind = RejectionKind.Unstandable, Index = i, Cell = c };
            }

            if (c.GetFirstPawn(map) is { } occupant)
            {
                return new Rejection { Kind = RejectionKind.Occupied, Index = i, Cell = c, Detail = occupant.LabelShort };
            }

            if (!seen.Add(c))
            {
                return new Rejection { Kind = RejectionKind.Duplicate, Index = i, Cell = c };
            }
        }

        return null;
    }

    internal sealed class Rejection
    {
        internal required RejectionKind Kind;
        internal int Index = -1;
        internal IntVec3 Cell;
        internal int Count;
        internal int Expected;
        internal string? Detail;

        internal string Text => Kind switch
        {
            RejectionKind.Null => "cells 为 null",
            RejectionKind.CountMismatch => "格数 " + Count + " ≠ 成员数 " + Expected,
            RejectionKind.OutOfBounds => "第 " + Index + " 格 " + Cell + " 出界",
            RejectionKind.Unstandable => "第 " + Index + " 格 " + Cell + " 不可站",
            RejectionKind.Occupied => "第 " + Index + " 格 " + Cell + " 已有 pawn " + Detail,
            RejectionKind.Duplicate => "第 " + Index + " 格 " + Cell + " 与前面的格重复",
        };
    }

    internal enum RejectionKind
    {
        Null,
        CountMismatch,
        OutOfBounds,
        Unstandable,
        Occupied,
        Duplicate,
    }

    private static void RequireDeployInputs(RimRunnerContext ctx, RimRunnerRaidSquad squad, Map map)
    {
        if (ctx == null)
        {
            throw new ArgumentNullException(nameof(ctx));
        }

        if (squad == null)
        {
            throw new ArgumentNullException(nameof(squad));
        }

        if (map == null)
        {
            throw new ArgumentNullException(nameof(map));
        }
    }

    [RimRunnerDirectGeneration("袭击部署：整队按 squad 构成生成，生成随机属于 squad，不经单个 pawn 生成根")]
    private static IReadOnlyList<Pawn> DeployCore(RimRunnerContext ctx, RimRunnerRaidSquad squad, Map map,
        Func<List<Pawn>, LordJob> place)
    {
        HashSet<Pawn> worldBefore = [.. Find.WorldPawns.AllPawnsAliveOrDead];
        HashSet<Pawn> orphansBefore = RimRunnerOrphanSweep.Snapshot();
        List<Pawn> pawns = new(squad.Members.Count);
        bool tracked = false;
        try
        {
            using (RimRunnerRandControl.Seeded(squad.Seed))
            {
                using (RimRunnerNameUseIsolation.Enter())
                using (RimRunnerTwelfthPin.Enter(squad.ComposedAtTicksAbs))
                {
                    for (int i = 0; i < squad.Members.Count; i++)
                    {
                        RimRunnerRaidMember member = squad.Members[i];
                        Pawn pawn;
                        using (member.Seed is int memberSeed ? RimRunnerRandControl.Seeded(memberSeed) : null)
                        using (RimRunnerThingIdSeed.Enter(member.Seed ?? Gen.HashCombineInt(squad.Seed, i)))
                        {
                            pawn = RimRunnerPawns.GenerateRaidMemberPawn(member.Kind, squad.Faction, member.Xenotype);
                        }

                        if (pawn.Faction != squad.Faction)
                        {
                            pawn.SetFaction(squad.Faction);
                        }

                        if (pawn.DevelopmentalStage.Adult()) RimRunnerPawns.SetFixedAdultAge(pawn);
                        RimRunnerPawns.MarkRaidMember(pawn);
                        pawn.mindState?.canFleeIndividual = false;

                        RimRunnerObservationBuffer.ObservePawn(pawn);
                        pawns.Add(pawn);
                    }
                }

                LordJob job = place(pawns);
                LordMaker.MakeNewLord(squad.Faction, job, map, pawns);
            }

            for (int i = 0; i < pawns.Count; i++)
            {
                ctx.TrackPawn(pawns[i]);
            }

            tracked = true;
            return pawns;
        }
        finally
        {
            if (!tracked)
            {
                for (int i = 0; i < pawns.Count; i++)
                {
                    RimRunnerPawns.Cleanup(pawns[i]);
                }
            }

            RimRunnerPawns.DiscardWorldPawnsAddedSince(worldBefore);
            RimRunnerOrphanSweep.SweepAddedSince(orphansBefore);
        }
    }

    /// <summary>敌队指纹：一批敌队的逐成员生成指纹，两次部署相等即同构。</summary>
    /// <remarks>逐成员记兵种、异种、性别、名字、背景、trait、技能、装备、衣物、物品栏与 hediff；须在改动这一批之前取。同一 squad 再次部署（上一批仍在场、跨月、换落点入口）逐成员指纹相等。</remarks>
    public static string Fingerprint(IReadOnlyList<Pawn> pawns)
    {
        StringBuilder sb = new();
        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn p = pawns[i];
            sb.Append('#').Append(i).Append(' ').Append(p.kindDef.defName)
                .Append(" xeno=").Append(p.genes?.Xenotype?.defName ?? "-")
                .Append(" gender=").Append(p.gender)
                .Append(" name=").Append(p.Name?.ToStringFull ?? "-")
                .Append(" story=").Append(p.story?.Childhood?.defName ?? "-")
                .Append('/').Append(p.story?.Adulthood?.defName ?? "-")
                .Append(" traits=").Append(JoinFingerprint(p.story?.traits?.allTraits.Select(t => t.def.defName + ":" + t.Degree)))
                .Append(" skills=").Append(JoinFingerprint(p.skills?.skills.Select(s => s.def.defName + ":" + s.Level + ":" + s.passion)))
                .Append(" equip=").Append(JoinFingerprint(p.equipment?.AllEquipmentListForReading.Select(DescribeThing)))
                .Append(" apparel=").Append(JoinFingerprint(p.apparel?.WornApparel.Select(DescribeThing)))
                .Append(" inv=").Append(JoinFingerprint(p.inventory?.innerContainer.Select(t => DescribeThing(t) + "x" + t.stackCount)))
                .Append(" hediffs=").Append(JoinFingerprint(p.health.hediffSet.hediffs.Select(h =>
                    h.def.defName + "@" + (h.Part?.def.defName ?? "whole"))))
                .Append('\n');
        }

        return sb.ToString();
    }

    private static string DescribeThing(Thing t) =>
        t.def.defName + (t.Stuff == null ? "" : "(" + t.Stuff.defName + ")");

    private static string JoinFingerprint(IEnumerable<string>? parts) =>
        parts == null ? "-" : "[" + string.Join(",", parts) + "]";

    private static Faction PinnedRaidFaction(FactionDef def, float points, bool allowHidden, bool allowNonHumanlike)
    {
        RequireFactionInSave(def);
        string? rejection = PinnedRejection(def, points, allowHidden, allowNonHumanlike);
        if (rejection != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + rejection);
        }

        return Find.FactionManager.FirstFactionOfDef(def)!;
    }

    internal const string NoEntryCellReason = "地图边缘找不到进入格";

    internal const string NoSpreadCellReason = "进入格周围 8 格内无法散开";

    internal const string NoUsableFactionReason = "存档里没有满足袭击派系判据的人形敌对派系";

    internal const string PinnedFactionAbsentReason = "指定的袭击派系不在本存档里";

    private static void RequireFactionInSave(FactionDef def)
    {
        RimRunnerFault.Require(Find.FactionManager.FirstFactionOfDef(def) != null, PinnedFactionAbsentReason, def.defName);
    }

    internal static string? PinnedRejection(FactionDef def, float points, bool allowHidden, bool allowNonHumanlike)
    {
        Faction? faction = Find.FactionManager.FirstFactionOfDef(def);
        if (faction == null)
        {
            return "指定的袭击派系 " + def.defName + " 在本存档里不存在";
        }

        string? rejection = RaidFactionRejection(faction, points, allowHidden, allowNonHumanlike);
        return rejection == null
            ? null
            : "指定的袭击派系 " + def.defName + " 在点数 " + points + " 下不满足袭击派系判据：" + rejection;
    }

    private static string? RaidFactionRejection(Faction f, float points, bool allowHidden, bool allowNonHumanlike)
    {
        if (f.Hidden && !allowHidden)
        {
            return "隐藏派系（allowHidden 未开）";
        }

        if (f.temporary)
        {
            return "临时派系";
        }

        if (f.defeated)
        {
            return "已败";
        }

        if (f.deactivated)
        {
            return "已停用";
        }

        if (!f.def.humanlikeFaction && !allowNonHumanlike)
        {
            return "非人形派系（allowNonHumanlike 未开）";
        }

        if (!f.HostileTo(Faction.OfPlayer))
        {
            return "对玩家不敌对";
        }

        if (f.def.raidsForbidden)
        {
            return "raidsForbidden";
        }

        if (f.def.pawnGroupMakers == null
            || !f.def.pawnGroupMakers.Any(maker => maker.kindDef == PawnGroupKindDefOf.Combat))
        {
            return "没有 Combat 组";
        }

        float minPoints = f.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat);
        if (points < minPoints)
        {
            return "点数低于该派系 Combat 组下限 " + minPoints;
        }

        if (f.def.disallowedRaidStrategies.NotNullAndContains(RaidStrategyDefOf.ImmediateAttack))
        {
            return "禁即时突击";
        }

        return null;
    }

    private static float AdjustRaidPoints(
        float points,
        PawnsArrivalModeDef arrival,
        RaidStrategyDef strategy,
        Faction faction,
        PawnGroupKindDef groupKind,
        IIncidentTarget target)
    {
        float adjusted = IncidentWorker_Raid.AdjustedRaidPoints(
            points, arrival, strategy, faction, groupKind, target, ageRestriction: null);
        return Mathf.Max(adjusted, faction.def.MinPointsToGeneratePawnGroup(groupKind));
    }

    private static PawnGroupMakerParms MakeGroupParms(
        Map map,
        Faction faction,
        float adjustedPoints,
        int seed,
        PawnGroupKindDef groupKind,
        RaidStrategyDef strategy)
    {
        return new PawnGroupMakerParms
        {
            groupKind = groupKind,
            faction = faction,
            points = adjustedPoints,
            tile = map.Tile,
            seed = seed,
            raidStrategy = strategy,
            raidAgeRestriction = null,
            generateFightersOnly = false,
            dontUseSingleUseRocketLaunchers = false
        };
    }

    private static string FormatComposition(List<RimRunnerRaidMember> members)
    {
        Dictionary<string, int> counts = [];
        for (int i = 0; i < members.Count; i++)
        {
            RimRunnerRaidMember member = members[i];
            string key = member.Kind.defName + "×" + (member.Xenotype == null ? "null" : member.Xenotype.defName);
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        List<string> parts = new(counts.Count);
        foreach (KeyValuePair<string, int> pair in counts)
        {
            parts.Add(pair.Key + "×" + pair.Value);
        }

        parts.Sort(StringComparer.Ordinal);
        return "n=" + members.Count + " [" + string.Join(" ", parts) + "]";
    }
}
