using System.Xml;

namespace RimRunnerCli;

public sealed class SaveDataInitializer(OrchestratorDescriptor descriptor, string productRoot,
    string? localAppDataOverride = null)
{
    public enum SavesMode
    {
        CopyBaseline,
        Empty,
    }

    public enum PrefsProfile
    {
        Batch,
        Film,
        Showcase,
    }

    private readonly OrchestratorDescriptor descriptor = descriptor;
    private readonly string? localAppDataOverride = localAppDataOverride;
    private readonly string productRoot = productRoot;

    public InstanceConfig.Outcome? ConfigOutcome { get; private set; }

    public static string InstanceSavePath(string saveData, string saveName) =>
        Path.Combine(saveData, "Saves", saveName + ".rws");

    public static string AssertManagedSaveDataPath(ClaimRecord claim, string? saveDataRoot = null)
    {
        string actual = PathIdentity.Normalize(claim.SaveData);
        string expected = PathIdentity.Normalize(
            Path.Combine(saveDataRoot ?? RecordBuilder.SaveDataRoot, claim.Number.ToString()));
        if (!PathIdentity.Same(actual, expected))
        {
            throw new StoreException($"拒绝清理非本实例的 savedata：{actual}（预期 {expected}）");
        }

        return actual;
    }

    public static void DeleteTree(string saveData)
    {
        if (RimTooling.IO.PathPresence.Of(saveData) == RimTooling.IO.PathPresenceKind.Absent)
        {
            return;
        }

        FileSystemSafety.DeleteTreeJunctionSafe(saveData);
    }

    private static readonly string[] KeptConfigFiles =
        ["ModsConfig.xml", "Prefs.xml", RimRunner.RimRunnerWireKeys.ConfigManifest.FileName];

    public static string? TrimStopped(ClaimStore store, int number, Func<ClaimRecord, bool> isLive,
        string? saveDataRoot = null)
    {
        using (store.SweepLock())
        {
            ClaimRecord? claim = store.Read(number);
            if (claim == null || isLive(claim))
                return null;
            string saveData;
            try
            {
                saveData = AssertManagedSaveDataPath(claim, saveDataRoot);
            }
            catch (StoreException)
            {
                return null;
            }

            var removed = new List<string>();
            string saves = Path.Combine(saveData, "Saves");
            if (Directory.Exists(saves))
            {
                if (Junction.GetLinkInfo(saves).Kind != RimTooling.Windows.ReparseLinkKind.None)
                    Junction.Remove(saves);
                else
                    FileSystemSafety.DeleteTreeJunctionSafe(saves);
                removed.Add("Saves");
            }

            string config = Path.Combine(saveData, "Config");
            if (Directory.Exists(config))
            {
                int count = 0;
                foreach (string entry in Directory.EnumerateFileSystemEntries(config))
                {
                    if (KeptConfigFiles.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                        continue;
                    if (Directory.Exists(entry))
                        FileSystemSafety.DeleteTreeJunctionSafe(entry);
                    else
                        File.Delete(entry);
                    count++;
                }

                if (count > 0)
                    removed.Add($"Config 里 {count} 项");
            }

            return removed.Count == 0 ? null : $"已裁 savedata（删 {string.Join("、", removed)}）：{saveData}";
        }
    }

    private const int ErrorInsufficientBuffer = 122;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    internal static string? PackageFullName()
    {
        int length = 0;
        if (GetCurrentPackageFullName(ref length, null) != ErrorInsufficientBuffer) return null;
        var buffer = new char[length];
        return GetCurrentPackageFullName(ref length, buffer) == 0 ? new string(buffer, 0, length - 1) : null;
    }

    public string Initialize(
        ClaimRecord claim,
        string? sourceModsConfigPath,
        IReadOnlyList<string> activeModIdOverride,
        string baselineSaveName,
        string savesSourceDir,
        string? saveDataRoot = null,
        SavesMode savesMode = SavesMode.CopyBaseline,
        string? modListName = null,
        bool showcase = false)
    {
        string saveData = AssertManagedSaveDataPath(claim, saveDataRoot);
        if (savesMode == SavesMode.CopyBaseline
            && PathIdentity.IsUnder(savesSourceDir, saveData, includeSelf: true))
        {
            throw new StoreException($"基线档在实例 savedata 里，冷启动重建 savedata 时会被删除：{savesSourceDir}");
        }
        DeleteTree(saveData);
        Directory.CreateDirectory(saveData);

        string PlayerConfigDir()
        {
            if (string.IsNullOrWhiteSpace(sourceModsConfigPath))
            {
                return PlayerConfigDirOf(localAppDataOverride);
            }

            string sourceModsConfig = CommandContext.FullPath(sourceModsConfigPath);
            return Path.GetDirectoryName(sourceModsConfig)
                ?? throw new StoreException($"ModsConfig 路径缺少父目录：{sourceModsConfig}");
        }

        var unitFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (OrchestratorDescriptor.UnitDescriptor unit in DeployedUnitsOf(claim))
        {
            unitFolders[unit.PackageId] = LoadDirNameOf(claim, unit);
        }

        ConfigOutcome = new InstanceConfig(descriptor).Build(Path.Combine(saveData, "Config"),
            new InstanceConfig.Inputs(productRoot, modListName, activeModIdOverride,
                showcase, PlayerConfigDir, unitFolders, claim.ModsFolder));
        string instanceSaves = Path.Combine(saveData, "Saves");
        Directory.CreateDirectory(instanceSaves);
        if (savesMode == SavesMode.CopyBaseline)
        {
            string sourceSave = Path.Combine(savesSourceDir, baselineSaveName + ".rws");
            if (!File.Exists(sourceSave))
            {
                throw new StoreException(
                    $"基线档不存在：{sourceSave}");
            }

            File.Copy(sourceSave, InstanceSavePath(saveData, baselineSaveName), overwrite: true);
            string sourceSidecar = Path.Combine(savesSourceDir, baselineSaveName + RimRunner.RimRunnerWireKeys.BuildSaveSidecar.Suffix);
            if (File.Exists(sourceSidecar))
            {
                File.Copy(sourceSidecar, Path.Combine(instanceSaves, baselineSaveName + RimRunner.RimRunnerWireKeys.BuildSaveSidecar.Suffix), overwrite: true);
            }
        }

        string modsConfigPath = Path.Combine(saveData, "Config", "ModsConfig.xml");

        if (claim.LoadDirs is { Count: > 0 })
        {
            List<OrchestratorDescriptor.UnitDescriptor> expected = DeployedUnitsOf(claim);
            var present = new List<OrchestratorDescriptor.UnitDescriptor>();
            var modsConfigDoc = new XmlDocument();
            modsConfigDoc.Load(modsConfigPath);
            foreach (XmlNode node in modsConfigDoc.SelectNodes("//activeMods/li")!)
            {
                string id = node.InnerText!.Trim();
                OrchestratorDescriptor.UnitDescriptor? unit = descriptor.Units.FirstOrDefault(candidate =>
                    string.Equals(candidate.PackageId, id, StringComparison.OrdinalIgnoreCase));
                if (unit != null)
                {
                    node.InnerText = SuffixedIdOf(claim, unit).ToLowerInvariant();
                    present.Add(unit);
                }
            }

            List<OrchestratorDescriptor.UnitDescriptor> missing = [.. expected.Except(present)];
            if (missing.Count > 0)
            {
                int hit = expected.Count - missing.Count;
                throw new StoreException(
                    $"ModsConfig activeMods 里产品单元命中 {hit} 个，预期 {expected.Count}"
                    + $"（缺 {string.Join("、", missing.Select(unit => unit.PackageId))}）");
            }

            modsConfigDoc.Save(modsConfigPath);
        }

        return modsConfigPath;
    }

    private List<OrchestratorDescriptor.UnitDescriptor> DeployedUnitsOf(ClaimRecord claim) =>
        [.. descriptor.Units.Where(unit => IndexOf(claim, unit) >= 0)];

    private static int IndexOf(ClaimRecord claim, OrchestratorDescriptor.UnitDescriptor unit) =>
        claim.PackageIds.FindIndex(id => string.Equals(RimRunner.RimRunnerPackagePresence.BareOf(id), unit.PackageId,
            StringComparison.OrdinalIgnoreCase));

    private static string SuffixedIdOf(ClaimRecord claim, OrchestratorDescriptor.UnitDescriptor unit) =>
        IndexOf(claim, unit) is int index and >= 0
            ? claim.PackageIds[index]
            : throw new StoreException($"ModsConfig activeMods 里有产品单元 {unit.PackageId}，登记 {claim.DisplayName} 却未部署该单元的载入目录");

    private static string LoadDirNameOf(ClaimRecord claim, OrchestratorDescriptor.UnitDescriptor unit) =>
        IndexOf(claim, unit) is int index and >= 0 && index < (claim.LoadDirs?.Count ?? 0)
            ? Path.GetFileName(claim.LoadDirs![index])
            : throw new StoreException($"登记 {claim.DisplayName} 没有单元 {unit.Name} 的载入目录");

    private static readonly (string Name, string Value)[] AlwaysNormalizedPrefs =
    [
        ("runInBackground", "True"),

        ("quickStartDevPaletteOn", "False"),

        ("openLogOnWarnings", "False"),

        ("adaptiveTrainingEnabled", "False"),

        ("resetModsConfigOnCrash", "False"),

        ("pauseOnLoad", "True"),

        ("automaticPauseMode", "Never"),

        ("autosaveIntervalDays", "999"),

        ("maxNumberOfPlayerSettlements", "1"),

        ("disableQuickStartCryptoSickness", "True")
    ];

    private (string Name, string Value)[] BatchOnlyPrefs()
    {
        (int width, int height) = (descriptor.WindowSize.Width, descriptor.WindowSize.Height);
        return
        [
            ("uiScale", "1"),
            ("scaleRelativeToSystem", "True"),
            ("screenWidth", width.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("screenHeight", height.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("windowedSizeLogical", "False"),
            ("fullscreen", "False")
        ];
    }

    private static string? GravshipCutscenes(PrefsProfile profile) => profile switch
    {
        PrefsProfile.Batch => "False",
        PrefsProfile.Film => "True",
        _ => null,
    };

    public void RewritePrefs(string saveDataFolder, PrefsProfile profile, bool silent = false)
    {
        if (profile != PrefsProfile.Batch && silent)
        {
            throw new InvalidOperationException(profile == PrefsProfile.Showcase
                ? "展台档不接受静音：展台实例的 Prefs 使用开发机的实际取值"
                : "拍摄档不接受静音：拍摄实例的音量取拍摄那组");
        }

        string prefsPath = Path.Combine(saveDataFolder, "Config", "Prefs.xml");
        if (!File.Exists(prefsPath))
        {
            throw new StoreException($"savedata 副本缺少 Prefs.xml：{prefsPath}");
        }

        var doc = new XmlDocument();
        doc.Load(prefsPath);
        void SetNode(string name, string value) => SetPrefsNode(doc, name, value);

        foreach ((string name, string value) in AlwaysNormalizedPrefs)
        {
            SetNode(name, value);
        }

        if (GravshipCutscenes(profile) is string cutscenes)
        {
            SetNode("gravshipCutscenes", cutscenes);
        }

        if (profile != PrefsProfile.Showcase)
        {
            foreach ((string name, string value) in BatchOnlyPrefs())
            {
                SetNode(name, value);
            }
        }

        if (silent)
        {
            foreach (string volumeName in VolumePrefs)
            {
                SetNode(volumeName, "0");
            }
        }

        if (profile == PrefsProfile.Film)
        {
            foreach ((string name, string value) in FilmVolumePrefs)
            {
                SetNode(name, value);
            }
        }

        doc.Save(prefsPath);
    }

    private static readonly string[] VolumePrefs = ["volumeMaster", "volumeGame", "volumeMusic", "volumeAmbient", "volumeUI"];

    private static readonly (string Name, string Value)[] FilmVolumePrefs =
    [
        ("volumeMaster", "0"), ("volumeGame", "1"), ("volumeMusic", "0.4"), ("volumeAmbient", "1"), ("volumeUI", "1"),
    ];

    public static readonly HashSet<string> PinnedPrefNames =
    [
        .. AlwaysNormalizedPrefs.Select(pref => pref.Name),
        "gravshipCutscenes", "uiScale", "scaleRelativeToSystem", "screenWidth", "screenHeight", "windowedSizeLogical", "fullscreen",
        .. VolumePrefs,
    ];

    public static void SetPrefsNode(XmlDocument doc, string name, string value)
    {
        XmlNode? node = doc.DocumentElement!.SelectSingleNode(name);
        if (node == null)
        {
            node = doc.CreateElement(name);
            doc.DocumentElement!.AppendChild(node);
        }

        node.InnerText = value;
    }

    public static string PlayerConfigDirOf(string? localAppDataOverride = null) =>
        Path.Combine(DefaultSaveDataRoot(localAppDataOverride), "Config");

    private static string DefaultSaveDataRoot(string? localAppDataOverride)
    {
        string localAppData = localAppDataOverride
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = CommandContext.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new StoreException("无法确定 Windows LocalAppData 路径");
        }

        string? parent = Path.GetDirectoryName(CommandContext.FullPath(localAppData)) ?? throw new StoreException($"LocalAppData 路径缺少父目录：{localAppData}");
        return CommandContext.FullPath(Path.Combine(parent,
            "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios"));
    }
}
