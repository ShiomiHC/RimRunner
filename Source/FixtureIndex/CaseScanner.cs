using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RimRunnerFixtureIndex;

internal sealed record CaseEntry(
    string Class, string File, int Line, string Kind, bool RealFrame, bool EnvCheck,
    List<string> Ids, bool Parameterized, Doc Doc, List<string> Groups);

internal sealed record CaseScan(List<CaseEntry> Cases, List<string> Problems, List<string> Forms, List<string> Warnings);

internal static class CaseScanner
{
    private static readonly string[] CaseTags = ["summary", "remarks"];

    private const string RealFrameInterface = "IRimRunnerRealFrameCase";
    private const string EnvCheckAttribute = "RimRunnerEnvCheck";
    private const string GroupAttribute = "RimRunnerGroup";
    private const string ParameterizedAttribute = "RimRunnerParameterizedCase";
    private const string ExpandMethod = "Expand";

    private static readonly Dictionary<string, string> KindBases = new(StringComparer.Ordinal)
    {
        ["RimRunnerRegression"] = "regression",
        ["RimRunnerReadout"] = "readout",
    };

    internal static CaseScan Scan(string root)
    {
        var files = SourceFiles.Under(root)
            .Select(path => (file: SourceFiles.Relative(root, path), tree: SourceFiles.Parse(path)))
            .ToList();
        var index = new CaseIndex(files.Select(f => f.tree));

        var problems = new List<string>();
        var forms = new List<string>();
        var warnings = new List<string>();
        var cases = new List<CaseEntry>();
        var declared = files.SelectMany(f => index.TopLevelCases(f.tree).Select(decl => (f.file, decl)))
            .GroupBy(item => item.decl.Identifier.ValueText);
        foreach (var group in declared)
        {
            var parts = group.ToList();
            var (file, decl) = parts[0];
            string name = group.Key;
            CaseInfo info = index.Resolve(decl)!;
            var holder = parts.FirstOrDefault(p => DocLinesOf(p.decl).Count > 0);
            if (holder.decl == null) holder = parts[0];
            Doc doc = DocForm.Parse(DocLinesOf(holder.decl), $"{holder.file} 用例类 {name}", problems, forms, CaseTags);

            bool parameterized = parts.Any(p => HasAttribute(p.decl, ParameterizedAttribute));
            (List<string> ids, bool idUnresolved) = parameterized ? index.ReadExpandIds(decl) : index.ReadId(decl);
            if (idUnresolved)
            {
                warnings.Add($"{file} 用例类 {name}：无法解析 Id（" + (parameterized
                    ? $"{ExpandMethod}() 里没有 new {name}(\"Id\", …)，或其首个实参既不是字面量也不是可解析的常量）"
                    : "Id 的表达式既不是字面量也不是可解析的常量）"));
            }

            (List<string> groups, bool unresolved) = index.ReadGroups(decl);
            if (unresolved) warnings.Add($"{file} 用例类 {name}：[RimRunnerGroup] 的组名无法读取（实参既不是字面量也不是可解析的常量）");

            cases.Add(new CaseEntry(name, file, FixtureScanner.LineOf(FixtureScanner.DeclarationStart(decl)), info.Kind,
                info.RealFrame, parts.Any(p => HasAttribute(p.decl, EnvCheckAttribute)), ids, parameterized, doc,
                groups));
        }

        return new CaseScan(cases, problems, forms, warnings);
    }

