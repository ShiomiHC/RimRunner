using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RimRunnerFixtureIndex;

internal static class SourceFiles
{
    private static readonly CSharpParseOptions Options = new(LanguageVersion.Latest, DocumentationMode.Parse);

    internal static List<string> Under(string root)
    {
        var output = new List<string>();
        Walk(root, output);
        return output;
    }

    private static void Walk(string dir, List<string> output)
    {
        var entries = Directory.EnumerateFileSystemEntries(dir).ToList();
        entries.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        foreach (string path in entries)
        {
            string name = Path.GetFileName(path);
            if (Directory.Exists(path))
            {
                if (name is not ("bin" or "obj")) Walk(path, output);
            }
            else if (name.EndsWith(".cs", StringComparison.Ordinal)) output.Add(path);
        }
    }

    internal static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    internal static SyntaxTree Parse(string path) =>
        CSharpSyntaxTree.ParseText(File.ReadAllText(path), Options, path);
}
