using System.Text.RegularExpressions;
using Microsoft.Win32;
using RimTooling.RimWorld;

namespace RimRunnerCli;

internal static partial class SteamGame
{
    public const string Source = "Steam 安装信息";

    internal static Func<string?>? FindExeOverride;

    public static string? FindExe() => FindExeOverride != null ? FindExeOverride() : FindExe(SteamDir());

    internal static string? FindExe(string? steamDir)
    {
        if (string.IsNullOrWhiteSpace(steamDir)) return null;
        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(steamDir, "steamapps", "libraryfolders.vdf"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return LibrariesWithGame(text)
            .Select(library => CommandContext.FullPath(Path.Combine(library, "steamapps", "common", "RimWorld", "RimWorld.exe")))
            .FirstOrDefault(File.Exists);
    }

    internal static IEnumerable<string> LibrariesWithGame(string libraryFolders)
    {
        MatchCollection paths = LibraryPath().Matches(libraryFolders);
        for (int i = 0; i < paths.Count; i++)
        {
            int start = paths[i].Index + paths[i].Length;
            int end = i + 1 < paths.Count ? paths[i + 1].Index : libraryFolders.Length;
            if (libraryFolders.AsSpan(start, end - start).Contains($"\"{SteamWorkshop.RimWorldAppId}\"", StringComparison.Ordinal))
                yield return paths[i].Groups[1].Value.Replace(@"\\", @"\");
        }
    }

    private static string? SteamDir() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPath();
}
