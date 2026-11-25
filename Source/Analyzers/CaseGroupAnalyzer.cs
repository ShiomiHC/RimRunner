using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RimRunnerAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CaseGroupAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RR0001";
    public const string ShapeDiagnosticId = "RR0002";

    private const string RegressionName = "RimRunner.RimRunnerRegression";
    private const string ReadoutName = "RimRunner.RimRunnerReadout";
    private const string GroupName = "RimRunner.RimRunnerGroupAttribute";
    private const string EnvCheckName = "RimRunner.RimRunnerEnvCheckAttribute";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "用例类没有用例组",
        "用例类 {0} 没有用例组：类与它的基类上都没有 [RimRunnerGroup]",
        "RimRunner",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "按组选批时没有组的用例不进批。组名只许英文字母、数字、- 与 _；[RimRunnerGroup] 可挂多个，随基类继承。");

    private static readonly DiagnosticDescriptor ShapeRule = new(
        ShapeDiagnosticId,
        "用例组名不合格",
        "[RimRunnerGroup] 的组名不合格：{0}",
        "RimRunner",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "组名只许英文字母、数字、- 与 _，all 留作「不按组过滤」。");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule, ShapeRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? regression = start.Compilation.GetTypeByMetadataName(RegressionName);
            INamedTypeSymbol? readout = start.Compilation.GetTypeByMetadataName(ReadoutName);
            INamedTypeSymbol? group = start.Compilation.GetTypeByMetadataName(GroupName);
            INamedTypeSymbol? envCheck = start.Compilation.GetTypeByMetadataName(EnvCheckName);
            if (regression == null || readout == null || group == null)
            {
                return;
            }

            start.RegisterSymbolAction(
                symbolContext => Check(symbolContext, regression, readout, group, envCheck),
                SymbolKind.NamedType);
        });
    }

    private static void Check(SymbolAnalysisContext context, INamedTypeSymbol regression, INamedTypeSymbol readout,
        INamedTypeSymbol group, INamedTypeSymbol? envCheck)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        CheckShapes(context, type, group);
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || !DerivesFrom(type, regression, readout))
        {
            return;
        }

        if (envCheck != null && HasOwnAttribute(type, envCheck))
        {
            return;
        }

        for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
        {
            if (HasOwnAttribute(current, group))
            {
                return;
            }
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, type.Locations.Length > 0 ? type.Locations[0] : null,
            type.ToDisplayString()));
    }

    private static void CheckShapes(SymbolAnalysisContext context, INamedTypeSymbol type, INamedTypeSymbol group)
    {
        foreach (AttributeData data in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(data.AttributeClass, group) || data.ConstructorArguments.Length != 1)
            {
                continue;
            }

            TypedConstant argument = data.ConstructorArguments[0];
            if (argument.Kind == TypedConstantKind.Error)
            {
                continue;
            }

            string? problem = argument.Value is string name
                ? RimRunner.RimRunnerCaseGroups.ShapeProblem(name)
                : "组名为 null";
            if (problem != null)
            {
                Location location = data.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
                    ?? (type.Locations.Length > 0 ? type.Locations[0] : Location.None);
                context.ReportDiagnostic(Diagnostic.Create(ShapeRule, location, problem));
            }
        }
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol regression, INamedTypeSymbol readout)
    {
        for (INamedTypeSymbol? current = type.BaseType; current != null; current = current.BaseType)
        {
            INamedTypeSymbol definition = current.OriginalDefinition;
            if (SymbolEqualityComparer.Default.Equals(definition, regression)
                || SymbolEqualityComparer.Default.Equals(definition, readout))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOwnAttribute(INamedTypeSymbol type, INamedTypeSymbol attribute)
    {
        foreach (AttributeData data in type.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute))
            {
                return true;
            }
        }

        return false;
    }
}
