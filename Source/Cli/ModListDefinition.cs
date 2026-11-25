namespace RimRunnerCli;

public sealed class ModListDefinition
{
    public const string ExtensionSection = "rimrunner";
    public const string ModsFromKey = "mods_from";
    public const string SaveFromKey = "save_from";

    private static readonly string[] KnownScalarKeys =
    [
        TextureTierPolicy.DirectiveKey,
        SaveFromKey,
    ];

    private static readonly string[] KnownArrayKeys =
    [
        OmitRequiredPolicy.DirectiveKey,
        CaseGroupPolicy.DirectiveKey,
        AcknowledgePolicy.DirectiveKey,
        AcknowledgeStartupErrorPolicy.DirectiveKey,
        KnownFailingPolicy.DirectiveKey,
        ComponentExemptionPolicy.DirectiveKey,
    ];

    private static readonly HashSet<string> ArrowListKeys = new(StringComparer.Ordinal)
    {
        AcknowledgePolicy.DirectiveKey,
        AcknowledgeStartupErrorPolicy.DirectiveKey,
        KnownFailingPolicy.DirectiveKey,
        ComponentExemptionPolicy.DirectiveKey,
    };

    private static readonly string[] AllKnownKeys = [.. KnownArrayKeys, .. KnownScalarKeys];

    private static readonly HashSet<string> KnownKeySet = new(AllKnownKeys, StringComparer.Ordinal);

    internal static readonly RimTooling.RimWorld.ModListLoadOptions LoadOptions = new()
    {
        AllowedExtensionSections = new HashSet<string>(StringComparer.Ordinal) { ExtensionSection },
        ExcludedFileSuffixes =
        [
            InstanceConfig.PrefsLayerSuffix,
            RimRunner.RimRunnerNewGameDescriptor.ModListSuffix,
        ],
        ValidateRequestedName = static (requestedName, filePath) =>
            RimRunner.RimRunnerDataLayout.LedgerFileName(
                RimRunner.RimRunnerDataLayout.PassRateLedgerStem, requestedName) == null
                ? $"mod 名单名 {requestedName} 含字母数字与 - _ 以外的字符，引擎不为该名单写入台账：{filePath}"
                : null,
    };

    private static readonly RimTooling.RimWorld.ModListLoadOptions OwnerLoadOptions = new()
    {
        AllowedExtensionSections = LoadOptions.AllowedExtensionSections,
        ExcludedFileSuffixes = LoadOptions.ExcludedFileSuffixes,
    };

    private readonly Dictionary<string, string> scalarDirectives;
    private readonly Dictionary<string, IReadOnlyList<string>> listDirectives;
    private readonly Dictionary<string, string> directiveOrigins;

    private ModListDefinition(
        string name,
        IReadOnlyList<string> modIds,
        string? modsFrom,
        string? saveFrom,
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string> notes,
        Dictionary<string, string> scalarDirectives,
        Dictionary<string, IReadOnlyList<string>> listDirectives,
        Dictionary<string, string> directiveOrigins)
    {
        Name = name;
        ModIds = modIds;
        ModsFrom = modsFrom;
        SaveFrom = saveFrom;
        SourcePaths = sourcePaths;
        Notes = notes;
        this.scalarDirectives = scalarDirectives;
        this.listDirectives = listDirectives;
        this.directiveOrigins = directiveOrigins;
    }

    public string Name { get; }
    public IReadOnlyList<string> ModIds { get; }

    public string? ModsFrom { get; }

    public string? SaveFrom { get; }

    public string SaveOwner => SaveFrom ?? Name;

    public IReadOnlyList<string> SourcePaths { get; }

    public IReadOnlyList<string> Notes { get; }

    public static string AvailableNames(string modListsDir)
    {
        string fullDir = CommandContext.FullPath(modListsDir);
        if (RimTooling.IO.PathPresence.Of(fullDir) == RimTooling.IO.PathPresenceKind.Absent)
        {
            return "（名单目录不存在）";
        }

        try
        {
            IReadOnlyList<string> names = RimTooling.RimWorld.RimWorldModList.ListNames(fullDir, LoadOptions);
            return names.Count > 0 ? string.Join(" / ", names) : "（空）";
        }
        catch (RimTooling.RimWorld.ModListException exception)
        {
            throw new StoreException(exception.Message, exception);
        }
    }

    public sealed record Listed(string Name, ModListDefinition? Definition, string? Error);

