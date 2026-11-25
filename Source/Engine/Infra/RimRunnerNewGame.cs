using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RimWorld.Planet;
using UnityEngine.SceneManagement;

namespace RimRunner;

public static class RimRunnerNewGame
{
    private static RimRunnerNewGameDescriptor? pending;

    private static bool randStatePushed;

    private static PendingReload? reload;

    private sealed class PendingReload
    {
        internal required string SavePath;
        internal required string MetadataPath;
        internal required Action<Utf8JsonWriter> MetadataMembers;
    }

    public static bool Active => pending != null;

    internal static void BeginFromCommandLine(string command, string? runId)
    {
        GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.NewGame, out string descriptorPath);
        RimRunnerNewGameDescriptor descriptor;
        try
        {
            descriptor = RimRunnerNewGameDescriptor.LoadOrThrow(descriptorPath);
        }
        catch (Exception exception)
        {
            Patch_UIRootEntry_RimRunnerAutoLoad.NoteStartupFault("建档描述符读入失败", exception);
            RimRunnerSession.WriteTerminalError(command, runId, "建档描述符读入失败", exception);
            return;
        }

        FileInfo autostart = SaveGameFilesUtility.GetAutostartSaveFile();
        if (autostart != null)
        {
            string message = "无法建档：存档目录中存在 autostart 档（" + autostart.FullName + "），该档会在建档之前被载入";
            Patch_UIRootEntry_RimRunnerAutoLoad.NoteStartupFault(message);
            RimRunnerSession.WriteTerminalError(command, runId, message);
            return;
        }

        if (!RimRunnerGameComponent.RequestBuildSaveAfterSceneLoad(runId, out string resolvedRunId))
        {
            return;
        }

        pending = descriptor;
        RimRunnerEngineLog.Info("建档：预设 " + descriptor.Preset
                                + "、存档名 " + descriptor.SaveName + "、seed " + descriptor.Seed
                                + "；描述符 " + descriptor.SourcePath);

