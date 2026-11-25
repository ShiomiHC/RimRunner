using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using RimRunner;

namespace RimRunnerCli;

public static class LiveCompiler
{
    public const string EvalName = "eval";

    public const int MaxImageBytes = 1024 * 1024;

    private const int ShownErrors = 20;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    private static readonly Lock Sync = new();

    private static readonly Dictionary<string, (MetadataReference Reference, Guid Mvid)> References = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, List<(int Line, string Text)>> GlobalUsingsByFile = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, byte[]> Images = new(StringComparer.Ordinal);

    private const int MaxCachedImages = 128;
    private const int MaxCachedReferences = 4096;

    private static CSharpCompilationOptions? options;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, DocumentationMode.None);

    public sealed record Result(byte[]? Image, IReadOnlyList<string> Errors, IReadOnlyList<string> Notes, bool LinkFailure = false);

    private sealed record ReferenceSet(List<MetadataReference> References, List<string> Names, string Fingerprint, List<string> Notes);

    public static Result CompileEval(int number, string productRoot, OrchestratorDescriptor descriptor, string code,
        IReadOnlyList<string> usings, string origin = EvalOrigin)
    {
        lock (Sync)
        {
            if (!TryReferences(number, out ReferenceSet? refs, out string? refsError))
                return new Result(null, [refsError!], [], LinkFailure: true);
            (SyntaxTree? globalUsings, string usingsFingerprint) = GlobalUsings(productRoot, descriptor);
            bool expression = IsExpression(code);
            string key = Hash("eval", code, origin, expression.ToString(), string.Join("\n", usings), refs!.Fingerprint, usingsFingerprint);
            string source = EvalSource(code, expression, usings, key[..8], origin);
            Result result = Compile("RimRunnerEval_" + key[..16], key, refs, globalUsings,
                [CSharpSyntaxTree.ParseText(source, ParseOptions, path: "", encoding: Encoding.UTF8),
                    CSharpSyntaxTree.ParseText(EvalSupport, ParseOptions, path: "", encoding: Encoding.UTF8)]);
            return result with { Errors = [.. result.Errors, .. EvalHints(result.Errors)] };
        }
    }

    internal const string EvalOrigin = "<eval>";

    internal static IEnumerable<string> EvalHints(IReadOnlyList<string> errors)
    {
        if (errors.Any(line => line.StartsWith("CS", StringComparison.Ordinal)))
            yield return "没有位置的错误落在 eval 代码之外的包装代码上：eval 代码按方法体里的语句编译，花括号须配对，类与方法的声明只能写在 <dir>/live/ 下的脚本里";
    }

    public static Result CompileScript(int number, string productRoot, OrchestratorDescriptor descriptor, string scriptPath)
    {
        lock (Sync)
        {
            if (!TryReferences(number, out ReferenceSet? refs, out string? refsError))
                return new Result(null, [refsError!], [], LinkFailure: true);
            return CompileScriptUnlocked(refs!, productRoot, descriptor, scriptPath);
        }
    }

    public static IReadOnlyList<(string Name, Result Result)> CompileScripts(int number, string productRoot,
        OrchestratorDescriptor descriptor, IReadOnlyList<string> scriptPaths, out string? refsError)
    {
        lock (Sync)
        {
            if (!TryReferences(number, out ReferenceSet? refs, out refsError))
                return [];
            return [.. scriptPaths.Select(path => (Path.GetFileNameWithoutExtension(path), CompileScriptUnlocked(refs!, productRoot, descriptor, path)))];
        }
    }

    private static Result CompileScriptUnlocked(ReferenceSet refs, string productRoot, OrchestratorDescriptor descriptor, string scriptPath)
    {
        string text;
        try
        {
            text = ReadSource(scriptPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new Result(null, [$"无法读取脚本 {scriptPath}：{exception.Message}"], []);
        }

        (SyntaxTree? globalUsings, string usingsFingerprint) = GlobalUsings(productRoot, descriptor);
        string key = Hash("script", scriptPath, text, refs.Fingerprint, usingsFingerprint);
        return Compile("RimRunnerScript_" + key[..16], key, refs, globalUsings,
            [CSharpSyntaxTree.ParseText(text, ParseOptions, path: scriptPath, encoding: Encoding.UTF8)]);
    }

    internal static string ReadSource(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static Result Compile(string assemblyName, string key, ReferenceSet refs, SyntaxTree? globalUsings, IReadOnlyList<SyntaxTree> sources)
    {
        if (Images.TryGetValue(key, out byte[]? cached))
            return new Result(cached, [], refs.Notes);
        if (Options() is not { } compileOptions)
        {
            return new Result(null, [$"当前 Roslyn（{typeof(CSharpCompilation).Assembly.GetName().Version}）缺少内部的 IgnoreAccessibility："
                                     + "eval 代码与脚本无法访问 internal 与私有成员，不编译"], []);
        }

        List<SyntaxTree> trees = [CSharpSyntaxTree.ParseText(AccessSupport(refs.Names), ParseOptions, path: "", encoding: Encoding.UTF8), .. sources];
        if (globalUsings != null)
            trees.Add(globalUsings);
        CSharpCompilation compilation = CSharpCompilation.Create(assemblyName, trees, refs.References, compileOptions);
        using var pe = new MemoryStream();
        EmitResult emitted = compilation.Emit(pe, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        Diagnostic[] errors = [.. emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];
        if (!emitted.Success)
        {
            List<string> lines = [.. errors.Take(ShownErrors).Select(Describe)];
            if (errors.Length > ShownErrors)
                lines.Add($"其余 {errors.Length - ShownErrors} 条编译错误未列");
            return new Result(null, lines.Count > 0 ? lines : ["Roslyn 未给出错误"], []);
        }

        byte[] image = pe.ToArray();
        if (image.Length > MaxImageBytes)
            return new Result(null, [$"编译出的程序集 {image.Length / 1024} KB，超过 {MaxImageBytes / 1024} KB 的上限"], []);
        if (Images.Count >= MaxCachedImages)
            Images.Clear();
        Images[key] = image;
        return new Result(image, [], refs.Notes);
    }

    internal static string Describe(Diagnostic diagnostic)
    {
        FileLinePositionSpan span = diagnostic.Location.GetMappedLineSpan();
        string where = span.IsValid && span.Path.Length > 0
            ? $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): "
            : "";
        return where + diagnostic.Id + " " + diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static bool IsExpression(string code) =>
        !string.IsNullOrWhiteSpace(code)
        && !SyntaxFactory.ParseExpression(code, options: ParseOptions, consumeFullText: true).GetDiagnostics().Any();

    internal static string EvalSource(string code, bool expression, IReadOnlyList<string> usings, string tag, string origin = EvalOrigin)
    {
        string line = "#line 1 \"" + origin + "\"\n";
        var source = new StringBuilder();
        foreach (string ns in (string[])["System", "System.Collections.Generic", "System.Linq", "Verse", "RimWorld", "RimRunner", .. usings])
            source.Append("using ").Append(ns).Append(";\n");
        source.Append("namespace RimRunnerEval\n{\n");
        source.Append("public sealed class Eval_").Append(tag).Append(" : global::RimRunner.RimRunnerLiveAction\n{\n");
        source.Append("private global::RimRunner.RimRunnerRegressionContext __ctx;\n");
        source.Append("public override string Title => \"eval\";\n");
        source.Append("public override global::System.Collections.Generic.IEnumerable<global::RimRunner.RimRunnerWait> Run(")
            .Append("global::RimRunner.RimRunnerRegressionContext ctx, global::RimRunner.RimRunnerLiveCall call)\n{\n");
        source.Append("__ctx = ctx;\nglobal::Verse.Map map = ctx.Map;\n");
        if (expression)
            source.Append("__EvalValue(() =>\n").Append(line).Append(code).Append("\n#line default\n);\n");
        else
            source.Append(line).Append(RewriteReturns(code)).Append("\n#line default\n");
        source.Append("yield break;\n}\n");
        source.Append("private void Show(object value, string label = \"值\") => global::RimRunnerEval.EvalShow.Write(__ctx, label, value);\n");
        source.Append("private void __EvalValue(global::System.Func<object> value) => Show(value());\n");
        source.Append("private void __EvalValue(global::System.Action action) => action();\n");
        source.Append("}\n}\n");
        return source.ToString();
    }

    internal static string RewriteReturns(string code)
    {
        const string prefix = "class __C{void __M(){\n";
        SyntaxNode root = CSharpSyntaxTree.ParseText(prefix + code + "\n}}", ParseOptions).GetRoot();
        if (root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(method => method.Identifier.Text == "__M")?.Body is not { } body)
            return code;
        List<(int Start, int Length, string Text)> edits = [];
        foreach (ReturnStatementSyntax statement in body
                     .DescendantNodes(node => node is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or BaseTypeDeclarationSyntax))
                     .OfType<ReturnStatementSyntax>())
        {
            int keyword = statement.ReturnKeyword.SpanStart - prefix.Length;
            int semicolon = statement.SemicolonToken.SpanStart - prefix.Length;
            if (statement.SemicolonToken.IsMissing || keyword < 0 || semicolon + 1 > code.Length)
                continue;
            if (statement.Expression == null)
            {
                edits.Add((keyword, "return".Length, "yield break"));
                continue;
            }

            edits.Add((keyword, "return".Length, "{Show("));
            edits.Add((semicolon, 1, ");yield break;}"));
        }

        var text = new StringBuilder(code);
        foreach ((int start, int length, string replacement) in edits.OrderByDescending(edit => edit.Start))
            text.Remove(start, length).Insert(start, replacement);
        return text.ToString();
    }

    internal const string EvalSupport = """
        namespace RimRunnerEval
        {
            internal static class EvalShow
            {
                private const int Items = 50;

                internal static void Write(global::RimRunner.RimRunnerRegressionContext ctx, string label, object value)
                {
                    if (value is global::System.Collections.IEnumerable items && !(value is string))
                    {
                        int count = 0;
                        foreach (object item in items)
                        {
                            if (count == Items)
                            {
                                Add(ctx, label, "只列前 " + Items + " 项，其余未列");
                                return;
                            }

                            Add(ctx, label + "[" + count + "]", Text(item));
                            count++;
                        }

                        if (count == 0)
                            Add(ctx, label, "空");
                        return;
                    }

                    Add(ctx, label, Text(value));
                }

                private static void Add(global::RimRunner.RimRunnerRegressionContext ctx, string label, string text) =>
                    ctx.AddDiagnostic(global::RimRunner.RimRunnerDiagnosticKind.Measurement, label, text);

                private static string Text(object value)
                {
                    if (value == null)
                        return "<null>";
                    try
                    {
                        return value.ToString() ?? "<null>";
                    }
                    catch (global::System.Exception exception)
                    {
                        return "<ToString 抛出 " + exception.GetType().Name + "：" + exception.Message + ">";
                    }
                }
            }
        }
        """;

    private static string AccessSupport(IEnumerable<string> names)
    {
        var source = new StringBuilder();
        foreach (string name in names)
            source.Append("[assembly: global::System.Runtime.CompilerServices.IgnoresAccessChecksTo(\"").Append(name).Append("\")]\n");
        source.Append("""
            namespace System.Runtime.CompilerServices
            {
                [global::System.AttributeUsage(global::System.AttributeTargets.Assembly, AllowMultiple = true)]
                internal sealed class IgnoresAccessChecksToAttribute : global::System.Attribute
                {
                    public IgnoresAccessChecksToAttribute(string assemblyName)
                    {
                        AssemblyName = assemblyName;
                    }

                    public string AssemblyName { get; }
                }
            }
            """);
        return source.ToString();
    }

    internal static CSharpCompilationOptions? Options()
    {
        if (options != null)
            return options;
        CSharpCompilationOptions plain = PlainOptions();
        Type? flagsType = typeof(CSharpCompilation).Assembly.GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags");
        MethodInfo? with = typeof(CSharpCompilationOptions).GetMethod("WithTopLevelBinderFlags", BindingFlags.Instance | BindingFlags.NonPublic);
        if (flagsType == null || with == null || !Enum.TryParse(flagsType, "IgnoreAccessibility", out object? flag))
            return null;
        options = (CSharpCompilationOptions)with.Invoke(plain, [flag])!;
        return options;
    }

    internal static CSharpCompilationOptions PlainOptions() =>
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Disable, deterministic: true)
            .WithMetadataImportOptions(MetadataImportOptions.All)
            .WithSpecificDiagnosticOptions([new("CS0436", ReportDiagnostic.Suppress)]);

    private static bool TryReferences(int number, out ReferenceSet? set, out string? error)
    {
        set = null;
        InstanceLink.Reply reply = InstanceLinks.Request(number, RimRunnerPipeProtocol.Ops.Assemblies, null, QueryTimeout);
        if (reply.Status != 200 || string.IsNullOrEmpty(reply.Body))
        {
            error = "实例未返回引用集：" +(reply.Status <= InstanceLink.Reply.NoLinkStatus ? reply.ErrorText() : $"状态码 {reply.Status}");
            return false;
        }

        var paths = new Dictionary<string, (string Path, string? Mvid)>(StringComparer.OrdinalIgnoreCase);
        using (JsonDocument doc = JsonDocument.Parse(reply.Body))
        {
            foreach (JsonElement item in doc.RootElement.GetProperty(RimRunnerLiveContract.Assemblies.Loaded).EnumerateArray())
            {
                paths.TryAdd(item.GetProperty(RimRunnerLiveContract.Assemblies.Name).GetString()!,
                    (item.GetProperty(RimRunnerLiveContract.Assemblies.Path).GetString()!, item.GetProperty(RimRunnerLiveContract.Assemblies.Mvid).GetString()));
            }

            foreach (JsonElement item in doc.RootElement.GetProperty(RimRunnerLiveContract.Assemblies.Tpa).EnumerateArray())
            {
                string path = item.GetString()!;
                paths.TryAdd(Path.GetFileNameWithoutExtension(path), (path, null));
            }
        }

        List<MetadataReference> references = [];
        List<string> names = [];
        List<string> fingerprint = [];
        List<string> stale = [];
        foreach ((string name, (string path, string? mvid)) in paths.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (Reference(path) is not { } entry)
                continue;
            references.Add(entry.Reference);
            names.Add(name);
            fingerprint.Add(name + "|" + entry.Mvid.ToString("N"));
            if (mvid != null && !string.Equals(mvid, entry.Mvid.ToString("N"), StringComparison.OrdinalIgnoreCase))
                stale.Add(name);
        }

        List<string> notes = stale.Count == 0
            ? []
            : [$"实例已装载的这些程序集与磁盘上的文件不匹配，按磁盘上的文件编译：{string.Join("、", stale)}"];
        set = new ReferenceSet(references, names, Hash([.. fingerprint]), notes);
        error = null;
        return true;
    }

    private static (MetadataReference Reference, Guid Mvid)? Reference(string path)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
                return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        string key = path + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        if (References.TryGetValue(key, out var cached))
            return cached;
        try
        {
            PortableExecutableReference reference = MetadataReference.CreateFromFile(path);
            Guid mvid = ((AssemblyMetadata)reference.GetMetadata()).GetModules()[0].GetModuleVersionId();
            if (References.Count >= MaxCachedReferences)
                References.Clear();
            References[key] = (reference, mvid);
            return (reference, mvid);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }

    private static (SyntaxTree? Tree, string Fingerprint) GlobalUsings(string productRoot, OrchestratorDescriptor descriptor)
    {
        string projectDir = Path.GetDirectoryName(Path.Combine(productRoot, descriptor.TestMod.Project.Replace('/', Path.DirectorySeparatorChar)))!;
        string joined = GlobalUsingsSource(projectDir);
        return joined.Length == 0
            ? (null, "")
            : (CSharpSyntaxTree.ParseText(joined, ParseOptions, path: "", encoding: Encoding.UTF8), Hash(joined));
    }

    internal static string GlobalUsingsSource(string projectDir)
    {
        if (!Directory.Exists(projectDir))
            return "";
        var source = new StringBuilder();
        foreach (string file in SourceFiles(projectDir))
        {
            foreach ((int line, string text) in FileGlobalUsings(file))
                source.Append("#line ").Append(line).Append(" \"").Append(file).Append("\"\n").Append(text).Append('\n');
        }

        return source.ToString();
    }

    private static IEnumerable<string> SourceFiles(string dir)
    {
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            yield return file;
        foreach (string sub in Directory.EnumerateDirectories(dir).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(sub);
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (string file in SourceFiles(sub))
                yield return file;
        }
    }

    private static List<(int Line, string Text)> FileGlobalUsings(string file)
    {
        var info = new FileInfo(file);
        string key = file + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        if (GlobalUsingsByFile.TryGetValue(key, out List<(int, string)>? cached))
            return cached;
        string text = File.ReadAllText(file);
        List<(int Line, string Text)> found = [];
        if (text.Contains("global using", StringComparison.Ordinal))
        {
            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text, ParseOptions, path: file).GetRoot();
            foreach (UsingDirectiveSyntax directive in root.Usings.Where(directive => directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
                found.Add((directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1, directive.WithoutTrivia().ToString()));
        }

        GlobalUsingsByFile[key] = found;
        return found;
    }

    private static string Hash(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u0001", parts))));
}
