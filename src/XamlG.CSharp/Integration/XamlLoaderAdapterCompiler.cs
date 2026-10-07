using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Roslyn;

namespace XamlG.CSharp.Integration;

/// <summary>Resolves actual loader symbols and emits call-site adapters. No source text is
/// rewritten and no application code is executed. All hosts use this same integration stage.</summary>
public sealed class XamlLoaderAdapterCompiler
{
    public XamlSourceIntegrationResult Compile(CSharpCompilation compilation, XamlProjectCompilation project,
        XamlLoaderConfiguration? configuration, CancellationToken cancellationToken = default)
    {
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        if (project == null) throw new ArgumentNullException(nameof(project));
        if (configuration == null) return XamlSourceIntegrationResult.Empty;
        var loader = compilation.GetTypeByMetadataName(configuration.TypeMetadataName);
        if (loader == null) return XamlSourceIntegrationResult.Empty;
        if (!project.Documents.IsEmpty && project.Documents.All(document => document.Document.IsSkipped) && !project.Resources.Resources.Any())
            return XamlSourceIntegrationResult.Empty;
        bool IsSkipped(ITypeSymbol? type) => type != null &&
            !project.Documents.Any(document => !document.Document.IsSkipped && SymbolEqualityComparer.Default.Equals(document.Document.ClassSymbol, type)) &&
            project.Documents.Any(document => document.Document.IsSkipped && SymbolEqualityComparer.Default.Equals(document.Document.ClassSymbol, type));
        var calls = new List<LoaderCall>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var tree in compilation.SyntaxTrees.OrderBy(t => t.FilePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = tree.GetRoot(cancellationToken);
            var model = compilation.GetSemanticModel(tree);
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = invocation.Expression is MemberAccessExpressionSyntax access ? access.Name.Identifier.ValueText :
                    invocation.Expression is SimpleNameSyntax simple ? simple.Identifier.ValueText : string.Empty;
                if (name != configuration.MethodName || model.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation ||
                    !SymbolEqualityComparer.Default.Equals(operation.TargetMethod.ContainingType, loader)) continue;
                var method = operation.TargetMethod;
                if (!TryKind(method, out var kind, out var hasServices))
                { diagnostics.Add(LoaderDiagnostics.Error(invocation.GetLocation(), "The configured loader overload is not supported by the typed loader contract.")); continue; }
                XamlProjectDocumentResult? target = null;
                if (kind == LoaderCallKind.Object)
                {
                    IOperation value = operation.Arguments.Single(a => a.Parameter?.Ordinal == method.Parameters.Length - 1).Value;
                    while (value is IConversionOperation conversion) value = conversion.Operand;
                    target = project.Documents.FirstOrDefault(d => !d.Document.IsSkipped && SymbolEqualityComparer.Default.Equals(d.Document.ClassSymbol, value.Type));
                    if (IsSkipped(value.Type)) continue;
                    if (target != null && (!target.Output.Success || !target.Document.Options.GenerateInitializeComponent || !CanReference(compilation, target.Document.ClassSymbol!)))
                    { diagnostics.Add(LoaderDiagnostics.Error(invocation.GetLocation(), "This source component cannot expose a compiled initializer. Resolve its XAML diagnostics, enable initialization, and use an accessible nongeneric component or its explicit Populate API.")); continue; }
                }
                if (IsExpressionTree(invocation, model, cancellationToken))
                { diagnostics.Add(LoaderDiagnostics.Error(invocation.GetLocation(), "Loader calls in expression trees require an explicit generated factory; call-site interception cannot preserve the expression tree contract.")); continue; }
                if (tree.Options is CSharpParseOptions options && options.LanguageVersion < LanguageVersion.CSharp11)
                { diagnostics.Add(LoaderDiagnostics.Error(invocation.GetLocation(), "Compiled loader adapters require C# 11 or later and the supported Roslyn SDK.")); continue; }
#pragma warning disable RSEXPERIMENTAL002
                var location = model.GetInterceptableLocation(invocation, cancellationToken);
#pragma warning restore RSEXPERIMENTAL002
                if (location == null)
                { diagnostics.Add(LoaderDiagnostics.Error(invocation.GetLocation(), "Roslyn cannot provide a stable interception location for this loader call.")); continue; }
#pragma warning disable RSEXPERIMENTAL002
                calls.Add(new(invocation, method, location.GetInterceptsLocationAttributeSyntax(), kind, hasServices, target));
#pragma warning restore RSEXPERIMENTAL002
            }
            // Delegates and function pointers do not have interceptable invocation locations.
            // Never silently leave a known method-group use bound to a throwing placeholder.
            foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == configuration.MethodName))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExpressionSyntax expression = identifier.Parent is MemberAccessExpressionSyntax member && ReferenceEquals(member.Name, identifier) ? member : identifier;
                if (expression.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Expression, expression)) continue;
                if (identifier.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is IdentifierNameSyntax n && n.Identifier.ValueText == "nameof")) continue;
                var symbol = model.GetSymbolInfo(expression, cancellationToken).Symbol as IMethodSymbol;
                if (symbol != null && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, loader))
                {
                    if (model.GetTypeInfo(expression, cancellationToken).ConvertedType is INamedTypeSymbol { DelegateInvokeMethod: { } invoke } &&
                        IsSkipped(invoke.Parameters.LastOrDefault()?.Type)) continue;
                    diagnostics.Add(LoaderDiagnostics.Error(expression.GetLocation(), "Loader method groups/function pointers cannot be intercepted. Use a lambda containing a loader invocation or an explicit compiled factory."));
                }
            }
        }
        var source = calls.Count == 0 ? string.Empty : new LoaderSourceEmitter(compilation, project, configuration).Emit(calls, cancellationToken);
        return new(source, diagnostics.ToImmutable());
    }

    internal static bool CanReference(CSharpCompilation compilation, INamedTypeSymbol type)
    {
        if (!compilation.IsSymbolAccessibleWithin(type, compilation.Assembly)) return false;
        for (var current = type; current != null; current = current.ContainingType) if (current.Arity != 0) return false;
        return true;
    }
    private static bool TryKind(IMethodSymbol method, out LoaderCallKind kind, out bool hasServices)
    {
        kind = LoaderCallKind.Object; hasServices = false;
        if (!method.IsStatic || method.IsGenericMethod || method.Parameters.Any(p => p.RefKind != RefKind.None)) return false;
        var parameters = method.Parameters;
        if (parameters.Length > 0 && parameters[0].Type.HasMetadataName("System.IServiceProvider")) hasServices = true;
        var offset = hasServices ? 1 : 0;
        if (method.ReturnsVoid && parameters.Length == offset + 1 && parameters[offset].Type.SpecialType == SpecialType.System_Object) return true;
        if (method.ReturnType.SpecialType == SpecialType.System_Object && parameters.Length == offset + 2 &&
            parameters[offset].Type.HasMetadataName("System.Uri") && parameters[offset + 1].Type.HasMetadataName("System.Uri"))
        { kind = LoaderCallKind.Uri; return true; }
        return false;
    }
    private static bool IsExpressionTree(SyntaxNode syntax, SemanticModel model, CancellationToken cancellationToken) =>
        syntax.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any(lambda =>
            model.GetTypeInfo(lambda, cancellationToken).ConvertedType is INamedTypeSymbol type &&
            type.OriginalDefinition.HasMetadataName("System.Linq.Expressions.Expression`1"));
}
