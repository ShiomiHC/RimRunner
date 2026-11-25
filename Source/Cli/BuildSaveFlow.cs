using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

using RimRunner;

namespace RimRunnerCli;

public static class BuildSaveFlow
{
    private const string EnginePrefix = "RimRunner";

    public sealed record Prepared(
        RimRunnerNewGameDescriptor Descriptor, string DescriptorPath, string SaveName, string? ModList,
        string PlayerLogPath, string TestModMarker);

    public static Prepared Prepare(OrchestratorDescriptor orchestrator, string productRoot,
        ClaimRecord claim, string? modList)
    {
        RimRunnerNewGameDescriptor descriptor = NewGameDescriptorFile.Load(
            productRoot, orchestrator, modList, out string mergedJson);
        string path = Path.Combine(claim.SaveData, "rimrunner.newgame.merged.json");
        Directory.CreateDirectory(claim.SaveData);
        File.WriteAllText(path, mergedJson);
        CliLog.Info($"[build-save] 预设 {descriptor.Preset}、存档名 {descriptor.SaveName}、"
                    + $"seed {descriptor.Seed}；合并后的描述符 {path}");

        return new Prepared(descriptor, path, descriptor.SaveName!, modList,
            GameArgsBuilder.LogPathFor(claim),
            "[" + RunCoordinator.AssemblyStem(orchestrator.TestMod.AssemblyName) + "]");
    }

    public static void VerifyLanding(Prepared prepared, string savesDir,
        DateTime processStartedUtc)
    {
        string savePath = Path.Combine(savesDir, prepared.SaveName + ".rws");
        if (!File.Exists(savePath))
        {
            throw new StoreException($"建档的存档文件不存在：{savePath}；游戏已进档并转入空闲");
        }

        DateTime written = File.GetLastWriteTimeUtc(savePath);
        if (written < processStartedUtc.AddSeconds(-1))
        {
            throw new StoreException(
                $"建档落点是旧档：{savePath} 的写入时间 {written:O} 早于本轮进程启动 "
                + $"{processStartedUtc:O}，本轮未覆写该文件");
        }

        string metadataPath = Path.Combine(savesDir, prepared.SaveName + RimRunner.RimRunnerWireKeys.BuildSaveSidecar.Suffix);
        if (!File.Exists(metadataPath))
        {
            throw new StoreException($"建档边角文件不存在：{metadataPath}");
        }

        JsonObject actual;
        try
        {
            actual = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject
                     ?? throw new JsonException("根元素不是对象");
        }
        catch (JsonException exception)
        {
            throw new StoreException($"建档边角文件 JSON 无效：{metadataPath}\n  {exception.Message}");
        }

        RimRunnerNewGameDescriptor expected = prepared.Descriptor;
        var mismatches = new List<string>();
        Compare(mismatches, actual, "preset", expected.Preset);
        Compare(mismatches, actual, "saveName", expected.SaveName);
        Compare(mismatches, actual, "seed", expected.Seed);
        Compare(mismatches, actual, "scenarioBase", expected.Scenario.Base);
        Compare(mismatches, actual, "storytellerDef", expected.Storyteller.Def);
        Compare(mismatches, actual, "difficultyDef", expected.Storyteller.DifficultyDef);
        Compare(mismatches, actual, "biome", expected.StartingTile.Biome);
        Compare(mismatches, actual, "hilliness", expected.StartingTile.Hilliness);
        Compare(mismatches, actual, "descriptorPath", prepared.DescriptorPath);
        Compare(mismatches, actual, "mapSize", expected.Map.Size);
        Compare(mismatches, actual, "startingPawnCount", expected.Scenario.StartingPawnCount);
        Compare(mismatches, actual, "pawnChoiceCount", expected.Scenario.PawnChoiceCount);
        CompareFloat(mismatches, actual, "planetCoverage", expected.World.PlanetCoverage);

        if (expected.Scenario.StartingPawnCount is int wanted
            && ReadInt(actual, "colonistsOnMap") is int onMap && onMap != wanted)
        {
            mismatches.Add($"colonistsOnMap：声明 {wanted}，实测 {onMap}");
        }

        if (expected.Ideo.ClassicMode
            && ReadBool(actual, "ideologyActive") == true
            && ReadBool(actual, "ideoClassicMode") != true)
        {
            mismatches.Add("ideoClassicMode：声明 true，实测 false（经典教条未生效）");
        }

        if (mismatches.Count > 0)
        {
            throw new StoreException(
                $"建档结果与下传的描述符不匹配（{metadataPath}）：\n  "
                + string.Join("\n  ", mismatches));
        }

        int reloadTicks = VerifyReload(actual, metadataPath);
        int forced = ReadInt(actual, "forcedWorldPawns") ?? 0;
        CliLog.Info($"[build-save] 存档文件核验通过：{savePath}"
                    + $"（tile {ReadInt(actual, "startingTile")}、"
                    + $"地图上 {ReadInt(actual, "colonistsOnMap")} 人）；回载空推 {reloadTicks} tick，"
                    + "无阻断窗进栈、无新登记的 game condition、无研究类 job 开工");
        if (forced > 0
            && expected.Scenario.PawnChoiceCount is int choice
            && expected.Scenario.StartingPawnCount is int starting && choice > starting)
        {
            CliLog.Info($"[build-save] 世界池里有 {forced} 个永久 pawn；"
                        + $"其中候选 pawn 比起始 pawn 多 {choice - starting} 个");
        }

        if (actual[RimRunner.RimRunnerWireKeys.BuildSaveSidecar.ComponentTickDriven] is JsonObject tickDriven)
        {
            foreach (KeyValuePair<string, JsonNode?> component in tickDriven.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                IEnumerable<string> elements = component.Value is JsonArray names
                    ? names.Select(name => name?.GetValue<string>() ?? "")
                    : [];
                CliLog.Info($"[建档] 自动豁免：组件 {component.Key} 的 {string.Join("、", elements)}"
                            + $"——回载空推 {reloadTicks} tick 期间随时间变，载这份档的批不比对这些元素");
            }
        }
    }