    internal static List<(int Line, string Text)> DocLinesOf(ClassDeclarationSyntax decl)
    {
        var lines = FixtureScanner.DocLines(decl);
        if (decl.AttributeLists.Count == 0) return lines;
        SyntaxToken afterAttributes = FixtureScanner.FirstNonAttributeToken(decl);
        var text = decl.SyntaxTree.GetText();
        foreach (SyntaxTrivia trivia in afterAttributes.LeadingTrivia)
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) continue;
            lines.AddRange(FixtureScanner.LinesOf(text, trivia.Span));
        }
        return lines;
    }

    private static bool HasAttribute(ClassDeclarationSyntax decl, string name) =>
        decl.AttributeLists.SelectMany(list => list.Attributes)
            .Any(a => LastIdentifier(a.Name) is string id && (id == name || id == name + "Attribute"));

    internal static string? LastIdentifier(NameSyntax name) => name switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => null,
    };

    internal sealed record CaseInfo(string Kind, bool RealFrame);

    internal sealed class CaseIndex
    {
        private readonly Dictionary<string, List<ClassDeclarationSyntax>> classes = new(StringComparer.Ordinal);

        internal CaseIndex(IEnumerable<SyntaxTree> trees)
        {
            foreach (SyntaxTree tree in trees)
            {
                foreach (ClassDeclarationSyntax decl in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (!classes.TryGetValue(decl.Identifier.ValueText, out var list)) classes[decl.Identifier.ValueText] = list = [];
                    list.Add(decl);
                }
            }
        }

        internal IEnumerable<ClassDeclarationSyntax> TopLevelCases(SyntaxTree tree) => TopLevelCases([tree]);

        internal IEnumerable<ClassDeclarationSyntax> TopLevelCases(IEnumerable<SyntaxTree> trees)
        {
            foreach (SyntaxTree tree in trees)
            {
                foreach (ClassDeclarationSyntax decl in tree.GetCompilationUnitRoot()
                             .DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                             .OfType<ClassDeclarationSyntax>())
                {
                    if (decl.Modifiers.Any(SyntaxKind.AbstractKeyword)) continue;
                    if (Resolve(decl) != null) yield return decl;
                }
            }
        }

        internal CaseInfo? Resolve(ClassDeclarationSyntax decl)
        {
            string? kind = null;
            bool realFrame = false;
            var visited = new HashSet<string>(StringComparer.Ordinal) { decl.Identifier.ValueText };
            var pending = new Queue<ClassDeclarationSyntax>();
            pending.Enqueue(decl);
            while (pending.Count > 0)
            {
                ClassDeclarationSyntax current = pending.Dequeue();
                foreach (ClassDeclarationSyntax part in Parts(current.Identifier.ValueText).Prepend(current))
                {
                    if (part.BaseList == null) continue;
                    foreach (BaseTypeSyntax baseType in part.BaseList.Types)
                    {
                        string? name = baseType.Type switch
                        {
                            NameSyntax n => LastIdentifier(n),
                            _ => null,
                        };
                        if (name == null) continue;
                        if (name == RealFrameInterface) realFrame = true;
                        if (KindBases.TryGetValue(name, out string? mapped)) kind ??= mapped;
                        if (visited.Add(name))
                        {
                            foreach (ClassDeclarationSyntax baseDecl in Parts(name)) pending.Enqueue(baseDecl);
                        }
                    }
                }
            }

            return kind == null ? null : new CaseInfo(kind, realFrame);
        }

        private IReadOnlyList<ClassDeclarationSyntax> Parts(string name) =>
            classes.TryGetValue(name, out var list) ? list : [];

        private IEnumerable<ClassDeclarationSyntax> Chain(ClassDeclarationSyntax decl)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            pending.Enqueue(decl.Identifier.ValueText);
            while (pending.Count > 0)
            {
                string name = pending.Dequeue();
                if (!visited.Add(name)) continue;
                foreach (ClassDeclarationSyntax part in Parts(name))
                {
                    yield return part;
                    if (part.BaseList == null) continue;
                    foreach (BaseTypeSyntax baseType in part.BaseList.Types)
                    {
                        if (baseType.Type is NameSyntax n && LastIdentifier(n) is string baseName && classes.ContainsKey(baseName))
                            pending.Enqueue(baseName);
                    }
                }
            }
        }

        internal (List<string> Groups, bool Unresolved) ReadGroups(ClassDeclarationSyntax decl)
        {
            ClassDeclarationSyntax[] chain = [.. Chain(decl)];
            var groups = new List<string>();
            bool unresolved = false;
            foreach (AttributeSyntax attribute in chain.SelectMany(c => c.AttributeLists).SelectMany(list => list.Attributes))
            {
                if (LastIdentifier(attribute.Name) is not string id || (id != GroupAttribute && id != GroupAttribute + "Attribute"))
                    continue;
                ExpressionSyntax? argument = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                if (argument != null && Literal(argument, chain, 0) is string group) groups.Add(group);
                else unresolved = true;
            }

            groups = [.. groups.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            return (groups, unresolved);
        }

        internal (List<string> Ids, bool Unresolved) ReadId(ClassDeclarationSyntax decl)
        {
            ClassDeclarationSyntax[] chain = [.. Chain(decl)];
            ExpressionSyntax? expression = chain.SelectMany(c => c.Members.OfType<PropertyDeclarationSyntax>())
                .Where(p => p.Identifier.ValueText == "Id")
                .Select(PropertyValue)
                .FirstOrDefault(e => e != null);
            return expression != null && Literal(expression, chain, 0) is string id ? ([id], false) : ([], true);
        }

        internal (List<string> Ids, bool Unresolved) ReadExpandIds(ClassDeclarationSyntax decl)
        {
            string name = decl.Identifier.ValueText;
            ClassDeclarationSyntax[] chain = [.. Chain(decl)];
            List<string?> ids =
            [
                .. Parts(name).SelectMany(part => part.Members.OfType<MethodDeclarationSyntax>())
                    .Where(method => method.Identifier.ValueText == ExpandMethod && method.ParameterList.Parameters.Count == 0)
                    .SelectMany(method => method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                    .Where(creation => creation.Type is NameSyntax type && LastIdentifier(type) == name)
                    .Select(creation => creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression)
                    .Select(argument => argument == null ? null : Literal(argument, chain, 0))
            ];
            List<string> read = [.. ids.OfType<string>().Distinct(StringComparer.Ordinal)];
            return (read, ids.Count == 0 || ids.Contains(null));
        }

        private static ExpressionSyntax? PropertyValue(PropertyDeclarationSyntax property)
        {
            if (property.ExpressionBody != null) return property.ExpressionBody.Expression;
            AccessorDeclarationSyntax? getter = property.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            if (getter?.ExpressionBody != null) return getter.ExpressionBody.Expression;
            return getter?.Body?.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression;
        }

        private static string? Literal(ExpressionSyntax expression, ClassDeclarationSyntax[] chain, int depth)
        {
            expression = Unwrap(expression);
            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                return literal.Token.ValueText;
            if (depth >= 4) return null;
            string? name = expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                _ => null,
            };
            if (name == null) return null;

            foreach (ClassDeclarationSyntax cls in chain)
            {
                foreach (MemberDeclarationSyntax member in cls.Members)
                {
                    ExpressionSyntax? value = member switch
                    {
                        FieldDeclarationSyntax field when field.Modifiers.Any(SyntaxKind.ConstKeyword) || field.Modifiers.Any(SyntaxKind.StaticKeyword) =>
                            field.Declaration.Variables.FirstOrDefault(v => v.Identifier.ValueText == name)?.Initializer?.Value,
                        PropertyDeclarationSyntax property when property.Identifier.ValueText == name =>
                            property.ExpressionBody?.Expression ?? property.Initializer?.Value,
                        _ => null,
                    };
                    if (value != null) return Literal(value, chain, depth + 1);
                }
            }

            return null;
        }

        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
            return expression;
        }
    }
}
