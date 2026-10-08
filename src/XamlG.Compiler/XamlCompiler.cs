using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;

/// <summary>The reusable compilation entry point. It has no dependency on generator, workspace, MSBuild or browser APIs.</summary>
public sealed class XamlCompiler
{
    public BoundDocument Bind(XamlSyntaxTree syntax, CSharpCompilation compilation, XamlFrameworkProfile? profile = null, XamlCompilerOptions? options = null, CancellationToken cancellationToken = default)
    {
        profile ??= XamlFrameworkProfile.Portable;
        return Bind(syntax, new RoslynTypeSystem(compilation, profile.TypeSystem), profile, options, cancellationToken);
    }
    public BoundDocument Bind(XamlSyntaxTree syntax, RoslynTypeSystem types, XamlFrameworkProfile profile, XamlCompilerOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        if (!profile.Directives.ShouldCompile(syntax, options))
        {
            var skippedClass = syntax.Root is { } skippedRoot ? NamespaceScope.Empty.Push(skippedRoot).Directive(skippedRoot, "Class")?.Value : null;
            return new(syntax, null, skippedClass, skippedClass == null ? null : types.Find(skippedClass), "public",
                syntax.Diagnostics.AddRange(profile.Directives.GetSkippedDiagnostics(syntax, cancellationToken)),
                ImmutableArray<BoundSymbolInfo>.Empty, profile, options with { ResourceUri = null })
                { IsSkipped = true, CanAugmentClass = false };
        }
        var context = new BindingContext(syntax, types, profile, options, cancellationToken);
        var root = syntax.Root; BoundObject? bound = null; string? className = null; var modifier = "public";
        var canAugment = false;
        if (root != null)
        {
            var scope = NamespaceScope.Empty.Push(root); var directive = scope.Directive(root, "Class"); className = directive?.Value;
            if (className != null)
            {
                context.RootClass = types.Find(className);
                if (context.RootClass == null) context.Report("XG1030", $"Code-behind class '{className}' was not found in the Roslyn compilation.", directive!.ValueSpan);
                else
                {
                    canAugment = XamlClassAugmentation.IsAvailable(context.RootClass, cancellationToken);
                    if (!canAugment && !types.IsAccessible(context.RootClass))
                        context.Report("XG1030", $"Non-partial code-behind '{className}' must be accessible to its generated factory.", directive!.ValueSpan);
                    if (!canAugment)
                        for (var current = context.RootClass; current != null; current = current.ContainingType)
                            if (current.Arity != 0 || current.TypeKind != TypeKind.Class)
                            { context.Report("XG1030", "An external component factory requires a nongeneric reference-type component and containing types.", directive!.ValueSpan); break; }
                }
            }
            modifier = profile.Directives.BindClassModifier(context, scope.Directive(root, "ClassModifier"));
            var typeArguments = scope.Directive(root, "TypeArguments");
            var declared = context.ResolveTypeAtSource(root.Name, scope, root.NameSpan, typeArguments?.Value, typeArgumentSpan: typeArguments?.ValueSpan);
            if (declared != null)
            {
                if (context.RootClass != null && !types.Compilation.ClassifyCommonConversion(context.RootClass, declared).IsImplicit)
                    context.Report("XG1030", $"Code-behind '{className}' does not derive from '{declared}'.", root.NameSpan);
                else bound = context.Objects.Bind(root, NamespaceScope.Empty, context.RootClass ?? declared, true);
            }
        }
        MetadataDiagnosticCollector.Collect(context);
        var document = new BoundDocument(syntax, bound, className, context.RootClass, modifier, context.Diagnostics.ToImmutableArray(), context.Symbols.ToImmutableArray(), profile, options)
        { Runtime = context.Runtime, CanAugmentClass = canAugment };
        foreach (var pass in profile.Passes) { cancellationToken.ThrowIfCancellationRequested(); document = pass.Run(document, context); }
        return document;
    }
}