    internal sealed record ProbeFindings(List<string> Issues, List<string> Lines, List<string> LogOnly);

    internal static ProbeFindings ReadProbe(EngineReport report, int exitCode)
    {
        List<string> issues = [];
        List<string> logOnly = [];
        int residues = report.AllCases.Sum(entry => entry.Warnings.Count(warning => warning.Key == RimRunner.RimRunnerWarningKey.Residue));
        int reloadErrors = report.ReloadErrors.Count(line => !string.IsNullOrEmpty(line));
        if (exitCode != 0) issues.Add($"退出码 {exitCode}");
        if (residues > 0) issues.Add($"未豁免的残留警告 {residues} 条");
        if (reloadErrors > 0) issues.Add($"载新档的错误级日志 {reloadErrors} 条");
        return new ProbeFindings(issues, ReportPresentation.ReloadErrorLines(report.ReloadErrors, logOnly), logOnly);
    }

    internal static int VerifyReload(JsonObject actual, string metadataPath)
    {
        if (actual["reloadCheck"] is not JsonObject check
            || ReadInt(check, "ticks") is not int ticks
            || check["hits"] is not JsonArray hits)
        {
            throw new StoreException($"建档边角文件没有回载验证结果（reloadCheck）：{metadataPath}");
        }

        List<string> lines = [.. hits.Select(hit => hit is JsonValue value && value.TryGetValue(out string? text)
            ? text
            : hit?.ToJsonString() ?? "null")];
        if (lines.Count > 0)
        {
            throw new StoreException(StoreException.Detailed(
                $"建档回载空推 {ticks} tick 期间发生档内已排定的事件（{metadataPath}）：", "[建档回载]",
                lines.Select(line => StoreException.Fact(line))));
        }

        return ticks;
    }

    public static void Scrub(string savesDir, string saveName, string tag = "build-save")
    {
        string savePath = Path.Combine(savesDir, saveName + ".rws");
        var document = new XmlDocument { PreserveWhitespace = true };
        document.Load(savePath);
        XmlNodeList? nodes = document.SelectNodes("/savegame/game/components/li");
        var doomed = new List<XmlNode>();
        if (nodes != null)
        {
            foreach (XmlNode node in nodes)
            {
                XmlAttribute? attribute = node.Attributes?["Class"];
                if (attribute != null
                    && attribute.Value.StartsWith(EnginePrefix, StringComparison.Ordinal))
                {
                    doomed.Add(node);
                }
            }
        }

        if (doomed.Count == 0)
        {
            throw new StoreException(
                $"摘引擎节点：{savePath} 里 /savegame/game/components 下没有 "
                + $"Class=\"{EnginePrefix}…\" 的节点");
        }

        foreach (XmlNode node in doomed)
        {
            node.ParentNode!.RemoveChild(node);
        }

        document.Save(savePath);
        CliLog.Info($"[{tag}] 已摘掉 {doomed.Count} 个引擎 GameComponent 节点");
    }

