using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RimRunnerAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LiveScriptAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RR0003";

    private const string ActionName = "RimRunner.RimRunnerLiveAction";
    private const string ConditionName = "RimRunner.RimRunnerLiveCondition";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "live 动作或条件写在测试工程里",
        "{0} 为 live {1}，live {2} 只装载 <dir>/live/<名>.cs 脚本（一个文件一个动作或条件，文件名即调用的名字），不找测试工程里的类",
        "RimRunner",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "live do / live until 每次调用时编译 <dir>/live/<名>.cs 并发给实例装载；<dir> 是编排描述符的 dir。脚本带测试工程的 global using，可访问引擎、游戏与测试程序集的非公开成员，共用的代码留在测试工程。");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? action = start.Compilation.GetTypeByMetadataName(ActionName);
            INamedTypeSymbol? condition = start.Compilation.GetTypeByMetadataName(ConditionName);
            if (action == null || condition == null)
            {
                return;
            }

            start.RegisterSymbolAction(symbolContext => Check(symbolContext, action, condition), SymbolKind.NamedType);
        });
    }

    private static void Check(SymbolAnalysisContext context, INamedTypeSymbol action, INamedTypeSymbol condition)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
        {
            return;
        }

        for (INamedTypeSymbol? current = type.BaseType; current != null; current = current.BaseType)
        {
            bool isAction = SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, action);
            if (isAction || SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, condition))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, type.Locations.Length > 0 ? type.Locations[0] : null,
                    type.ToDisplayString(), isAction ? "动作" : "条件", isAction ? "do" : "until"));
                return;
            }
        }
    }
}
