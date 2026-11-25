using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RimRunner;

public sealed partial class RimRunnerNewGameDescriptor
{
    public const string PresetRealistic = "realistic";

    public const string ModListSuffix = ".newgame.toml";

    [DescriptorKey("预设名，只有 realistic：正常游玩能到达的状态")]
    public string Preset { get; set; } = PresetRealistic;

    [DescriptorKey("存档文件名，恒为 <saveNamePrefix>-<名单名>，由 CLI 填", FilledByCli = true)]
    public string SaveName { get; set; } = "";

    [DescriptorKey("世界种子字符串。未指定 = saveNamePrefix，同一产品的各名单共用一个世界；某份名单要另一个世界就在它的名单层写")]
    public string Seed { get; set; } = "";

    [DescriptorKey("开局剧本")]
    public ScenarioSection Scenario { get; set; } = new();
    [DescriptorKey("叙事者与难度")]
    public StorytellerSection Storyteller { get; set; } = new();
    [DescriptorKey("世界生成参数")]
    public WorldSection World { get; set; } = new();
    [DescriptorKey("Ideology 的教条预设")]
    public IdeoSection Ideo { get; set; } = new();
    [DescriptorKey("起始地的筛选条件")]
    public StartingTileSection StartingTile { get; set; } = new();
    [DescriptorKey("殖民地地图")]
    public MapSection Map { get; set; } = new();
    [DescriptorKey("存档文件")]
    public OutputSection Output { get; set; } = new();

    public sealed class ScenarioSection
    {
        [DescriptorKey("开局剧本的 ScenarioDef defName")]
        public string Base { get; set; } = "";
        [DescriptorKey("按 ScenPart 类名摘掉的部件。开局对话框引擎恒摘，不必写")]
        public List<string> RemoveParts { get; set; } = [];

        [DescriptorKey("地图上的殖民者数，与 pawnChoiceCount 同时指定或同时不指定。未指定 = 剧本原值")]
        public int? StartingPawnCount { get; set; }
        [DescriptorKey("起始 pawn 的候选数，≥ startingPawnCount，与 startingPawnCount 同时指定或同时不指定。未指定 = 剧本原值")]
        public int? PawnChoiceCount { get; set; }

        [DescriptorKey("玩家派系的 FactionDef defName。未指定 = 剧本自带的派系")]
        public string? PlayerFaction { get; set; }
        [DescriptorKey("抵达方式，PlayerPawnsArriveMethod 的枚举名。未指定 = 剧本原值")]
        public string? ArriveMethod { get; set; }
        [DescriptorKey("关掉的事件，IncidentDef defName 列表")]
        public List<string> DisableIncidents { get; set; } = [];
    }

    public sealed class StorytellerSection
    {
        [DescriptorKey("叙事者的 StorytellerDef defName")]
        public string Def { get; set; } = "";
        [DescriptorKey("难度的 DifficultyDef defName")]
        public string DifficultyDef { get; set; } = "";

        [DescriptorKey("偏离项：键 = Difficulty 的字段名，值 = 数，底值取 difficultyDef 的标准值。只与自定义难度档（isCustom 的 DifficultyDef）同时指定。未指定 = difficultyDef 的标准值")]
        public Dictionary<string, float>? Difficulty { get; set; }

        [DescriptorKey("建档过程中停用叙事者事件。不进存档；执行批次期间的停用不由这个键决定")]
        public bool RuntimeGate { get; set; } = true;
    }

    public sealed class WorldSection
    {
        [DescriptorKey("星球覆盖率，(0, 1]")]
        public float PlanetCoverage { get; set; }
        [DescriptorKey("降雨，OverallRainfall 的枚举名")]
        public string Rainfall { get; set; } = "";
        [DescriptorKey("温度，OverallTemperature 的枚举名")]
        public string Temperature { get; set; } = "";
        [DescriptorKey("人口，OverallPopulation 的枚举名")]
        public string Population { get; set; } = "";
        [DescriptorKey("地标密度，LandmarkDensity 的枚举名")]
        public string LandmarkDensity { get; set; } = "";
    }

    public sealed class IdeoSection
    {
        [DescriptorKey("生成一份全派系共用的经典教条。Ideology 未启用时无效")]
        public bool ClassicMode { get; set; } = true;
    }

    public sealed class StartingTileSection
    {
        [DescriptorKey("起始地的 BiomeDef defName")]
        public string Biome { get; set; } = "";
        [DescriptorKey("地形起伏，Hilliness 的枚举名")]
        public string Hilliness { get; set; } = "";
        [DescriptorKey("起始地须有河")]
        public bool RequireRiver { get; set; }
        [DescriptorKey("起始地须临海")]
        public bool RequireCoast { get; set; }
    }

