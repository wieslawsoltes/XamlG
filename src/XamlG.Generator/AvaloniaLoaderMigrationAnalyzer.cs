using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using XamlG.Frameworks.Avalonia;
using XamlG.Roslyn;

namespace XamlG.Generator;

/// <summary>Source generators cannot rewrite existing code. Fail explicitly instead of leaving a removed IL-loader path in an application.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AvaloniaLoaderMigrationAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor LoaderCall = new(
        "XG2001", "Replace the runtime XAML loader", "'{0}' is an XamlX loading path. Remove the handwritten InitializeComponent method and use XamlG's generated initializer, or call the generated populate method explicitly.",
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
                nodeContext.ReportDiagnostic(Diagnostic.Create(LoaderCall, invocation.GetLocation(), method.ToDisplayString()));
            }, SyntaxKind.InvocationExpression);
        });
    }
}