    public static IReadOnlyList<Listed> LoadAll(string modListsDir)
    {
        string fullDir = CommandContext.FullPath(modListsDir);
        if (RimTooling.IO.PathPresence.Of(fullDir) == RimTooling.IO.PathPresenceKind.Absent)
        {
            return [];
        }

        IReadOnlyList<string> names;
        try
        {
            names = RimTooling.RimWorld.RimWorldModList.ListNames(fullDir, LoadOptions);
        }
        catch (RimTooling.RimWorld.ModListException exception)
        {
            throw new StoreException(exception.Message, exception);
        }

        var listed = new List<Listed>(names.Count);
        foreach (string name in names)
        {
            try
            {
                listed.Add(new Listed(name, Load(fullDir, name), null));
            }
            catch (Exception exception) when (exception is StoreException or IOException or UnauthorizedAccessException)
            {
                listed.Add(new Listed(name, null, exception.Message));
            }
        }

        return listed;
    }

    public string ModListsDir => Path.GetDirectoryName(SourcePaths[0])!;

    public static ModListDefinition Load(string modListsDir, string name) =>
        LoadInternal(modListsDir, name, validateSaveFrom: true);

    private static ModListDefinition LoadInternal(string modListsDir, string name, bool validateSaveFrom)
    {
        string fullDir = CommandContext.FullPath(modListsDir);
        RimTooling.RimWorld.ModListLoadOptions loadOptions = validateSaveFrom ? LoadOptions : OwnerLoadOptions;
        RimTooling.RimWorld.RimWorldModList list;
        try
        {
            list = RimTooling.RimWorld.RimWorldModList.Load(fullDir, name, loadOptions);
        }
        catch (RimTooling.RimWorld.ModListException exception)
        {
            throw new StoreException(exception.Message, exception);
        }

        var notes = new List<string>(list.Notes.Count);
        foreach (RimTooling.RimWorld.ModListNote note in list.Notes)
        {
            notes.Add(note.Message);
        }

        var scalarDirectives = new Dictionary<string, string>(StringComparer.Ordinal);
        var listDirectives = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var directiveOrigins = new Dictionary<string, string>(StringComparer.Ordinal);
        string? saveFrom = null;
        RimTooling.RimWorld.ModListLayer leafLayer = list.Layers[^1];

        foreach ((RimTooling.RimWorld.ModListLayer layer, RimTooling.IO.TomlTable section) in list.GetExtensionChain(ExtensionSection))
        {
            bool isLeaf = ReferenceEquals(layer, leafLayer);
            foreach ((string key, object raw) in section)
            {
                int line = section.TryGetLine(key, out int parsedLine) ? parsedLine : 0;
                string lineLabel = line > 0 ? $" 第 {line} 行" : "";
                if (!KnownKeySet.Contains(key))
                {
                    throw new StoreException(
                        $"mod 名单 {layer.Name}{lineLabel} 在 [{ExtensionSection}] 中有未知键 {key}（{layer.FilePath}）；"
                        + $"可用：{string.Join(" / ", AllKnownKeys)}");
                }

                if (key == SaveFromKey)
                {
                    if (raw is not string saveText)
                    {
                        throw new StoreException(
                            $"mod 名单 {layer.Name}{lineLabel} 的 [{ExtensionSection}].{SaveFromKey} 须为字符串（{layer.FilePath}）");
                    }

                    if (isLeaf)
                    {
                        saveFrom = saveText.Trim();
                    }

                    continue;
                }

                if (key == TextureTierPolicy.DirectiveKey)
                {
                    if (raw is not string textureText)
                    {
                        throw new StoreException(
                            $"mod 名单 {layer.Name}{lineLabel} 的 [{ExtensionSection}].{key} 须为字符串（{layer.FilePath}）");
                    }

                    string trimmed = textureText.Trim();
                    if (trimmed.Length == 0 && scalarDirectives.ContainsKey(key))
                    {
                        scalarDirectives.Remove(key);
                        directiveOrigins.Remove(key);
                    }
                    else
                    {
                        scalarDirectives[key] = trimmed;
                        directiveOrigins[key] = layer.Name;
                    }

                    continue;
                }

                if (raw is not RimTooling.IO.TomlArray array)
                {
                    throw new StoreException(
                        $"mod 名单 {layer.Name}{lineLabel} 的 [{ExtensionSection}].{key} 须为字符串数组 [\"...\"]（{layer.FilePath}）");
                }

                var items = new List<string>(array.Count);
                var seen = ArrowListKeys.Contains(key) ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
                for (int i = 0; i < array.Count; i++)
                {
                    int itemLine = array.TryGetItemLine(i, out int parsedItemLine) ? parsedItemLine : line;
                    string itemLineLabel = itemLine > 0 ? $" 第 {itemLine} 行" : "";
                    if (array[i] is not string element)
                    {
                        throw new StoreException(
                            $"mod 名单 {layer.Name}{itemLineLabel} 的 [{ExtensionSection}].{key} 第 {i + 1} 项须为字符串（{layer.FilePath}）");
                    }

                    string trimmed = element.Trim();
                    if (seen != null)
                    {
                        if (seen.TryGetValue(trimmed, out int firstLine))
                        {
                            notes.Add($"mod 名单 {layer.Name} 的 [{ExtensionSection}].{key} 在第 {firstLine}、{itemLine} 行重复写同一项（{layer.FilePath}）");
                            continue;
                        }

                        seen[trimmed] = itemLine;
                    }

                    items.Add(trimmed);
                }

                if (items.Count == 0 && listDirectives.ContainsKey(key))
                {
                    listDirectives.Remove(key);
                    directiveOrigins.Remove(key);
                }
                else
                {
                    listDirectives[key] = items;
                    directiveOrigins[key] = layer.Name;
                }
            }
        }

        var definition = new ModListDefinition(
            list.Name,
            list.Mods,
            list.ModsFrom,
            saveFrom,
            list.SourcePaths,
            notes,
            scalarDirectives,
            listDirectives,
            directiveOrigins);

        if (validateSaveFrom && saveFrom != null)
        {
            CheckSaveFrom(fullDir, definition, saveFrom);
        }

        return definition;
    }

