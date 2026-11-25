using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RimRunnerFixtureIndex;

internal enum Profile
{
    Engine,

    Product,
}

internal sealed class Doc
{
    internal static readonly Doc Empty = new();

    internal string? Summary { get; set; }
    internal string? Remarks { get; set; }
    internal List<(string Name, string Text)> Params { get; } = [];
    internal string? Returns { get; set; }
    internal List<(string Cref, string Text)> Exceptions { get; } = [];
}

internal sealed record Member(string Signature, int Line, Doc Doc);

internal sealed class Unit
{
    internal required string Name { get; init; }
    internal required string File { get; init; }
    internal required int Line { get; init; }
    internal required string Kind { get; init; }
    internal Doc Doc { get; set; } = Doc.Empty;
    internal List<Member> Members { get; } = [];
}

internal sealed record ScanResult(List<Unit> Units, List<string> Problems, List<string> Forms);

internal static class FixtureScanner
{
    internal const int SummaryMax = 50;
    internal const int ShortTagMax = 60;
    internal const int RemarksMax = 120;
    internal static readonly string[] AuthoringTags = ["summary", "remarks", "param", "returns", "exception"];

    internal static ScanResult Scan(string root, Profile profile)
    {
        var problems = new List<string>();
        var forms = new List<string>();
        var units = new List<Unit>();
        var files = SourceFiles.Under(root)
            .Select(path => (path, file: SourceFiles.Relative(root, path), tree: SourceFiles.Parse(path)))
            .ToList();

        var markedNames = new HashSet<string>(StringComparer.Ordinal);
        var caseIndex = new CaseScanner.CaseIndex(files.Select(f => f.tree));
        var caseTypes = new HashSet<ClassDeclarationSyntax>(caseIndex.TopLevelCases(files.Select(f => f.tree)));
        if (profile == Profile.Engine)
        {
            foreach (var (_, _, tree) in files)
            {
                foreach (BaseTypeDeclarationSyntax type in TopLevelTypes(tree.GetCompilationUnitRoot()))
                {
                    if (type is ClassDeclarationSyntax cls && caseTypes.Contains(cls)) continue;
                    if (DocLines(type).Count > 0) markedNames.Add(type.Identifier.ValueText);
                }
            }
        }

        foreach (var (_, file, tree) in files)
        {
            new FileScan(file, tree, profile, markedNames, caseTypes, units, problems, forms).Run();
        }

        var typeDoc = new Dictionary<string, Doc>(StringComparer.Ordinal);
        foreach (Unit unit in units)
        {
            if (unit.Doc.Summary != null) typeDoc.TryAdd(unit.Name, unit.Doc);
        }
        foreach (Unit unit in units)
        {
            if (unit.Doc.Summary == null && typeDoc.TryGetValue(unit.Name, out Doc? borrowed)) unit.Doc = borrowed;
        }

        return new ScanResult(units, problems, forms);
    }

    private static IEnumerable<BaseTypeDeclarationSyntax> TopLevelTypes(CompilationUnitSyntax root) =>
        root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<BaseTypeDeclarationSyntax>();

