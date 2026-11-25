using System.Text.RegularExpressions;

namespace RimRunner;

/// <summary>他 mod 归因：判一条错误级日志是否归属他 mod。</summary>
public static class RimRunnerForeignAttribution
{
    private const string EngineMarker = "RimRunner";

    private static string[] OwnMarkers
    {
        get
        {
            field ??= RimRunnerDefScope.OwnedAssemblies
                    .Select(assembly => assembly.GetName().Name!)
                    .Concat([EngineMarker])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            return field;
        }
    }

    private static readonly string[] EngineNamespacePrefixes =
    [
        "Verse.", "RimWorld.", "LudeonTK.", "UnityEngine.", "Unity.", "System.", "Mono.",
        "HarmonyLib.", "MonoMod.", "Microsoft.", "Waylith.", "Brutal.", "RimWorldWaylith.", "UnityStubs.", "<"
    ];

    private static readonly Regex PatchAnnotationLine = new(
        @"^[ \t]*- (?:INNER )?(?:TRANSPILER|PREFIX|POSTFIX|FINALIZER) [^\n]*? <- [^\n]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    internal static string StripPatchAnnotations(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : PatchAnnotationLine.Replace(text, string.Empty);

    internal static bool MentionsOwnAssembly(string? text, string? stackTrace)
    {
        string body = StripPatchAnnotations(text);
        string stack = StripPatchAnnotations(stackTrace);
        return OwnMarkers.Any(marker =>
            body.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0
            || stack.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>归因：返回栈上首个他 mod 帧的类型限定名；不成立返回 null。</summary>
    /// <remarks>不看 Harmony 补丁注记行；text 或 stackTrace 含自家标记即不成立；否则取 stackTrace 里首个带命名空间、不在游戏及其运行时、Unity、Harmony 与 .NET 名单内的帧，找不到即不成立。</remarks>
    public static string? Resolve(string? text, string? stackTrace)
    {
        if (MentionsOwnAssembly(text, stackTrace))
        {
            return null;
        }

        if (string.IsNullOrEmpty(stackTrace))
        {
            return null;
        }

        foreach (string rawLine in StripPatchAnnotations(stackTrace).Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line == "No stack trace.")
            {
                continue;
            }

            if (line.StartsWith("at ", StringComparison.Ordinal))
            {
                line = line[3..].TrimStart();
            }

            int cut = line.Length;
            foreach (char delimiter in new[] { '(', ' ', ':' })
            {
                int index = line.IndexOf(delimiter);
                if (index >= 0 && index < cut) { cut = index; }
            }

            string qualified = line[..cut].TrimStart('.');
            if (qualified.IndexOf('.') < 0
                || EngineNamespacePrefixes.Any(prefix =>
                    qualified.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            if (cut < line.Length && line[cut] == ':')
            {
                return qualified;
            }

            int lastDot = qualified.LastIndexOf('.');
            return lastDot > 0 ? qualified[..lastDot] : qualified;
        }

        return null;
    }
}
