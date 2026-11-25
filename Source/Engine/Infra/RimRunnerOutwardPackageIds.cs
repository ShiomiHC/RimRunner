namespace RimRunner;

public sealed class RimRunnerOutwardPackageIdsMod : Mod
{
    public RimRunnerOutwardPackageIdsMod(ModContentPack content) : base(content)
    {
        RimRunnerOutwardPackageIds.Install(content);
    }
}

internal static class RimRunnerOutwardPackageIds
{
    internal static ModContentPack? TestMod { get; private set; }

    internal static HashSet<string> Registered { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal static void Install(ModContentPack content)
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            return;
        }

        TestMod = content;
        if (GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.SuffixedPackageIds, out string rawIds))
        {
            foreach (string id in RimRunnerPackagePresence.ParseArg(rawIds))
            {
                if (id.IndexOf('.') > 0)
                {
                    Registered.Add(id);
                }
                else
                {
                    RimRunnerStartupErrors.Raise($"{RimRunnerEngineArgs.SuffixedPackageIds} 里的 {id} 不带后缀，该项不做后缀剥离");
                }
            }
        }

        var harmony = new Harmony("rimrunner.packageids");
        if (Registered.Count > 0)
        {
            harmony.Patch(
                AccessTools.PropertyGetter(typeof(ModContentPack), nameof(ModContentPack.PackageIdPlayerFacing)),
                postfix: new HarmonyMethod(typeof(RimRunnerOutwardPackageIds), nameof(PlayerFacingPostfix)));
        }

        harmony.Patch(
            AccessTools.Method(typeof(ScribeMetaHeaderUtility), nameof(ScribeMetaHeaderUtility.WriteMetaHeader)),
            prefix: new HarmonyMethod(typeof(RimRunnerOutwardPackageIds), nameof(WriteMetaHeaderPrefix)));

        if (Registered.Count > 0)
        {
            harmony.Patch(AccessTools.Method(typeof(ModsConfig), "RecacheActiveMods"),
                postfix: new HarmonyMethod(typeof(RimRunnerOutwardPackageIds), nameof(AddBareActiveIds)));
            harmony.Patch(AccessTools.Method(typeof(ModLister), nameof(ModLister.RebuildModList)),
                postfix: new HarmonyMethod(typeof(RimRunnerOutwardPackageIds), nameof(AddBareListedIds)));
            AddBareActiveIds();
            AddBareListedIds();
        }
    }

    private static readonly FieldInfo ActiveIdsField = AccessTools.Field(typeof(ModsConfig), "activeModsHashSet");

    private static readonly FieldInfo[] ListedIdsFields =
    [
        AccessTools.Field(typeof(ModLister), "modsByPackageId"),
        AccessTools.Field(typeof(ModLister), "modsByPackageIdIgnorePostfix"),
    ];

    private static void AddBareActiveIds()
    {
        var active = (HashSet<string>)ActiveIdsField.GetValue(null)!;
        foreach (string id in Registered)
        {
            if (active.Contains(id.ToLowerInvariant()))
            {
                active.Add(RimRunnerPackagePresence.BareOf(id).ToLowerInvariant());
            }
        }
    }

    private static void AddBareListedIds()
    {
        foreach (FieldInfo field in ListedIdsFields)
        {
            var table = (Dictionary<string, List<ModMetaData>>)field.GetValue(null)!;
            foreach (string id in Registered)
            {
                if (!table.TryGetValue(id, out List<ModMetaData>? ours))
                {
                    continue;
                }

                string bare = RimRunnerPackagePresence.BareOf(id);
                if (!table.TryGetValue(bare, out List<ModMetaData>? listed))
                {
                    table[bare] = [.. ours];
                    continue;
                }

                foreach (ModMetaData mod in ours)
                {
                    if (!listed.Contains(mod))
                    {
                        listed.Add(mod);
                    }
                }
            }
        }
    }

    internal static string? BarePackageId(string? packageId) =>
        packageId != null && Registered.Contains(packageId)
            ? RimRunnerPackagePresence.BareOf(packageId)
            : packageId;

    internal static readonly string[] CopiedHeaderNodes = [MetaNode, GameVersionNode, ModIdsNode, SteamIdsNode, ModNamesNode];
    private const string MetaNode = "meta";
    private const string GameVersionNode = "gameVersion";
    private const string ModIdsNode = "modIds";
    private const string SteamIdsNode = "modSteamIds";
    private const string ModNamesNode = "modNames";

    internal static List<ModContentPack> OutwardMods() =>
        LoadedModManager.RunningMods.Where(mod => mod != TestMod).ToList();

    private static void PlayerFacingPostfix(ref string? __result)
    {
        __result = BarePackageId(__result);
    }

    private static bool WriteMetaHeaderPrefix()
    {
        if (!Scribe.EnterNode(MetaNode))
        {
            return false;
        }

        try
        {
            string? version = VersionControl.CurrentVersionStringWithRev;
            Scribe_Values.Look(ref version, GameVersionNode);
            List<ModContentPack> mods = OutwardMods();
            List<string?> ids = mods.Select(mod => BarePackageId(mod.PackageId)).ToList();
            Scribe_Collections.Look(ref ids, ModIdsNode, LookMode.Undefined);
            List<uint> steamIds = mods.Select(mod => mod.SteamAppId).ToList();
            Scribe_Collections.Look(ref steamIds, SteamIdsNode, LookMode.Undefined);
            List<string> names = mods.Select(mod => mod.Name).ToList();
            Scribe_Collections.Look(ref names, ModNamesNode, LookMode.Undefined);
        }
        finally
        {
            Scribe.ExitNode();
        }

        return false;
    }
}