    public sealed class OutwardModIdsException(string message, bool rewriteNotApplied) : StoreException(message)
    {
        public bool RewriteNotApplied { get; } = rewriteNotApplied;
    }

    public static void VerifyOutwardModIds(string savesDir, string saveName, string testModId,
        IReadOnlyList<string> registeredPackageIds, IReadOnlyList<string>? modListIds = null)
    {
        string savePath = Path.Combine(savesDir, saveName + ".rws");
        var document = new XmlDocument();
        document.Load(savePath);
        List<string> modIds =
        [
            .. (document.SelectNodes("/savegame/meta/modIds/li")?.Cast<XmlNode>() ?? [])
                .Select(node => node.InnerText.Trim())
        ];

        var complaints = new List<string>();
        bool rewriteNotApplied = false;
        if (modIds.Contains(testModId, StringComparer.OrdinalIgnoreCase))
        {
            complaints.Add($"含测试 mod {testModId}");
            rewriteNotApplied = true;
        }

        if (registeredPackageIds.Count == 0)
        {
            throw new StoreException("存档头核验：登记里没有载入目录的 packageId，无从核对产品单元");
        }

        foreach (string registered in registeredPackageIds)
        {
            string bareId = RimRunner.RimRunnerPackagePresence.BareOf(registered);
            if (modIds.Contains(registered, StringComparer.OrdinalIgnoreCase))
            {
                complaints.Add($"含带后缀的 {registered}");
                rewriteNotApplied = true;
            }

            int hits = modIds.Count(id => string.Equals(id, bareId, StringComparison.OrdinalIgnoreCase));
            if (hits != 1)
            {
                complaints.Add($"{bareId} 出现 {hits} 次，预期 1 次");
            }
        }

        if (modListIds != null)
        {
            foreach (string id in modListIds.Where(id => !modIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
            {
                complaints.Add($"mod 名单上的 {id} 不在存档头");
            }
        }

        if (complaints.Count > 0)
        {
            throw new OutwardModIdsException(
                $"存档头核验：{savePath} 的 /savegame/meta/modIds 与真实安装的形状不匹配：" + Environment.NewLine
                + string.Join(Environment.NewLine, complaints.Select(line => "  " + line)), rewriteNotApplied);
        }

        CliLog.Info($"[build-save] 存档头核验通过：{savePath} 的 modIds 是真实安装的形状");
    }

    public static void PruneDanglingRelations(string savesDir, string saveName)
    {
        string savePath = Path.Combine(savesDir, saveName + ".rws");
        var document = new XmlDocument { PreserveWhitespace = true };
        document.Load(savePath);

        if (document.SelectSingleNode("//social") == null)
        {
            throw new StoreException($"摘悬空亲属关系：{savePath} 里没有 <social> 节点");
        }

        var saved = new HashSet<string>(StringComparer.Ordinal);
        XmlNodeList? ids = document.SelectNodes("//id");
        if (ids != null)
        {
            foreach (XmlNode id in ids)
            {
                saved.Add("Thing_" + id.InnerText.Trim());
            }
        }

        var doomed = new List<XmlNode>();
        var described = new List<string>();
        XmlNodeList? relations = document.SelectNodes("//social/directRelations/li[otherPawn]");
        if (relations != null)
        {
            foreach (XmlNode relation in relations)
            {
                string other = relation["otherPawn"]!.InnerText.Trim();
                if (other != "null" && saved.Contains(other))
                {
                    continue;
                }

                doomed.Add(relation);
                described.Add($"{DescribePawn(relation.ParentNode?.ParentNode?.ParentNode)} "
                              + $"—{relation["def"]?.InnerText.Trim() ?? "?"}→ {other}");
            }
        }

        foreach (XmlNode relation in doomed)
        {
            relation.ParentNode!.RemoveChild(relation);
        }

        if (doomed.Count > 0)
        {
            document.Save(savePath);
            CliLog.Info($"[build-save] 已摘掉 {doomed.Count} 条悬空亲属关系："
                        + string.Join("、", described));
        }

        RecordPruned(Path.Combine(savesDir, saveName + RimRunner.RimRunnerWireKeys.BuildSaveSidecar.Suffix), described);
    }

    public static void Publish(string instanceSaves, string savesDir, string saveName)
    {
        Directory.CreateDirectory(savesDir);
        foreach (string file in new[] { saveName + ".rws", saveName + RimRunner.RimRunnerWireKeys.BuildSaveSidecar.Suffix })
        {
            string source = Path.Combine(instanceSaves, file);
            string target = Path.Combine(savesDir, file);
            string staging = target + ".publishing";
            try
            {
                File.Copy(source, staging, overwrite: true);
                File.Move(staging, target, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(staging); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw new StoreException(
                    $"建档已在实例副本上完成，拷回基线档目录失败：{source} → {target}\n  {exception.Message}");
            }
        }

        CliLog.Info($"[build-save] 已发布存档：{Path.Combine(savesDir, saveName + ".rws")}");
    }

    private static void RecordPruned(string metadataPath, List<string> described)
    {
        JsonObject metadata = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject
                              ?? throw new StoreException($"建档边角文件根元素不是对象：{metadataPath}");
        metadata["prunedDanglingRelations"] = new JsonArray(
            described.Select(line => (JsonNode?)JsonValue.Create(line)).ToArray());
        WriteMetadata(metadataPath, metadata);
    }

    public static void RecordFirstFills(string savesDir, string saveName, IReadOnlyDictionary<string, string> fills)
    {
        string metadataPath = Path.Combine(savesDir, saveName + RimRunner.RimRunnerWireKeys.BuildSaveSidecar.Suffix);
        JsonObject metadata = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject
                              ?? throw new StoreException($"建档边角文件根元素不是对象：{metadataPath}");
        JsonObject table = [];
        foreach (KeyValuePair<string, string> pair in fills.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            table[pair.Key] = new JsonArray(pair.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(element => (JsonNode?)JsonValue.Create(element)).ToArray());
        }

        metadata[RimRunner.RimRunnerWireKeys.BuildSaveSidecar.ComponentFirstFills] = table;
        WriteMetadata(metadataPath, metadata);
    }

    private static void WriteMetadata(string metadataPath, JsonObject metadata)
    {
        RimTooling.IO.AtomicFile.Write(metadataPath, metadata.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");
    }

    private static string DescribePawn(XmlNode? pawn)
    {
        string id = pawn?["id"]?.InnerText.Trim() ?? "?";
        string? nick = pawn?["name"]?["nick"]?.InnerText.Trim();
        return string.IsNullOrEmpty(nick) ? id : $"{id}「{nick}」";
    }

    private static void Compare(List<string> sink, JsonObject actual, string key, string? expected)
    {
        string? got = actual[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        if (!string.Equals(got, expected, StringComparison.Ordinal))
        {
            sink.Add($"{key}：下传 {Show(expected)}，回读 {Show(got)}");
        }
    }

    private static void Compare(List<string> sink, JsonObject actual, string key, int? expected)
    {
        int? got = ReadInt(actual, key);
        if (got != expected)
        {
            sink.Add($"{key}：下传 {Show(expected?.ToString())}，回读 {Show(got?.ToString())}");
        }
    }

    private static void CompareFloat(List<string> sink, JsonObject actual, string key, float expected)
    {
        double? got = actual[key] is JsonValue value && value.TryGetValue(out double parsed)
            ? parsed
            : null;
        if (got == null || Math.Abs(got.Value - expected) > 1e-6)
        {
            sink.Add($"{key}：下传 {expected.ToString("R", CultureInfo.InvariantCulture)}，"
                     + $"回读 {Show(got?.ToString("R", CultureInfo.InvariantCulture))}");
        }
    }

    private static int? ReadInt(JsonObject actual, string key) =>
        actual[key] is JsonValue value && value.TryGetValue(out int parsed) ? parsed : null;

    private static bool? ReadBool(JsonObject actual, string key) =>
        actual[key] is JsonValue value && value.TryGetValue(out bool parsed) ? parsed : null;

    private static string Show(string? value) => value == null ? "null" : "「" + value + "」";
}
