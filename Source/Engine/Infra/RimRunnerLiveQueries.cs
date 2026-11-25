using System.Text.Json;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    internal static string GetStatExplanation(Pawn pawn, StatDef stat, float value)
    {
        return stat.Worker.GetExplanationFull(
            StatRequest.For(pawn), stat.toStringNumberSense, value);
    }

    private static LiveResponse QueryPawnsRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly();
        if (Current.ProgramState != ProgramState.Playing)
        {
            return ErrorResponse(503, "游戏当前不在 Playing 状态");
        }

        return ExecuteOnMainThread(QueryPawns);
    }

    private static LiveResponse QueryGizmosRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly();
        if (Current.ProgramState != ProgramState.Playing)
        {
            return ErrorResponse(503, "游戏当前不在 Playing 状态");
        }

        return ExecuteOnMainThread(QueryGizmos);
    }

    private static LiveResponse QueryGizmos()
    {
        if (RimRunnerCurrentMapProbe.EngineCurrentMap() == null)
        {
            return ErrorResponse(503, "没有当前地图");
        }

        IReadOnlyList<RimRunnerGizmoButton> bar = RimRunnerGizmos.Bar();
        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Gizmos, writer =>
        {
            AppendStrings(writer, RimRunnerLiveContract.Gizmos.Selected, [.. Find.Selector.SelectedObjects.Select(RimRunnerDrivePawns.Describe)]);
            writer.WriteStartArray(RimRunnerLiveContract.Gizmos.Items);
            foreach (RimRunnerGizmoButton button in bar)
            {
                writer.WriteStartObject();
                writer.WriteNumber(RimRunnerLiveContract.Gizmos.Index, button.Index);
                writer.WriteString(RimRunnerLiveContract.Gizmos.Label, button.Label);
                writer.WriteString(RimRunnerLiveContract.Gizmos.Type, button.Gizmo.GetType().Name);
                writer.WriteBoolean(RimRunnerLiveContract.Gizmos.Visible, button.Visible);
                writer.WriteBoolean(RimRunnerLiveContract.Gizmos.Disabled, button.Disabled);
                if (button.DisabledReason != null)
                {
                    writer.WriteString(RimRunnerLiveContract.Gizmos.DisabledReason, button.DisabledReason);
                }

                if (button.ToggleActive is { } active)
                {
                    writer.WriteBoolean(RimRunnerLiveContract.Gizmos.Toggle, active);
                }

                writer.WriteNumber(RimRunnerLiveContract.Gizmos.Group, button.GroupSize);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }));
    }

    private static LiveResponse QueryMenuRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly();
        if (Current.ProgramState != ProgramState.Playing)
        {
            return ErrorResponse(503, "游戏当前不在 Playing 状态");
        }

        return ExecuteOnMainThread(QueryMenu);
    }

    private static LiveResponse QueryMenu()
    {
        FloatMenu? menu = RimRunnerFloatMenus.Top();
        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Menu, writer =>
        {
            writer.WriteBoolean(RimRunnerLiveContract.Menu.Open, menu != null);
            if (menu == null)
            {
                return;
            }

            List<FloatMenuOption> options = RimRunnerFloatMenus.Options(menu);
            writer.WriteStartArray(RimRunnerLiveContract.Menu.Items);
            for (int i = 0; i < options.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteNumber(RimRunnerLiveContract.Menu.Index, i + 1);
                writer.WriteString(RimRunnerLiveContract.Menu.Label, options[i].Label);
                writer.WriteBoolean(RimRunnerLiveContract.Menu.Disabled, options[i].Disabled);
                writer.WriteBoolean(RimRunnerLiveContract.Menu.Shown, RimRunnerFloatMenus.ClickPoint(menu, options[i]) != null);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }));
    }

    private static LiveResponse QueryWindowsRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly();
        if (Current.ProgramState != ProgramState.Playing)
        {
            return ErrorResponse(503, "游戏当前不在 Playing 状态");
        }

        return ExecuteOnMainThread(QueryWindows);
    }

    private static LiveResponse QueryWindows()
    {
        IReadOnlyList<Window> windows = RimRunnerDriveWindowClose.Listed();
        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Windows, writer =>
        {
            writer.WriteStartArray(RimRunnerLiveContract.Windows.Items);
            for (int i = 0; i < windows.Count; i++)
            {
                Window window = windows[i];
                writer.WriteStartObject();
                writer.WriteNumber(RimRunnerLiveContract.Windows.Index, i + 1);
                writer.WriteString(RimRunnerLiveContract.Windows.Type, window.GetType().Name);
                if (!window.optionalTitle.NullOrEmpty())
                {
                    writer.WriteString(RimRunnerLiveContract.Windows.Title, window.optionalTitle);
                }

                writer.WriteString(RimRunnerLiveContract.Windows.Layer, window.layer.ToString());
                writer.WriteBoolean(RimRunnerLiveContract.Windows.ForcePause, window.forcePause);
                writer.WriteBoolean(RimRunnerLiveContract.Windows.CloseOnCancel, window.closeOnCancel);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }));
    }

    private static LiveResponse QueryStatRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly(RimRunnerLiveContract.Stat.Pawn, RimRunnerLiveContract.Stat.StatName);
        string pawnNick = p.RequireText(RimRunnerLiveContract.Stat.Pawn, "昵称或 ThingID");
        string statName = p.RequireText(RimRunnerLiveContract.Stat.StatName, "StatDef 名");

        if (Current.ProgramState != ProgramState.Playing)
        {
            return ErrorResponse(503, "游戏当前不在 Playing 状态");
        }

        return ExecuteOnMainThread(() => QueryStat(pawnNick, statName));
    }

    private static LiveResponse QueryHediffsRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly(RimRunnerLiveContract.Hediffs.Pawn);
        string pawnKey = p.RequireText(RimRunnerLiveContract.Hediffs.Pawn, "昵称或 ThingID");

        if (Current.ProgramState != ProgramState.Playing)
        {
            return ErrorResponse(503, "游戏当前不在 Playing 状态");
        }

        return ExecuteOnMainThread(() => QueryHediffs(pawnKey));
    }

    internal static LiveResponse QueryPawns()
    {
        Map map = RimRunnerIsolatedMap.LiveMap;
        if (map == null)
        {
            return ErrorResponse(503, "没有当前地图");
        }

        IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Pawns, writer =>
            AppendPawnArray(writer, RimRunnerLiveContract.Pawns.Items, pawns)));
    }

    private static void AppendPawnArray(Utf8JsonWriter writer, string name, IReadOnlyList<Pawn> pawns)
    {
        writer.WriteStartArray(name);
        foreach (Pawn pawn in pawns)
        {
            writer.WriteStartObject();
            writer.WriteString(RimRunnerLiveContract.Pawns.Nick, GetPawnNick(pawn));
            writer.WriteString(RimRunnerLiveContract.Pawns.Name, pawn.Name?.ToStringFull);
            writer.WriteString(RimRunnerLiveContract.Pawns.Id, pawn.ThingID);
            writer.WriteString(RimRunnerLiveContract.Pawns.Faction, pawn.Faction?.def?.defName);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    internal static LiveResponse QueryStat(string pawnNick, string statName)
    {
        Map map = RimRunnerIsolatedMap.LiveMap;
        if (map == null)
        {
            return ErrorResponse(503, "没有当前地图");
        }

        IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
        Pawn? pawn = FindSpawnedPawn(pawns, pawnNick);
        if (pawn == null)
        {
            return ErrorWithAvailablePawns(404, "找不到 pawn：" + pawnNick, pawns);
        }

        StatDef stat = DefDatabase<StatDef>.GetNamed(statName, false);
        if (stat == null)
        {
            return ErrorResponse(404, "找不到 StatDef：" + statName);
        }

        float value = pawn.GetStatValue(stat);
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return ErrorResponse(500, "stat value 不是有限数字：" + statName);
        }

        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Stat, writer =>
        {
            writer.WriteString(RimRunnerLiveContract.Stat.Pawn, GetPawnNick(pawn));
            writer.WriteString(RimRunnerLiveContract.Stat.StatName, stat.defName);
            writer.WriteNumber(RimRunnerLiveContract.Stat.Value, value);
            writer.WriteString(RimRunnerLiveContract.Stat.Explanation, GetStatExplanation(pawn, stat, value));
            writer.WriteString(RimRunnerLiveContract.Stat.Description, stat.description ?? string.Empty);
        }));
    }

    private static LiveResponse QueryHediffs(string pawnKey)
    {
        Map map = RimRunnerIsolatedMap.LiveMap;
        if (map == null)
        {
            return ErrorResponse(503, "没有当前地图");
        }

        IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
        Pawn? pawn = FindSpawnedPawn(pawns, pawnKey);
        if (pawn == null)
        {
            return ErrorWithAvailablePawns(404, "找不到 pawn：" + pawnKey, pawns);
        }

        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Hediffs, writer =>
        {
            writer.WriteString(RimRunnerLiveContract.Hediffs.Pawn, GetPawnNick(pawn));
            writer.WriteNumber(RimRunnerLiveContract.Hediffs.PainTotal, pawn.health.hediffSet.PainTotal);
            writer.WriteStartArray(RimRunnerLiveContract.Hediffs.Items);
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            foreach (Hediff h in hediffs)
            {
                writer.WriteStartObject();
                writer.WriteString(RimRunnerLiveContract.Hediffs.Def, h.def.defName);
                writer.WriteNumber(RimRunnerLiveContract.Hediffs.Severity, h.Severity);
                writer.WriteString(RimRunnerLiveContract.Hediffs.Part, h.Part?.def.defName);
                writer.WriteNumber(RimRunnerLiveContract.Hediffs.PainOffset, h.PainOffset);
                writer.WriteBoolean(RimRunnerLiveContract.Hediffs.IsBad, h.def.isBad);
                HediffComp_Disappears disappears = h.TryGetComp<HediffComp_Disappears>();
                if (disappears == null)
                {
                    writer.WriteNull(RimRunnerLiveContract.Hediffs.TicksToDisappear);
                }
                else
                {
                    writer.WriteNumber(
                        RimRunnerLiveContract.Hediffs.TicksToDisappear, disappears.ticksToDisappear);
                }

                writer.WriteStartArray(RimRunnerLiveContract.Hediffs.StatEntries);
                AppendStageStatEntries(writer, h);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString(RimRunnerLiveContract.Hediffs.ProductDiagnostics,
                RimRunnerProduct.Capabilities.DescribePawn(pawn));
        }));
    }

    private static void AppendStageStatEntries(Utf8JsonWriter writer, Hediff h)
    {
        HediffStage stage = h.CurStage;
        if (stage == null) return;
        foreach (StatDrawEntry entry in HediffStatsUtility.SpecialDisplayStats(stage, h))
        {
            if (entry == null) continue;
            writer.WriteStartObject();
            writer.WriteString(RimRunnerLiveContract.Hediffs.EntryLabel, entry.LabelCap);
            writer.WriteString(RimRunnerLiveContract.Hediffs.EntryValue, entry.ValueString);
            writer.WriteEndObject();
        }
    }

    internal static string? GetPawnNick(Pawn pawn)
    {
        return pawn.Name is NameTriple nameTriple ? nameTriple.Nick : pawn.Name?.ToStringShort;
    }

    internal static Pawn? FindSpawnedPawn(IReadOnlyList<Pawn> pawns, string pawnKey)
    {
        return pawns.FirstOrDefault(candidate =>
            string.Equals(GetPawnNick(candidate), pawnKey, StringComparison.Ordinal)
            || string.Equals(candidate.ThingID, pawnKey, StringComparison.Ordinal));
    }
}
