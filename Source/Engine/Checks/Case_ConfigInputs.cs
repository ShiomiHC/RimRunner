using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RimRunner;

/// <summary>产品写进实例 Config 的 Prefs 覆盖与 mod 设置都由游戏读取。</summary>
/// <remarks>只验输入清单列出的条目：Prefs 键须是 PrefsData 的字段，mod 设置须有已装载的 Mod 子类读它；玩家基底带来的条目不核对。</remarks>
public class Case_ConfigInputs : RimRunnerRegression
{
    private const string Source = "引擎承诺：产品根写进实例 Config 的 Prefs 覆盖与 mod 设置都被游戏读到；"
                                  + "未被游戏读取的那项由游戏忽略，批在游戏缺省值上执行";

    public override string Id => "CFG1";
    public override RimRunnerLevel Level => RimRunnerLevel.Defs;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        string path = Path.Combine(GenFilePaths.ConfigFolderPath, RimRunnerWireKeys.ConfigManifest.FileName);
        JsonDocument? manifest = null;
        string? failure = null;
        try
        {
            manifest = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            failure = exception.Message;
        }

        ctx.AssertTrue("CFG1-Manifest", manifest != null,
            "无法读取实例 Config 里的输入清单：" + path + "（" + failure + "）",
            RimRunnerExpectationSource.Literal("引擎承诺：写进实例 Config 的产品输入列在输入清单里"));
        if (manifest == null)
        {
            return;
        }

        using (manifest)
        {
            JsonElement root = manifest.RootElement;
            ctx.AssertNoneOffending("CFG1-PrefsKeys", UnknownPrefsKeys(root),
                RimRunnerExpectationSource.Literal(Source));
            ctx.AssertNoneOffending("CFG1-ModSettings", UnreadModSettings(root),
                RimRunnerExpectationSource.Literal(Source));
        }
    }

    private static IEnumerable<string> UnknownPrefsKeys(JsonElement root)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (FieldInfo field in typeof(PrefsData).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            known.Add(field.Name);
            foreach (LoadAliasAttribute alias in field.GetCustomAttributes<LoadAliasAttribute>())
            {
                known.Add(alias.alias);
            }
        }

        var unknown = new List<string>();
        foreach (JsonElement item in Items(root, RimRunnerWireKeys.ConfigManifest.PrefsKeys))
        {
            string key = item.GetProperty(RimRunnerWireKeys.ConfigManifest.Key).GetString() ?? "";
            if (!known.Contains(key))
            {
                unknown.Add(key + "（" + item.GetProperty(RimRunnerWireKeys.ConfigManifest.Layer).GetString() + "）不是 Prefs 的字段");
            }
        }

        return unknown;
    }

    private static IEnumerable<string> UnreadModSettings(JsonElement root)
    {
        var handles = new HashSet<(string Folder, string ModClass)>();
        foreach (Mod handle in LoadedModManager.ModHandles)
        {
            handles.Add((handle.Content.FolderName.ToLowerInvariant(), handle.GetType().Name));
        }

        var unread = new List<string>();
        foreach (JsonElement item in Items(root, RimRunnerWireKeys.ConfigManifest.ModSettings))
        {
            string packageId = item.GetProperty(RimRunnerWireKeys.ConfigManifest.PackageId).GetString() ?? "";
            string modClass = item.GetProperty(RimRunnerWireKeys.ConfigManifest.ModClass).GetString() ?? "";
            var folders = new List<string>();
            foreach (JsonElement folder in item.GetProperty(RimRunnerWireKeys.ConfigManifest.Folders).EnumerateArray())
            {
                folders.Add(folder.GetString() ?? "");
            }

            if (!folders.Any(folder => handles.Contains((folder.ToLowerInvariant(), modClass))))
            {
                string loaded = string.Join("、", LoadedModManager.ModHandles
                    .Where(handle => folders.Any(folder => string.Equals(folder, handle.Content.FolderName, StringComparison.OrdinalIgnoreCase)))
                    .Select(handle => handle.GetType().Name));
                unread.Add(packageId + "/" + modClass + ".xml（" + item.GetProperty(RimRunnerWireKeys.ConfigManifest.Layer).GetString()
                           + "）：装载目录 " + string.Join("、", folders)
                           + (loaded.Length == 0 ? " 下无对应的 Mod 类" : " 的 Mod 类为 " + loaded));
            }
        }

        return unread;
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToList()
            : [];
}
