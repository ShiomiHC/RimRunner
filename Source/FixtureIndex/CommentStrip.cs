using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RimRunnerFixtureIndex;

internal static class CommentStrip
{
    private static readonly Regex TrailingBlank = new(@"[ \t]+(?=\r?$)", RegexOptions.Compiled | RegexOptions.Multiline);

    internal static int Run(string sourceDir, string outDir)
    {
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) continue;

            string target = Path.Combine(outDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, Strip(File.ReadAllText(file)), new UTF8Encoding(false));
            count++;
        }
        Console.WriteLine($"随包源码 {count} 个文件 → {outDir}");
        return count == 0 ? 1 : 0;
    }

    internal static string Strip(string text)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(text).GetRoot();
        IEnumerable<SyntaxTrivia> comments = root.DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia));
        string aligned = TrailingBlank.Replace(root.ReplaceTrivia(comments, (original, _) => original.IsKind(SyntaxKind.MultiLineCommentTrivia)
            ? SyntaxFactory.Whitespace(new string('\n', original.ToFullString().Count(c => c == '\n')))
            : default).ToFullString(), "");

        string[] before = SplitLines(text);
        string[] after = SplitLines(aligned);
        if (before.Length != after.Length) throw new InvalidOperationException("删注释已改变行数");

        var literal = new bool[before.Length];
        TextLineCollection lines = SourceText.From(text).Lines;
        foreach (SyntaxToken token in root.DescendantTokens())
        {
            int first = lines.GetLineFromPosition(token.SpanStart).LineNumber;
            int last = lines.GetLineFromPosition(token.Span.End).LineNumber;
            for (int line = first + 1; line <= last; line++) literal[line] = true;
        }

        var kept = new List<(string Text, bool Literal)>();
        for (int i = 0; i < after.Length; i++)
        {
            bool blank = after[i].Length == 0;
            if (literal[i]) { kept.Add((after[i], true)); continue; }
            if (blank && before[i].Trim().Length > 0) continue;
            if (blank && (kept.Count == 0 || IsBlank(kept[^1]) || !kept[^1].Literal && kept[^1].Text.TrimEnd().EndsWith('{'))) continue;
            if (!blank && after[i].TrimStart().StartsWith('}') && kept.Count > 0 && IsBlank(kept[^1])) kept.RemoveAt(kept.Count - 1);
            kept.Add((after[i], false));
        }
        while (kept.Count > 0 && IsBlank(kept[^1])) kept.RemoveAt(kept.Count - 1);

        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Join(newline, kept.Select(line => line.Text)) + newline;
    }

    private static bool IsBlank((string Text, bool Literal) line) => !line.Literal && line.Text.Length == 0;

    private static string[] SplitLines(string text)
    {
        string[] parts = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        return parts.Length > 0 && parts[^1].Length == 0 ? parts[..^1] : parts;
    }
}
