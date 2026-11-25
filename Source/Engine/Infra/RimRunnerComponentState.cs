using System.IO;
using System.Text;
using System.Text.Json;
using RimWorld.Planet;

namespace RimRunner;

internal sealed class RimRunnerComponentState
{
    private const int MaxDetailChars = 140;

    private static readonly Dictionary<string, string[]> BuiltInExemptions = new(StringComparer.Ordinal)
    {
        ["RimWorld.Planet.TilePollutionComp"] = ["cycleIndex", "currentWorldPollution", "totalWorldPollution"],
        ["RimWorld.GameComponent_Anomaly"] = ["nextHarbingerTreeCheckTick"],
    };

    private static HashSet<Assembly>? ourAssemblies;

    private static Dictionary<string, string[]> savedFirstFills = new(StringComparer.Ordinal);
    private static Dictionary<string, string[]> savedTickDriven = new(StringComparer.Ordinal);

    private static Dictionary<string, string[]> modListExemptions = new(StringComparer.Ordinal);
    private const string ModListDirective = "[rimrunner].component_exemption";

    private readonly Func<IEnumerable<KeyValuePair<string, IExposable>>> source;
    private readonly CleanLedger? ledger;
    private readonly Dictionary<string, Entry> origin;
    private Dictionary<string, Entry> start;
    private Dictionary<string, Entry>? startAtSave;
    private Dictionary<string, Entry>? atSave;

    private readonly struct Entry(Type type, string xml)
    {
        public readonly Type Type = type;
        public readonly string Xml = xml;
    }

    internal enum Layer
    {
        Map,
        World,
        Game,
    }

    internal sealed class Drift
    {
        internal required string Where;
        internal required string TypeName;
        internal bool Product;
        internal bool OnMap;
        internal Layer Layer;
        internal required string Key;
        internal required string Detail;
        internal bool BothBaselines;
        internal string[]? FilledElements;

        public override string ToString() => Where + " 的存档状态与开始执行前不匹配（" + Detail + "）";
    }

    internal sealed class CleanLedger
    {
        internal static readonly CleanLedger Shared = new();

        private readonly Dictionary<string, string> clean = new(StringComparer.Ordinal);

        internal bool IsClean(string key, string xml) => clean.TryGetValue(key, out string? first) && first == xml;

        internal void NoteDrift(string key, string cleanXml)
        {
            if (!clean.ContainsKey(key))
            {
                clean[key] = cleanXml;
            }
        }

        internal void Clear() => clean.Clear();
    }

    internal static void OnGameLoaded(bool checkpointLoad)
    {
        if (!checkpointLoad)
        {
            CleanLedger.Shared.Clear();
            LoadSavedExemptions();
        }
    }

    internal static IDisposable SavedExemptionsForSelfTest(string componentType, string[] elements, bool tickDriven = false)
    {
        Dictionary<string, string[]> table = new(StringComparer.Ordinal) { [componentType] = elements };
        if (tickDriven)
        {
            Dictionary<string, string[]> previousTickDriven = savedTickDriven;
            savedTickDriven = table;
            return new Restore(() => savedTickDriven = previousTickDriven);
        }

        Dictionary<string, string[]> previous = savedFirstFills;
        savedFirstFills = table;
        return new Restore(() => savedFirstFills = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    internal static List<RimRunnerFinding> UseModListExemptions(IReadOnlyList<RimRunnerRunClaims.Ack> declared)
    {
        List<RimRunnerFinding> findings = [];
        Dictionary<string, SortedSet<string>> effective = new(StringComparer.Ordinal);
        foreach (RimRunnerRunClaims.Ack ack in declared)
        {
            int colon = ack.Target.IndexOf(':');
            string typeName = colon < 0 ? ack.Target : ack.Target[..colon];
            string[] elements = colon < 0
                ? []
                : ack.Target[(colon + 1)..].Split(',').Select(element => element.Trim()).Where(element => element.Length > 0).ToArray();
            Type? type = AccessTools.TypeByName(typeName);
            if (elements.Length == 0 || type == null
                || !(typeof(MapComponent).IsAssignableFrom(type) || typeof(WorldComponent).IsAssignableFrom(type)
                     || typeof(GameComponent).IsAssignableFrom(type)))
            {
                findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.InertDeclaration,
                    "mod 名单的 " + ModListDirective + " 指定 " + ack.Target + "：" + (elements.Length == 0
                        ? "没有元素名"
                        : type == null ? "本进程里没有这个类型" : "该类型不是图、世界或整局组件") + "，这条不生效"));
                continue;
            }

            if (IsProduct(type))
            {
                findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.InertDeclaration,
                    "mod 名单的 " + ModListDirective + " 指定被测产品的组件 " + typeName
                    + "，这条不生效：产品组件的豁免在产品描述符里经 ComponentStateExemption 登记"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(ack.Reason))
            {
                findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.EmptyReason,
                    "mod 名单的 " + ModListDirective + " 对 " + ack.Target + " 的理由为空，这条不生效"));
                continue;
            }