        LongEventHandler.QueueLongEvent(
            () => SceneManager.LoadScene("Play"),
            "LoadingLongEvent",
            doAsynchronously: false,
            exception =>
            {
                pending = null;
                RimRunnerGameComponent.ClearAutoRunRequest();
                Patch_UIRootEntry_RimRunnerAutoLoad.NoteStartupFault("建档切场景失败", exception);
                RimRunnerSession.WriteTerminalError(
                    command, resolvedRunId, "建档切场景失败", exception);
            });
    }

    internal static bool SetupReplacement()
    {
        RimRunnerNewGameDescriptor? descriptor = pending;
        if (descriptor == null)
        {
            return true;
        }

        Rand.PushState(GenText.StableStringHash(descriptor.Seed));
        randStatePushed = true;

        if (descriptor.Storyteller.RuntimeGate)
        {
            DebugSettings.enableStoryteller = false;
        }

        Current.ProgramState = ProgramState.Entry;
        Game.ClearCaches();
        Current.Game = new Game();
        Current.Game.InitData = new GameInitData();
        Current.Game.Scenario = BuildScenario(descriptor);
        Find.Scenario.PreConfigure();
        Current.Game.storyteller = BuildStoryteller(descriptor);
        Current.Game.World = WorldGenerator.GenerateWorld(
            descriptor.World.PlanetCoverage,
            descriptor.Seed,
            ParseEnum<OverallRainfall>(descriptor.World.Rainfall, "world.rainfall"),
            ParseEnum<OverallTemperature>(descriptor.World.Temperature, "world.temperature"),
            ParseEnum<OverallPopulation>(descriptor.World.Population, "world.population"),
            ParseEnum<LandmarkDensity>(descriptor.World.LandmarkDensity, "world.landmarkDensity"));
        ChooseStartingTile(descriptor);
        Find.GameInitData.mapSize = descriptor.Map.Size;
        ApplyClassicIdeo(descriptor);
        Find.Scenario.PostIdeoChosen();
        return false;
    }

    internal static void ReleaseRandState()
    {
        if (!randStatePushed)
        {
            return;
        }

        randStatePushed = false;
        Rand.PopState();
    }

    internal static void FinishAndSave(string command, string? runId)
    {
        RimRunnerNewGameDescriptor? descriptor = pending;
        pending = null;
        if (descriptor == null)
        {
            RimRunnerSession.WriteTerminalError(command, runId,
                "建档收尾时建档描述符已缺失");
            return;
        }

        try
        {
            SettlePendingMonolithActivation();
            QuiesceStudiables();
            QuiescePendingMonolithDialog();
            QuiescePendingAnomalyTimers();
            ApplyGeneratorDefOnSave(descriptor);
            string savePath = SaveGame(descriptor);
            Action<Utf8JsonWriter> metadataMembers = MetadataMembers(descriptor);
            string metadataPath = Path.ChangeExtension(savePath, null) + RimRunnerWireKeys.BuildSaveSidecar.Suffix;
            File.WriteAllText(metadataPath, MetadataBodyJson(metadataMembers, null, null, null));

            reload = new PendingReload
            {
                SavePath = savePath,
                MetadataPath = metadataPath,
                MetadataMembers = metadataMembers,
            };
            RimRunnerBuildSaveReloadCheck.Arm();
            RimRunnerGameComponent.RequestBuildSaveReload(runId);
            RimRunnerEngineLog.Info("建档已存盘：" + savePath + "；开始回载验证");
            RimRunnerGameComponent.QueueLoadGameAfterCurrentUpdate(descriptor.SaveName, exception =>
            {
                reload = null;
                RimRunnerBuildSaveReloadCheck.Disarm();
                RimRunnerGameComponent.ClearAutoRunRequest();
                RimRunnerSession.WriteTerminalError(command, runId, "建档回载失败", exception);
            });
        }
        catch (Exception exception)
        {
            RimRunnerBuildSaveReloadCheck.Disarm();
            RimRunnerSession.WriteTerminalError(command, runId, "建档收尾失败", exception);
        }
    }

    internal static void FailFromMapGeneration(Exception exception)
    {
        if (pending == null)
        {
            return;
        }

        RimRunnerRunSnapshot run = RimRunnerRunCoordinator.GetSnapshot();
        pending = null;
        ReleaseRandState();
        RimRunnerGameComponent.ClearAutoRunRequest();
        Patch_UIRootEntry_RimRunnerAutoLoad.NoteStartupFault("建档生成地图失败", exception);
        RimRunnerSession.WriteTerminalError(run.Command, run.RunId, "建档生成地图失败", exception);
    }

    internal static bool ReloadPending => reload != null;

    internal static void FinishReloadCheck(string command, string? runId)
    {
        PendingReload pendingReload = reload!;
        reload = null;
        try
        {
            int ticks = RimRunnerBuildSaveReloadCheck.Run();
            SortedDictionary<string, string[]> tickDriven = RimRunnerBuildSaveReloadCheck.TakeTickDriven();
            List<string> hits = RimRunnerBuildSaveReloadCheck.TakeHits();
            RimRunnerGameComponent.MarkWorldTouched();
            File.WriteAllText(pendingReload.MetadataPath,
                MetadataBodyJson(pendingReload.MetadataMembers, ticks, hits, tickDriven));

            RimRunnerRunCoordinator.Complete(command, runId);
            RimRunnerEngineLog.Info("建档完成：" + pendingReload.SavePath + "；回载空推 " + ticks + " tick，命中 "
                                    + hits.Count + " 项；已在暂停态等待下一步（与 load-only 同形）");
        }
        catch (Exception exception)
        {
            RimRunnerBuildSaveReloadCheck.Disarm();
            RimRunnerSession.WriteTerminalError(command, runId, "建档回载验证失败", exception);
        }
    }

    private static void ApplyClassicIdeo(RimRunnerNewGameDescriptor descriptor)
    {
        if (!descriptor.Ideo.ClassicMode)
        {
            return;
        }

        if (!ModsConfig.IdeologyActive)
        {
            RimRunnerEngineLog.Info("建档：Ideology 未启用，跳过教条设置");
            return;
        }

        FactionDef playerFactionDef = Find.FactionManager.OfPlayer.def;
        IdeoGenerationParms parms = new(playerFactionDef);
        List<CultureDef> cultures = [];
        List<CultureDef> allCultures = DefDatabase<CultureDef>.AllDefsListForReading;
        for (int i = 0; i < allCultures.Count; i++)
        {
            if (playerFactionDef.allowedCultures != null
                && playerFactionDef.allowedCultures.Contains(allCultures[i]))
            {
                cultures.Add(allCultures[i]);
            }
        }

        if (!cultures.TryRandomElement(out CultureDef culture)
            && !allCultures.TryRandomElement(out culture))
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 建档：没有 CultureDef，无法生成经典教条");
        }

        Ideo classic = IdeoGenerator.GenerateClassicIdeo(culture, parms, noExpansionIdeo: false);

        Faction.OfPlayer.ideos.SetPrimary(classic);
        List<Ideo> known = Find.IdeoManager.IdeosListForReading;
        for (int i = 0; i < known.Count; i++)
        {
            known[i].initialPlayerIdeo = false;
        }

        classic.initialPlayerIdeo = true;
        Find.IdeoManager.Add(classic);

        List<Faction> factions = Find.FactionManager.AllFactionsListForReading;
        for (int i = 0; i < factions.Count; i++)
        {
            if (factions[i].ideos != null)
            {
                factions[i].ideos.RemoveAll();
                factions[i].ideos.SetPrimary(classic);
            }
        }

        Find.IdeoManager.classicMode = true;
        Find.IdeoManager.RemoveUnusedStartingIdeos();

        RimRunnerEngineLog.Info(
            "建档：已置经典教条 " + classic.name
            + "（文化 " + culture.defName + "、meme " + (classic.memes?.Count ?? 0) + " 条）");
    }

    private static void QuiesceStudiables()
    {
        List<Map> maps = Find.Maps;
        int disabled = 0;
        for (int m = 0; m < maps.Count; m++)
        {
            List<Thing> things = maps[m].listerThings.AllThings;
            for (int i = 0; i < things.Count; i++)
            {
                CompStudiable studiable = things[i].TryGetComp<CompStudiable>();
                if (studiable == null || !studiable.studyEnabled)
                {
                    continue;
                }

                studiable.SetStudyEnabled(false);
                disabled++;
            }
        }

        RimRunnerEngineLog.Info("建档收尾：已关闭 " + disabled + " 件可研究物的研究开关");
    }

    private static void SettlePendingMonolithActivation()
    {
        FieldInfo tickField = AccessTools.Field(typeof(Building_VoidMonolith), "autoActivateTick") ?? throw new MissingFieldException(
                "Building_VoidMonolith 上无 autoActivateTick，"
                + "剧本安排的巨石自动激活未执行");
        List<Map> maps = Find.Maps;
        int settled = 0;
        for (int m = 0; m < maps.Count; m++)
        {
            List<Thing> things = maps[m].listerThings.AllThings;
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is not Building_VoidMonolith monolith || (int)tickField.GetValue(monolith)! <= 0)
                {
                    continue;
                }

                if (!maps[m].mapPawns.FreeColonists.TryRandomElement(out Pawn activator))
                {
                    tickField.SetValue(monolith, -99999);
                    RimRunnerEngineLog.Warn(
                        "建档收尾：地图上没有自由殖民者，巨石的自动激活只能清零而非执行完");
                    continue;
                }

                monolith.Activate(activator);
                settled++;
            }
        }

        RimRunnerEngineLog.Info("建档收尾：已就地执行完 " + settled + " 处剧本安排的巨石自动激活");
    }

    private static void QuiescePendingMonolithDialog()
    {
        FieldInfo tickField = AccessTools.Field(typeof(Building_VoidMonolith), "activatedDialogTick");
        FieldInfo pawnField = AccessTools.Field(typeof(Building_VoidMonolith), "activatorPawn");
        if (tickField == null || pawnField == null)
        {
            throw new MissingFieldException(
                "Building_VoidMonolith 上无 activatedDialogTick 或 activatorPawn："
                + "待弹的激活对话未清除");
        }

        List<Map> maps = Find.Maps;
        int cleared = 0;
        for (int m = 0; m < maps.Count; m++)
        {
            List<Thing> things = maps[m].listerThings.AllThings;
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is not Building_VoidMonolith monolith || (int)tickField.GetValue(monolith)! <= 0)
                {
                    continue;
                }

                tickField.SetValue(monolith, -99999);
                pawnField.SetValue(monolith, null);
                cleared++;
            }
        }

        RimRunnerEngineLog.Info("建档收尾：已清除 " + cleared + " 处待弹的巨石激活对话");
    }

    private static readonly string[] AnomalyPendingTickFields =
    [
        "fireGrayPallTick", "fireLevelIncidentTick",
    ];

    private static void QuiescePendingAnomalyTimers()
    {
        GameComponent_Anomaly anomaly = Find.Anomaly;
        if (anomaly == null)
        {
            RimRunnerEngineLog.Info("建档收尾：无 Anomaly 组件，不存在待触发计数器");
            return;
        }

        int cleared = 0;
        for (int i = 0; i < AnomalyPendingTickFields.Length; i++)
        {
            FieldInfo field = AccessTools.Field(
                typeof(GameComponent_Anomaly), AnomalyPendingTickFields[i]) ?? throw new MissingFieldException(
                    "GameComponent_Anomaly 上无 " + AnomalyPendingTickFields[i]
                    + "：待触发的定时事件未清除");
            if ((int)field.GetValue(anomaly)! <= 0)
            {
                continue;
            }

            field.SetValue(anomaly, -99999);
            cleared++;
        }

        RimRunnerEngineLog.Info("建档收尾：已清除 " + cleared + " 个待触发的 anomaly 定时事件");
    }

    private static Scenario BuildScenario(RimRunnerNewGameDescriptor descriptor)
    {
        ScenarioDef scenarioDef = DefDatabase<ScenarioDef>.GetNamedSilentFail(descriptor.Scenario.Base) ?? throw new FormatException(RimRunnerLog.Prefix + " scenario.base 找不到对应 ScenarioDef："
                                      + descriptor.Scenario.Base);

        Scenario scenario = scenarioDef.scenario.CopyForEditing();
        List<ScenPart> parts = PartsOf(scenario);

        if (descriptor.Scenario.RemoveParts.Count > 0)
        {
            ScenPart? startingPawns = parts.Find(part => part is ScenPart_ConfigPage_ConfigureStartingPawnsBase
                                                         && descriptor.Scenario.RemoveParts.Contains(part.GetType().Name));
            if (startingPawns != null)
            {
                throw new FormatException(
                    RimRunnerLog.Prefix + " scenario.removeParts 摘掉起始 pawn 的 " + startingPawns.GetType().Name
                    + "：该项不能摘；殖民者数由 scenario.startingPawnCount 与 scenario.pawnChoiceCount 设");
            }

            int removed = parts.RemoveAll(part => descriptor.Scenario.RemoveParts.Contains(part.GetType().Name));
            if (removed == 0)
            {
                throw new FormatException(
                    RimRunnerLog.Prefix + " scenario.removeParts 的每一项都未命中（"
                    + string.Join("、", descriptor.Scenario.RemoveParts.ToArray()) + "）；"
                    + "剧本 " + descriptor.Scenario.Base + " 实际含："
                    + string.Join("、", parts.ConvertAll(part => part.GetType().Name).ToArray()));
            }
        }

        parts.RemoveAll(part => part is ScenPart_GameStartDialog);

        if (!string.IsNullOrEmpty(descriptor.Scenario.PlayerFaction))
        {
            SetPlayerFaction(scenario, descriptor.Scenario.PlayerFaction);
        }

        if (!string.IsNullOrEmpty(descriptor.Scenario.ArriveMethod))
        {
            SetArriveMethod(parts, descriptor.Scenario.ArriveMethod);
        }

        SetPawnCounts(parts, descriptor);

        foreach (string incidentName in descriptor.Scenario.DisableIncidents)
        {
            parts.Add(BuildDisableIncidentPart(incidentName));
        }

        return scenario;
    }

    private static void SetPawnCounts(List<ScenPart> parts, RimRunnerNewGameDescriptor descriptor)
    {
        if (!descriptor.Scenario.StartingPawnCount.HasValue)
        {
            return;
        }

        ScenPart_ConfigPage_ConfigureStartingPawnsBase? target = null;
        for (int i = 0; i < parts.Count; i++)
        {
            target = parts[i] as ScenPart_ConfigPage_ConfigureStartingPawnsBase;
            if (target != null)
            {
                break;
            }
        }

        if (target == null)
        {
            throw new FormatException(
                RimRunnerLog.Prefix + " 声明 scenario.startingPawnCount，但剧本 "
                + descriptor.Scenario.Base + " 里没有配置起始 pawn 的 ScenPart，scenario.startingPawnCount 未写入");
        }

        target.pawnChoiceCount = descriptor.Scenario.PawnChoiceCount!.Value;
        ScenPart_ConfigPage_ConfigureStartingPawns standard =
        target as ScenPart_ConfigPage_ConfigureStartingPawns ?? throw new FormatException(
            RimRunnerLog.Prefix + " 剧本 " + descriptor.Scenario.Base + " 的起始 pawn part 是 "
            + target.GetType().Name + "，起始人数不在 pawnCount 上，而在该类型自己的计数列表里。"
            + "scenario.startingPawnCount 只接受 ScenPart_ConfigPage_ConfigureStartingPawns");
        standard.pawnCount = descriptor.Scenario.StartingPawnCount.Value;
    }

    private static List<ScenPart> PartsOf(Scenario scenario)
    {
        FieldInfo field = AccessTools.Field(typeof(Scenario), "parts") ?? throw new FormatException(RimRunnerLog.Prefix + " Scenario 上无 parts 字段");
        return (List<ScenPart>)field.GetValue(scenario)!;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "playerFaction")]
    private static extern ref ScenPart_PlayerFaction PlayerFactionOf(Scenario scenario);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "factionDef")]
    private static extern ref FactionDef FactionDefOf(ScenPart_PlayerFaction part);

    private static void SetPlayerFaction(Scenario scenario, string factionDefName)
    {
        FactionDef factionDef = DefDatabase<FactionDef>.GetNamedSilentFail(factionDefName) ?? throw new FormatException(RimRunnerLog.Prefix + " scenario.playerFaction 找不到对应 FactionDef："
                                      + factionDefName);
        if (!factionDef.isPlayer)
        {
            throw new FormatException(
                RimRunnerLog.Prefix + " scenario.playerFaction 的 FactionDef " + factionDefName + " 不是玩家派系（isPlayer = false）");
        }

        FactionDefOf(PlayerFactionOf(scenario)) = factionDef;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "method")]
    private static extern ref PlayerPawnsArriveMethod ArriveMethodOf(ScenPart_PlayerPawnsArriveMethod part);

    private static void SetArriveMethod(List<ScenPart> parts, string method)
    {
        PlayerPawnsArriveMethod parsed =
            ParseEnum<PlayerPawnsArriveMethod>(method, "scenario.arriveMethod");
        int touched = 0;
        foreach (ScenPart part in parts)
        {
            if (part is ScenPart_PlayerPawnsArriveMethod arrive)
            {
                ArriveMethodOf(arrive) = parsed;
                touched++;
            }
        }

        if (touched == 0)
        {
            throw new FormatException(
                RimRunnerLog.Prefix + " scenario.arriveMethod 为 " + method
                + "，但剧本里没有 ScenPart_PlayerPawnsArriveMethod 可改");
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "incident")]
    private static extern ref IncidentDef IncidentOf(ScenPart_IncidentBase part);

    private static ScenPart BuildDisableIncidentPart(string incidentDefName)
    {
        IncidentDef incident = DefDatabase<IncidentDef>.GetNamedSilentFail(incidentDefName) ?? throw new FormatException(
                RimRunnerLog.Prefix + " scenario.disableIncidents 找不到对应 IncidentDef：" + incidentDefName);

        ScenPartDef partDef = DefDatabase<ScenPartDef>.GetNamedSilentFail("DisableIncident") ?? throw new FormatException(
                RimRunnerLog.Prefix + " 无 ScenPartDef DisableIncident（vanilla core 应有）");
        ScenPart_DisableIncident part = (ScenPart_DisableIncident)Activator.CreateInstance(
            partDef.scenPartClass)!;
        part.def = partDef;
        IncidentOf(part) = incident;
        return part;
    }

    private static Storyteller BuildStoryteller(RimRunnerNewGameDescriptor descriptor)
    {
        StorytellerDef def = DefDatabase<StorytellerDef>.GetNamedSilentFail(descriptor.Storyteller.Def) ?? throw new FormatException(RimRunnerLog.Prefix + " storyteller.def 找不到对应 StorytellerDef："
                                      + descriptor.Storyteller.Def);

        DifficultyDef difficultyDef =
            DefDatabase<DifficultyDef>.GetNamedSilentFail(descriptor.Storyteller.DifficultyDef) ?? throw new FormatException(
                RimRunnerLog.Prefix + " storyteller.difficultyDef 找不到对应 DifficultyDef："
                + descriptor.Storyteller.DifficultyDef);
        Storyteller storyteller = new(def, difficultyDef);
        if (descriptor.Storyteller.Difficulty == null)
        {
            return storyteller;
        }

        if (!difficultyDef.isCustom)
        {
            DifficultyDef? custom = DefDatabase<DifficultyDef>.AllDefsListForReading.Find(def => def.isCustom);
            throw new FormatException(
                RimRunnerLog.Prefix + " storyteller.difficulty 只在 difficultyDef 是自定义难度档（isCustom）时随档存下，"
                + difficultyDef.defName + " 不是自定义难度档"
                + (custom == null ? "" : "。自定义难度档：" + custom.defName));
        }

        foreach (KeyValuePair<string, float> pair in descriptor.Storyteller.Difficulty)
        {
            FieldInfo field = AccessTools.Field(typeof(Difficulty), pair.Key) ?? throw new FormatException(
                    RimRunnerLog.Prefix + " storyteller.difficulty 里的 " + pair.Key
                    + " 不是 Difficulty 的字段");

            if (field.FieldType == typeof(float))
            {
                field.SetValue(storyteller.difficulty, pair.Value);
            }
            else if (field.FieldType == typeof(int))
            {
                field.SetValue(storyteller.difficulty, (int)pair.Value);
            }
            else if (field.FieldType == typeof(bool))
            {
                field.SetValue(storyteller.difficulty, pair.Value != 0f);
            }
            else
            {
                throw new FormatException(
                    RimRunnerLog.Prefix + " storyteller.difficulty 里的 " + pair.Key
                    + " 类型是 " + field.FieldType.Name + "，本通道只支持 float / int / bool");
            }
        }

        return storyteller;
    }

    private static void ChooseStartingTile(RimRunnerNewGameDescriptor descriptor)
    {
        BiomeDef biome = DefDatabase<BiomeDef>.GetNamedSilentFail(descriptor.StartingTile.Biome) ?? throw new FormatException(RimRunnerLog.Prefix + " startingTile.biome 找不到对应 BiomeDef："
                                      + descriptor.StartingTile.Biome);
        Hilliness hilliness = ParseEnum<Hilliness>(descriptor.StartingTile.Hilliness, "startingTile.hilliness");
        PlanetTile chosen = TileFinder.RandomSettlementTileFor(
            Find.WorldGrid.Surface, Faction.OfPlayer, mustBeAutoChoosable: true,
            candidate => Matches(candidate, biome, hilliness, descriptor));

        if (!Matches(chosen, biome, hilliness, descriptor))
        {
            Tile actual = Find.WorldGrid[chosen];
            throw new FormatException(
                RimRunnerLog.Prefix + " 找不到符合 startingTile 的落点（要求 " + descriptor.StartingTile.Biome
                + "、" + descriptor.StartingTile.Hilliness
                + (descriptor.StartingTile.RequireRiver ? "、有河" : "")
                + (descriptor.StartingTile.RequireCoast ? "、沿海" : "")
                + "）。返回的落点是 " + chosen + "（" + actual.PrimaryBiome.defName
                + "、" + actual.hilliness + "）。"
                + "星球覆盖率 " + descriptor.World.PlanetCoverage);
        }

        Find.GameInitData.startingTile = chosen;
    }

    private static bool Matches(
        PlanetTile candidate, BiomeDef biome, Hilliness hilliness,
        RimRunnerNewGameDescriptor descriptor)
    {
        Tile tile = Find.WorldGrid[candidate];
        if (tile == null || tile.PrimaryBiome != biome || tile.hilliness != hilliness)
        {
            return false;
        }

        if (descriptor.StartingTile.RequireCoast && !tile.IsCoastal)
        {
            return false;
        }

        if (descriptor.StartingTile.RequireRiver)
        {
            if (tile is not SurfaceTile surface || surface.Rivers == null || surface.Rivers.Count == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyGeneratorDefOnSave(RimRunnerNewGameDescriptor descriptor)
    {
        if (string.IsNullOrEmpty(descriptor.Map.GeneratorDefOnSave))
        {
            return;
        }

        MapGeneratorDef target =
            DefDatabase<MapGeneratorDef>.GetNamedSilentFail(descriptor.Map.GeneratorDefOnSave) ?? throw new FormatException(
                RimRunnerLog.Prefix + " map.generatorDefOnSave 找不到对应 MapGeneratorDef："
                + descriptor.Map.GeneratorDefOnSave);
        foreach (Map map in Find.Maps)
        {
            map.generatorDef = target;
        }
    }

    private static string SaveGame(RimRunnerNewGameDescriptor descriptor)
    {
        string path = GenFilePaths.FilePathForSavedGame(descriptor.SaveName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        GameDataSaveLoader.SaveGame(descriptor.SaveName);
        if (!File.Exists(path))
        {
            throw new FormatException(RimRunnerLog.Prefix + " 建档另存后存档文件不存在：" + path);
        }

        return path;
    }

    private static Action<Utf8JsonWriter> MetadataMembers(RimRunnerNewGameDescriptor descriptor)
    {
        PlanetTile startingTile = Find.GameInitData == null
            ? RimRunnerCurrentMapProbe.EngineCurrentMap()!.Tile
            : Find.GameInitData.startingTile;
        Tile startingTileData = Find.WorldGrid[startingTile];
        string biome = startingTileData.PrimaryBiome.defName;
        string hilliness = startingTileData.hilliness.ToString();
        int colonistsOnMap = RimRunnerCurrentMapProbe.EngineCurrentMap()?.mapPawns.FreeColonistsCount ?? 0;
        int forcedWorldPawns = Find.WorldPawns == null ? 0 : Find.WorldPawns.ForcefullyKeptPawns.Count();
        bool ideoClassicMode =
            ModsConfig.IdeologyActive && Find.IdeoManager != null && Find.IdeoManager.classicMode;
        bool ideologyActive = ModsConfig.IdeologyActive;
        return writer =>
        {
            writer.WriteString("preset", descriptor.Preset);
            writer.WriteString("saveName", descriptor.SaveName);
            writer.WriteString("seed", descriptor.Seed);
            writer.WriteString("scenarioBase", descriptor.Scenario.Base);
            writer.WriteString("storytellerDef", descriptor.Storyteller.Def);
            writer.WriteString("difficultyDef", descriptor.Storyteller.DifficultyDef);
            writer.WriteString("biome", biome);
            writer.WriteString("hilliness", hilliness);
            writer.WriteString("descriptorPath", descriptor.SourcePath);
            writer.WriteNumber("planetCoverage", descriptor.World.PlanetCoverage);
            writer.WriteNumber("mapSize", descriptor.Map.Size);
            writer.WriteNumber("startingTile", startingTile.tileId);
            WriteNullableNumber(writer, "startingPawnCount", descriptor.Scenario.StartingPawnCount);
            WriteNullableNumber(writer, "pawnChoiceCount", descriptor.Scenario.PawnChoiceCount);
            writer.WriteBoolean("ideoClassicMode", ideoClassicMode);
            writer.WriteBoolean("ideologyActive", ideologyActive);
            writer.WriteNumber("colonistsOnMap", colonistsOnMap);
            writer.WriteNumber("forcedWorldPawns", forcedWorldPawns);
        };
    }

    private static string MetadataBodyJson(
        Action<Utf8JsonWriter> members, int? reloadTicks, List<string>? reloadHits,
        SortedDictionary<string, string[]>? tickDriven)
    {
        return RimRunnerJson.Write(
            writer =>
            {
                writer.WriteStartObject();
                members(writer);
                if (reloadTicks is int ticks)
                {
                    writer.WriteStartObject("reloadCheck");
                    writer.WriteNumber("ticks", ticks);
                    writer.WriteStartArray("hits");
                    foreach (string hit in reloadHits!)
                    {
                        writer.WriteStringValue(hit);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteStartObject(RimRunnerWireKeys.BuildSaveSidecar.ComponentTickDriven);
                    foreach (KeyValuePair<string, string[]> component in tickDriven!)
                    {
                        writer.WriteStartArray(component.Key);
                        foreach (string element in component.Value)
                        {
                            writer.WriteStringValue(element);
                        }

                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            },
            true) + "\n";
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string key, int? value)
    {
        if (value is int number)
        {
            writer.WriteNumber(key, number);
            return;
        }

        writer.WriteNull(key);
    }

    private static T ParseEnum<T>(string value, string field) where T : struct
    {
        try
        {
            return (T)Enum.Parse(typeof(T), value, ignoreCase: false);
        }
        catch (Exception)
        {
            throw new FormatException(
                RimRunnerLog.Prefix + " " + field + " 的值无法识别：" + value
                + "（合法值：" + string.Join("、", Enum.GetNames(typeof(T))) + "）");
        }
    }
}

[HarmonyPatch(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay))]
public static class Patch_SetupForQuickTestPlay_RimRunnerNewGame
{
    public static bool Prefix() => RimRunnerNewGame.SetupReplacement();
}

[HarmonyPatch(typeof(GameAndMapInitExceptionHandlers),
    nameof(GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap))]
public static class Patch_MapGenFailure_RimRunnerNewGame
{
    public static void Prefix(Exception e) => RimRunnerNewGame.FailFromMapGeneration(e);
}

[HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
public static class Patch_InitNewGame_RimRunnerNewGame
{
    public static void Finalizer() => RimRunnerNewGame.ReleaseRandState();
}