    internal static List<(int Line, string Text)> DocLines(SyntaxNode node)
    {
        var lines = new List<(int, string)>();
        SourceText text = node.SyntaxTree.GetText();
        foreach (SyntaxTrivia trivia in node.GetLeadingTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) continue;
            lines.AddRange(LinesOf(text, trivia.Span));
        }
        return lines;
    }

    internal static IEnumerable<(int, string)> LinesOf(SourceText text, TextSpan span)
    {
        LinePositionSpan pos = text.Lines.GetLinePositionSpan(span);
        int last = pos.End.Character == 0 && pos.End.Line > pos.Start.Line ? pos.End.Line - 1 : pos.End.Line;
        for (int i = pos.Start.Line; i <= last; i++)
        {
            string line = text.Lines[i].ToString().Trim();
            if (line.StartsWith("///", StringComparison.Ordinal)) yield return (i + 1, line[3..].Trim());
            else if (line.StartsWith("/**", StringComparison.Ordinal)) yield return (i + 1, line);
        }
    }

    internal static int LineOf(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static bool IsHarmonyPatch(MemberDeclarationSyntax node) =>
        node.AttributeLists.SelectMany(list => list.Attributes).Any(a => a.Name.ToString().Contains("HarmonyPatch", StringComparison.Ordinal));

    private enum Access { Public, Internal, Hidden }

    private static Access AccessOf(SyntaxTokenList modifiers, bool topLevel)
    {
        if (modifiers.Any(SyntaxKind.PrivateKeyword) || modifiers.Any(SyntaxKind.ProtectedKeyword)) return Access.Hidden;
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return Access.Public;
        if (modifiers.Any(SyntaxKind.InternalKeyword)) return Access.Internal;
        return topLevel ? Access.Internal : Access.Hidden;
    }

    private static bool IsAccessModifier(SyntaxToken token) =>
        token.IsKind(SyntaxKind.PublicKeyword) || token.IsKind(SyntaxKind.InternalKeyword)
        || token.IsKind(SyntaxKind.PrivateKeyword) || token.IsKind(SyntaxKind.ProtectedKeyword);

    private static string SignatureOf(MemberDeclarationSyntax member)
    {
        SyntaxToken first = member.Modifiers.Count > 0 ? member.Modifiers.First() : FirstNonAttributeToken(member);
        int end = member switch
        {
            BaseMethodDeclarationSyntax method => method.ParameterList.Span.End,
            DelegateDeclarationSyntax del => del.ParameterList.Span.End,
            PropertyDeclarationSyntax property => property.Identifier.Span.End,
            EventDeclarationSyntax ev => ev.Identifier.Span.End,
            IndexerDeclarationSyntax indexer => indexer.ParameterList.Span.End,
            BaseFieldDeclarationSyntax field when field.Modifiers.Any(SyntaxKind.ConstKeyword) => field.Declaration.Span.End,
            BaseFieldDeclarationSyntax field => FieldEnd(field.Declaration),
            _ => throw new InvalidOperationException($"签名不认 {member.Kind()}"),
        };

        var span = TextSpan.FromBounds(first.SpanStart, end);
        SourceText text = member.SyntaxTree.GetText();
        var raw = new StringBuilder(text.ToString(span));
        foreach (SyntaxTrivia trivia in member.DescendantTrivia(span))
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) continue;
            if (trivia.SpanStart < span.Start || trivia.Span.End > span.End) continue;
            for (int i = trivia.SpanStart; i < trivia.Span.End; i++) raw[i - span.Start] = ' ';
        }

        string signature = Regex.Replace(raw.ToString(), @"\s+", " ");
        signature = Regex.Replace(signature, @"\(\s+", "(");
        signature = Regex.Replace(signature, @"\s+\)", ")");
        signature = Regex.Replace(signature, @"^(?:public|internal)\s+", "");
        signature = Regex.Replace(signature, @"^sealed\s+", "");
        return signature.Trim();
    }

    private static int FieldEnd(VariableDeclarationSyntax declaration) =>
        declaration.Variables.FirstOrDefault(v => v.Initializer != null)?.Initializer!.SpanStart ?? declaration.Span.End;

    internal static SyntaxToken FirstNonAttributeToken(MemberDeclarationSyntax member) =>
        member.AttributeLists.Count > 0 ? member.AttributeLists.Last().GetLastToken().GetNextToken() : member.GetFirstToken();

    internal static SyntaxToken DeclarationStart(MemberDeclarationSyntax member) =>
        member.Modifiers.Count > 0 ? member.Modifiers.First() : FirstNonAttributeToken(member);

    private sealed class FileScan(
        string file,
        SyntaxTree tree,
        Profile profile,
        HashSet<string> markedNames,
        HashSet<ClassDeclarationSyntax> caseTypes,
        List<Unit> units,
        List<string> problems,
        List<string> forms)
    {
        private readonly HashSet<int> docClaimed = [];

        internal void Run()
        {
            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
            foreach (BaseTypeDeclarationSyntax type in TopLevelTypes(root)) VisitTopLevel(type);

            if (profile != Profile.Engine) return;
            SourceText text = tree.GetText();
            foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: false))
            {
                if (!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                    && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) continue;
                foreach (var (line, _) in LinesOf(text, trivia.Span))
                {
                    if (!docClaimed.Contains(line)) forms.Add($"{file}:{line}：作者面以外不许出现 ///——编译器把它写进 RimRunner.xml；理由类注释写 //");
                }
            }
        }

        private void VisitTopLevel(BaseTypeDeclarationSyntax type)
        {
            if (IsHarmonyPatch(type)) return;
            if (type is ClassDeclarationSyntax caseClass && caseTypes.Contains(caseClass))
            {
                Claim(CaseScanner.DocLinesOf(caseClass));
                return;
            }
            string name = type.Identifier.ValueText
                + (type is TypeDeclarationSyntax { TypeParameterList: { } parameters } ? parameters.ToString() : "");
            Access access = AccessOf(type.Modifiers, topLevel: true);

            if (profile == Profile.Engine)
            {
                if (!markedNames.Contains(type.Identifier.ValueText) || access == Access.Hidden) return;
                var docs = DocLines(type);
                Doc summary = docs.Count > 0 ? DocOf(docs, $"{file} 类型 {name}") : Doc.Empty;
                if (access != Access.Public)
                {
                    problems.Add($"{file} 类型 {name}：作者面类型须为 public（引擎外够不着 internal）；不是作者面就删摘要");
                    return;
                }
                AddUnit(type, name, summary);
                return;
            }

            AddUnit(type, name, DocOf(DocLines(type), $"{file} 类型 {name}"));
        }

        private void AddUnit(BaseTypeDeclarationSyntax type, string name, Doc doc)
        {
            var unit = new Unit { Name = name, File = file, Line = LineOf(DeclarationStart(type)), Kind = type switch { TypeDeclarationSyntax t => t.Keyword.ValueText, EnumDeclarationSyntax e => e.EnumKeyword.ValueText, _ => "?" }, Doc = doc };
            units.Add(unit);
            VisitBody(unit, type);
        }

        private void VisitBody(Unit owner, BaseTypeDeclarationSyntax type)
        {
            if (type is EnumDeclarationSyntax enumType)
            {
                if (profile != Profile.Engine) return;
                foreach (EnumMemberDeclarationSyntax value in enumType.Members)
                {
                    string valueName = value.Identifier.ValueText;
                    owner.Members.Add(new Member(valueName, LineOf(value.Identifier), DocOf(DocLines(value), $"{file} {owner.Name}.{valueName}")));
                }
                return;
            }

            bool isInterface = type is InterfaceDeclarationSyntax;
            foreach (MemberDeclarationSyntax member in ((TypeDeclarationSyntax)type).Members)
            {
                if (member is BaseTypeDeclarationSyntax nested)
                {
                    VisitNested(owner, nested);
                    continue;
                }

                if (member is not (BaseMethodDeclarationSyntax or DelegateDeclarationSyntax or BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax))
                {
                    problems.Add($"{file}:{LineOf(member.GetFirstToken())}：无法识别的成员种类 {member.Kind()}——改工具，不要绕开");
                    continue;
                }

                Access access = isInterface && !member.Modifiers.Any(IsAccessModifier)
                    ? Access.Public
                    : AccessOf(member.Modifiers, topLevel: false);
                if (access == Access.Hidden) continue;

                string signature = SignatureOf(member);
                var docs = DocLines(member);
                if (access == Access.Internal && profile == Profile.Engine)
                {
                    if (docs.Count > 0)
                    {
                        Claim(docs);
                        problems.Add($"{file} {owner.Name}.{signature}：internal 成员带摘要——是作者面就公开，不是就删摘要");
                    }
                    continue;
                }

                owner.Members.Add(new Member(signature, LineOf(DeclarationStart(member)), DocOf(docs, $"{file} {owner.Name}.{signature}")));
            }
        }

        private void VisitNested(Unit owner, BaseTypeDeclarationSyntax nested)
        {
            if (IsHarmonyPatch(nested)) return;
            Access access = AccessOf(nested.Modifiers, topLevel: false);
            if (access == Access.Hidden) return;
            string name = $"{owner.Name}.{nested.Identifier.ValueText}";
            var docs = DocLines(nested);
            if (access == Access.Internal && profile == Profile.Engine)
            {
                if (docs.Count > 0)
                {
                    Claim(docs);
                    problems.Add($"{file} 类型 {name}：作者面类型须为 public（引擎外够不着 internal）；不是作者面就删摘要");
                }
                return;
            }
            AddUnit(nested, name, DocOf(docs, $"{file} 类型 {name}"));
        }

        private void Claim(List<(int Line, string Text)> docs)
        {
            foreach (var (line, _) in docs) docClaimed.Add(line);
        }

        private Doc DocOf(List<(int Line, string Text)> docs, string where)
        {
            Claim(docs);
            return DocForm.Parse(docs, where, problems, forms, AuthoringTags);
        }
    }
}