            string key = type.FullName ?? typeName;
            if (!effective.TryGetValue(key, out SortedSet<string>? merged))
            {
                merged = new SortedSet<string>(StringComparer.Ordinal);
                effective[key] = merged;
            }

            merged.UnionWith(elements);
        }

        modListExemptions = effective.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        foreach (KeyValuePair<string, string[]> pair in modListExemptions)
        {
            RimRunnerEngineLog.Info("名单豁免组件元素：" + pair.Key + " 的 " + string.Join("、", pair.Value));
        }

        return findings;
    }

    private static void LoadSavedExemptions()
    {
        savedFirstFills = new Dictionary<string, string[]>(StringComparer.Ordinal);
        savedTickDriven = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.Save, out string saveName)
            || string.IsNullOrWhiteSpace(saveName))
        {
            return;
        }

        string path = Path.ChangeExtension(GenFilePaths.FilePathForSavedGame(saveName), null) + RimRunnerWireKeys.BuildSaveSidecar.Suffix;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            savedFirstFills = ReadTable(document.RootElement, RimRunnerWireKeys.BuildSaveSidecar.ComponentFirstFills);
            savedTickDriven = ReadTable(document.RootElement, RimRunnerWireKeys.BuildSaveSidecar.ComponentTickDriven);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                              or InvalidOperationException)
        {
            savedFirstFills = new Dictionary<string, string[]>(StringComparer.Ordinal);
            savedTickDriven = new Dictionary<string, string[]>(StringComparer.Ordinal);
            RimRunnerEngineLog.Warn("无法读取基线档边角文件的组件豁免表，本次载档不据此豁免：" + path + "：" + exception.Message);
        }
    }

    private static Dictionary<string, string[]> ReadTable(JsonElement root, string key)
    {
        Dictionary<string, string[]> table = new(StringComparer.Ordinal);
        if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return table;
        }

        foreach (JsonProperty component in value.EnumerateObject())
        {
            table[component.Name] = component.Value.EnumerateArray()
                .Select(element => element.GetString())
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .ToArray();
        }

        return table;
    }

    internal RimRunnerComponentState(Map map, bool withGlobal)
        : this(() => Components(map.uniqueID, withGlobal), CleanLedger.Shared)
    {
    }

    internal RimRunnerComponentState(Func<IEnumerable<KeyValuePair<string, IExposable>>> source,
        CleanLedger? ledger = null)
    {
        this.source = source;
        this.ledger = ledger;
        origin = Take();
        start = origin;
    }

    internal static void Normalize()
    {
        foreach (Map map in Find.Maps.ToList())
        {
            if (!map.Disposed)
            {
                foreach (MapComponent component in map.components.ToList())
                {
                    Serialize(component);
                }
            }
        }

        foreach (WorldComponent component in Find.World.components.ToList())
        {
            Serialize(component);
        }

        foreach (GameComponent component in Current.Game.components.ToList())
        {
            Serialize(component);
        }
    }

    internal void NoteCheckpointSave()
    {
        startAtSave = start;
        atSave = Take();
    }

    internal void NoteCheckpointLoad()
    {
        if (atSave == null)
        {
            return;
        }

        Dictionary<string, Entry> loaded = Take();
        Dictionary<string, Entry> rebased = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Entry> entry in loaded)
        {
            bool keptBeforeSave = startAtSave!.TryGetValue(entry.Key, out Entry before)
                                  && atSave.TryGetValue(entry.Key, out Entry saved)
                                  && (before.Xml == saved.Xml
                                      || (origin.TryGetValue(entry.Key, out Entry first) && first.Xml == saved.Xml));
            rebased[entry.Key] = keptBeforeSave || !startAtSave!.ContainsKey(entry.Key) ? entry.Value : startAtSave![entry.Key];
        }

        start = rebased;
    }

    internal List<Drift> Close()
    {
        List<Drift> drifts = [];
        Dictionary<string, Entry> now = Take();
        foreach (KeyValuePair<string, Entry> entry in now)
        {
            if (!start.TryGetValue(entry.Key, out Entry rebased) || rebased.Xml == entry.Value.Xml)
            {
                continue;
            }

            bool onMap = entry.Key.StartsWith("map:", StringComparison.Ordinal);
            if (!onMap && ledger != null && ledger.IsClean(entry.Key, entry.Value.Xml))
            {
                continue;
            }

            string detail;
            bool bothBaselines = false;
            bool hasOrigin = origin.TryGetValue(entry.Key, out Entry first);
            if (hasOrigin && first.Xml != rebased.Xml)
            {
                if (first.Xml == entry.Value.Xml)
                {
                    continue;
                }

                detail = "对开始执行前 " + FirstDiff(first.Xml, entry.Value.Xml) + "；对读档后 " + FirstDiff(rebased.Xml, entry.Value.Xml);
                bothBaselines = true;
            }
            else
            {
                detail = FirstDiff(rebased.Xml, entry.Value.Xml);
            }

            if (!onMap)
            {
                ledger?.NoteDrift(entry.Key, hasOrigin ? first.Xml : rebased.Xml);
            }

            drifts.Add(new Drift
            {
                Where = Label(entry.Key),
                TypeName = entry.Value.Type.FullName ?? entry.Value.Type.Name,
                Product = IsProduct(entry.Value.Type),
                OnMap = onMap,
                Layer = LayerOf(entry.Key),
                Key = entry.Key,
                Detail = detail,
                BothBaselines = bothBaselines,
                FilledElements = FilledElements(rebased.Xml, entry.Value.Xml),
            });
        }

        return drifts;
    }

    internal static bool IsProduct(Type type)
    {
        ourAssemblies ??= [.. RimRunnerDefScope.OwnedAssemblies];
        return ourAssemblies.Contains(type.Assembly);
    }

    internal static string Serialize(IExposable component)
    {
        string xml = Scribe.saver.DebugOutputFor(component);
        string[]? exempt = ExemptElements(component.GetType());
        if (exempt == null || string.IsNullOrEmpty(xml))
        {
            return xml;
        }

        return RemoveTopLevelElements(xml, exempt);
    }

    private static string RemoveTopLevelElements(string xml, string[] names)
    {
        List<string> kept = [];
        string? closing = null;
        foreach (string raw in xml.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (closing != null)
            {
                if (line == closing)
                {
                    closing = null;
                }

                continue;
            }

            string? name = names.FirstOrDefault(candidate => OpensTopLevel(line, candidate));
            if (name == null)
            {
                kept.Add(line);
                continue;
            }

            if (!line.EndsWith("/>", StringComparison.Ordinal) && !line.EndsWith("</" + name + ">", StringComparison.Ordinal))
            {
                closing = "  </" + name + ">";
            }
        }

        if (kept.Count(line => line.Length > 0) == 1 && kept.First(line => line.Length > 0) is var root
            && root.StartsWith("<", StringComparison.Ordinal) && root.EndsWith("/>", StringComparison.Ordinal))
        {
            int nameEnd = root.IndexOfAny([' ', '/', '>'], 1);
            return root[..^2].TrimEnd() + ">\n</" + root[1..nameEnd] + ">";
        }

        return string.Join("\n", kept);
    }

    internal static string[] ChangedElements(string before, string after)
    {
        Dictionary<string, List<string>> left = TopLevelBlocks(before);
        Dictionary<string, List<string>> right = TopLevelBlocks(after);
        return left.Keys.Union(right.Keys)
            .Where(name => !left.TryGetValue(name, out List<string>? was) || !right.TryGetValue(name, out List<string>? now)
                           || !was.SequenceEqual(now))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string[]? FilledElements(string before, string after)
    {
        Dictionary<string, List<string>> left = TopLevelBlocks(before);
        Dictionary<string, List<string>> right = TopLevelBlocks(after);
        foreach (KeyValuePair<string, List<string>> block in left)
        {
            if (!right.TryGetValue(block.Key, out List<string>? kept) || !kept.SequenceEqual(block.Value))
            {
                return null;
            }
        }

        string[] added = right.Keys.Where(name => !left.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        return added.Length == 0 ? null : added;
    }

    private static Dictionary<string, List<string>> TopLevelBlocks(string xml)
    {
        Dictionary<string, List<string>> blocks = new(StringComparer.Ordinal);
        string? name = null;
        StringBuilder current = new();

        void Flush()
        {
            if (name == null)
            {
                return;
            }

            if (!blocks.TryGetValue(name, out List<string>? list))
            {
                list = [];
                blocks[name] = list;
            }

            list.Add(current.ToString());
            current.Clear();
            name = null;
        }

        foreach (string raw in xml.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (TopLevelOpening(line) is string opened)
            {
                Flush();
                name = opened;
            }
            else if (!line.StartsWith("  ", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            if (name != null)
            {
                current.Append(line).Append('\n');
            }
        }

        Flush();
        return blocks;
    }

    private static string? TopLevelOpening(string line)
    {
        if (line.Length <= 3 || !line.StartsWith("  <", StringComparison.Ordinal) || line[3] == '/' || line[3] == ' ')
        {
            return null;
        }

        int end = line.IndexOfAny([' ', '>', '/'], 3);
        return end < 0 ? line[3..] : line[3..end];
    }

    private static bool OpensTopLevel(string line, string name)
    {
        string head = "  <" + name;
        if (!line.StartsWith(head, StringComparison.Ordinal) || line.Length == head.Length)
        {
            return false;
        }

        char next = line[head.Length];
        return next is '>' or ' ' or '/';
    }

    private static string[]? ExemptElements(Type type)
    {
        BuiltInExemptions.TryGetValue(type.FullName ?? "", out string[]? builtIn);
        string[]? declared = null;
        RimRunnerProduct.TryGetCapabilities()?.ComponentStateExemptions.TryGetValue(type, out declared);
        savedTickDriven.TryGetValue(type.FullName ?? "", out string[]? tickDriven);
        modListExemptions.TryGetValue(type.FullName ?? "", out string[]? byModList);
        string[]? firstFills = null;
        if (!RimRunnerSaveProbe.Armed)
        {
            savedFirstFills.TryGetValue(type.FullName ?? "", out firstFills);
        }

        string[] merged = (builtIn ?? []).Concat(declared ?? []).Concat(tickDriven ?? []).Concat(byModList ?? []).Concat(firstFills ?? [])
            .Distinct(StringComparer.Ordinal).ToArray();
        return merged.Length == 0 ? null : merged;
    }

    internal static Dictionary<string, KeyValuePair<string, string>> ForeignViews()
    {
        Dictionary<string, KeyValuePair<string, string>> views = new(StringComparer.Ordinal);

        void Add(string key, IExposable component)
        {
            Type type = component.GetType();
            if (IsProduct(type))
            {
                return;
            }

            while (views.ContainsKey(key))
            {
                key += "+";
            }

            views[key] = new KeyValuePair<string, string>(type.FullName ?? type.Name, Serialize(component));
        }

        foreach (WorldComponent component in Find.World.components.ToList())
        {
            Add("world:" + component.GetType().FullName, component);
        }

        foreach (GameComponent component in Current.Game.components.ToList())
        {
            Add("game:" + component.GetType().FullName, component);
        }

        return views;
    }

    private static IEnumerable<KeyValuePair<string, IExposable>> Components(int mapId, bool withGlobal)
    {
        Map? map = Find.Maps.FirstOrDefault(candidate => candidate.uniqueID == mapId && !candidate.Disposed);
        if (map != null)
        {
            foreach (MapComponent component in map.components)
            {
                yield return new KeyValuePair<string, IExposable>("map:" + component.GetType().FullName, component);
            }
        }

        if (withGlobal)
        {
            foreach (WorldComponent component in Find.World.components)
            {
                yield return new KeyValuePair<string, IExposable>("world:" + component.GetType().FullName, component);
            }

            foreach (GameComponent component in Current.Game.components)
            {
                yield return new KeyValuePair<string, IExposable>("game:" + component.GetType().FullName, component);
            }
        }
    }

    private Dictionary<string, Entry> Take()
    {
        Dictionary<string, Entry> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, IExposable> component in source().ToList())
        {
            string key = component.Key;
            while (result.ContainsKey(key))
            {
                key += "+";
            }

            result[key] = new Entry(component.Value.GetType(), Serialize(component.Value));
        }

        return result;
    }

    private static Layer LayerOf(string key) =>
        key.StartsWith("map:", StringComparison.Ordinal) ? Layer.Map
        : key.StartsWith("world:", StringComparison.Ordinal) ? Layer.World
        : Layer.Game;

    private static string Label(string key)
    {
        if (key.StartsWith("map:", StringComparison.Ordinal))
        {
            return "图组件 " + key[4..];
        }

        return key.StartsWith("world:", StringComparison.Ordinal) ? "世界组件 " + key[6..] : "整局组件 " + key[5..];
    }

    internal static string FirstDiff(string before, string after)
    {
        string[] left = before.Split('\n');
        string[] right = after.Split('\n');
        int index = 0;
        while (index < left.Length && index < right.Length && left[index] == right[index])
        {
            index++;
        }

        string Clip(string[] lines)
        {
            if (index >= lines.Length)
            {
                return "<结尾>";
            }

            string line = lines[index].Trim();
            return line.Length > MaxDetailChars ? line[..MaxDetailChars] + "…" : line;
        }

        string? Enclosing(string[] lines)
        {
            for (int at = Math.Min(index, lines.Length - 1); at >= 0; at--)
            {
                string line = lines[at].TrimEnd('\r');
                if (TopLevelOpening(line) is string name)
                {
                    return name;
                }

                if (!line.StartsWith("  ", StringComparison.Ordinal))
                {
                    return null;
                }
            }

            return null;
        }

        string? element = Enclosing(right) ?? Enclosing(left);
        return "第 " + (index + 1) + " 行" + (element == null ? "" : "（顶层元素 " + element + "）") + " "
               + Clip(left) + " → " + Clip(right);
    }
}
