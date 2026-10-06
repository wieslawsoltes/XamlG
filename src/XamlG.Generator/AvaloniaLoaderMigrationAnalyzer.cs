using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using XamlG.Frameworks.Avalonia;

namespace XamlG.Generator;

/// <summary>Diagnoses a remaining runtime-loader invocation, but does not reject calls
/// that Roslyn has actually redirected to an emitted source-level loader adapter.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AvaloniaLoaderMigrationAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor LoaderCall = new(
        "XG2001", "Compile the component loader adapter",
        "'{0}' has no compiled loader interceptor. Include the component's XAML in XamlG compilation, use its generated initializer, or call its generated populate method explicitly.",
        "XamlG.Migration", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(LoaderCall);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var options = GeneratorOptions.Read(start.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
            if (!options.Enabled) return;
            var loader = start.Compilation.GetTypeByMetadataName(AvaloniaMetadata.Loader);
            if (loader == null) return;
            start.RegisterSyntaxNodeAction(nodeContext =>
            {
                var invocation = (InvocationExpressionSyntax)nodeContext.Node;
                if (nodeContext.SemanticModel.GetSymbolInfo(invocation, nodeContext.CancellationToken).Symbol is not IMethodSymbol method ||
                    method.Name != AvaloniaMetadata.Load || !SymbolEqualityComparer.Default.Equals(method.ContainingType, loader)) return;

                // Analyzer execution sees generator output. Ask Roslyn which method will
                // execute: matching a namespace or merely finding generated attributes
                // would incorrectly accept uncovered or malformed interception attempts.
                if (nodeContext.SemanticModel.GetInterceptorMethod(invocation, nodeContext.CancellationToken) != null) return;

                nodeContext.ReportDiagnostic(Diagnostic.Create(LoaderCall, invocation.GetLocation(), method.ToDisplayString()));
            }, SyntaxKind.InvocationExpression);
        });
    }
}