    public sealed class MapSection
    {
        [DescriptorKey("地图边长，100..500")]
        public int Size { get; set; }

        [DescriptorKey("存盘前把地图的 MapGeneratorDef 改写为哪个 def。未指定 = 不改")]
        public string? GeneratorDefOnSave { get; set; }
    }

    public sealed class OutputSection
    {
        [DescriptorKey("从存档里摘掉引擎的空 GameComponent 节点，档在没装引擎的游戏里也零错误级日志载入")]
        public bool ScrubEngineComponent { get; set; } = true;
    }

    internal static readonly JsonSerializerOptions MergedJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RequireEveryKey } },
    };

    private static void RequireEveryKey(JsonTypeInfo info)
    {
        foreach (JsonPropertyInfo property in info.Properties)
        {
            property.IsRequired = true;
        }
    }

    internal List<string> Problems()
    {
        var problems = new List<string>();
        if (Preset != PresetRealistic)
        {
            problems.Add($"无法识别 preset：{Preset}；允许：{PresetRealistic}");
        }

        if (string.IsNullOrWhiteSpace(SaveName))
        {
            problems.Add("saveName 为空");
        }
        else if (SaveName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                 || SaveName.Contains('/') || SaveName.Contains('\\') || SaveName.Contains(".."))
        {
            problems.Add($"saveName 含非法字符或路径分隔符（收到 {SaveName}）");
        }

        if (string.IsNullOrWhiteSpace(Seed))
        {
            problems.Add("seed 为空");
        }

        foreach ((string value, string field) in new[]
                 {
                     (Scenario.Base, "scenario.base"),
                     (Storyteller.Def, "storyteller.def"),
                     (Storyteller.DifficultyDef, "storyteller.difficultyDef"),
                     (World.Rainfall, "world.rainfall"),
                     (World.Temperature, "world.temperature"),
                     (World.Population, "world.population"),
                     (World.LandmarkDensity, "world.landmarkDensity"),
                     (StartingTile.Biome, "startingTile.biome"),
                     (StartingTile.Hilliness, "startingTile.hilliness"),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"{field} 为空");
            }
        }

        if (Scenario.PlayerFaction != null && string.IsNullOrWhiteSpace(Scenario.PlayerFaction))
        {
            problems.Add("scenario.playerFaction 是空串；沿用剧本自带的派系时未指定该键");
        }

        if ((Scenario.StartingPawnCount == null) != (Scenario.PawnChoiceCount == null))
        {
            problems.Add(
                "scenario.startingPawnCount 与 scenario.pawnChoiceCount 须同时指定或同时不指定");
        }

        if (Scenario.StartingPawnCount is < 0)
        {
            problems.Add($"scenario.startingPawnCount 不能为负（收到 {Scenario.StartingPawnCount}）");
        }

        if (Scenario.PawnChoiceCount is int choice
            && Scenario.StartingPawnCount is int starting && choice < starting)
        {
            problems.Add(
                $"scenario.pawnChoiceCount（{choice}）须 ≥ scenario.startingPawnCount（{starting}）");
        }

        foreach (string part in Scenario.RemoveParts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                problems.Add("scenario.removeParts 含空项");
            }
        }

        foreach (string incident in Scenario.DisableIncidents)
        {
            if (string.IsNullOrWhiteSpace(incident))
            {
                problems.Add("scenario.disableIncidents 含空项");
            }
        }

        if (World.PlanetCoverage is <= 0f or > 1f)
        {
            problems.Add($"world.planetCoverage 须在 (0, 1]（收到 {World.PlanetCoverage}）");
        }

        if (Map.Size is < 100 or > 500)
        {
            problems.Add($"map.size 须在 100..500（收到 {Map.Size}）");
        }

        if (Storyteller.Difficulty != null && Storyteller.Difficulty.Count == 0)
        {
            problems.Add(
                "storyteller.difficulty 是空表：未指定该键时取 difficultyDef 的标准值");
        }

        if (Storyteller.Difficulty != null
            && Storyteller.Difficulty.TryGetValue("diseaseIntervalFactor", out float diseaseInterval)
            && diseaseInterval <= 0f)
        {
            problems.Add(
                $"storyteller.difficulty.diseaseIntervalFactor 须 > 0（收到 {diseaseInterval}）：≤ 0 是每次检定都发病，停用疾病是 ≥ 100");
        }

        return problems;
    }
}