    private static void CheckSaveFrom(string fullDir, ModListDefinition definition, string owner)
    {
        string path = Path.Combine(fullDir, definition.Name + RimTooling.RimWorld.RimWorldModList.Extension);
        if (owner.Length == 0)
        {
            throw new StoreException($"mod 名单 {definition.Name} 的 [{ExtensionSection}].{SaveFromKey} 未指定名单名（{path}）");
        }

        if (owner.Equals(definition.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new StoreException($"mod 名单 {definition.Name} 的 [{ExtensionSection}].{SaveFromKey} 引用本名单（{path}）");
        }

        string newGame = Path.Combine(fullDir, definition.Name + RimRunner.RimRunnerNewGameDescriptor.ModListSuffix);
        if (File.Exists(newGame))
        {
            throw new StoreException(
                $"mod 名单 {definition.Name} 写有 [{ExtensionSection}].{SaveFromKey} = {owner}，不建自己的档，{newGame} 不被读取");
        }

        if (owner.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || owner is "." or "..")
        {
            throw new StoreException(
                $"mod 名单 {definition.Name} 的 [{ExtensionSection}].{SaveFromKey} 引的名单名不合法：{owner}（{path}）");
        }

        string ownerPath = Path.Combine(fullDir, owner + RimTooling.RimWorld.RimWorldModList.Extension);
        if (RimTooling.IO.PathPresence.Of(ownerPath) == RimTooling.IO.PathPresenceKind.Absent)
        {
            throw new StoreException(
                $"mod 名单 {definition.Name} 的 [{ExtensionSection}].{SaveFromKey} 引的名单不存在：{ownerPath}；"
                + $"可用：{AvailableNames(fullDir)}");
        }

        ModListDefinition source = LoadInternal(fullDir, owner, validateSaveFrom: false);
        if (source.SaveFrom != null)
        {
            throw new StoreException(
                $"mod 名单 {definition.Name} 的 [{ExtensionSection}].{SaveFromKey} 引的 {owner} 自己也写有 [{ExtensionSection}].{SaveFromKey} = {source.SaveFrom}");
        }

        if (!definition.ModIds.SequenceEqual(source.ModIds, StringComparer.OrdinalIgnoreCase))
        {
            throw new StoreException(
                $"mod 名单 {definition.Name} 的 [{ExtensionSection}].{SaveFromKey} = {owner}：两份名单的生效清单不同，"
                + $"{definition.Name} 是 {string.Join(", ", definition.ModIds)}，{owner} 是 {string.Join(", ", source.ModIds)}（{path}）");
        }
    }

    public string? DirectiveOrNull(string key) =>
        scalarDirectives.TryGetValue(key, out string? value) ? value : null;

    public IReadOnlyList<string>? DirectiveList(string key) =>
        listDirectives.TryGetValue(key, out IReadOnlyList<string>? values) ? values : null;

    public string DirectiveSource(string key) =>
        directiveOrigins.TryGetValue(key, out string? origin) ? origin : Name;
}
