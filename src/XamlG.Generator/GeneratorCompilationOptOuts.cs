using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using XamlG.Compiler;
using XamlG.Frameworks;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Generator;

internal sealed record GeneratorCompilationOptOuts(bool AllDocuments, ImmutableHashSet<ISymbol> Classes)
{
    private static readonly GeneratorCompilationOptOuts Empty = new(false, ImmutableHashSet.Create<ISymbol>(SymbolEqualityComparer.Default));
    public static GeneratorCompilationOptOuts Read(CSharpCompilation compilation, AnalyzerOptions analyzerOptions, GeneratorOptions options, CancellationToken cancellationToken)
    {
        XamlFrameworkProfile profile;
        try { profile = KnownFrameworkProfiles.Select(compilation, options.Framework, options.CompileBindingsByDefault, options.CreateSourceInfo); }
        catch (ArgumentException) { return Empty; }
        var types = new RoslynTypeSystem(compilation, profile.TypeSystem);
        var skipped = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
        var compiled = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
        var count = 0; var skippedCount = 0;
        foreach (var text in analyzerOptions.AdditionalFiles.Where(text => XamlSourceFile.IsSupported(text.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = GeneratorInput.Read(text, analyzerOptions.AnalyzerConfigOptionsProvider, cancellationToken);
            if (!input.Compile) continue;
            count++;
            var syntax = XamlSyntaxTree.Parse(input.Text, input.LogicalPath, cancellationToken);
            var isSkipped = !profile.Directives.ShouldCompile(syntax, new XamlCompilerOptions { IsPrecompilation = true });
            if (isSkipped) skippedCount++;
            if (syntax.Root is { } root && NamespaceScope.Empty.Push(root).Directive(root, "Class") is { } directive && types.Find(directive.Value) is { } type)
                (isSkipped ? skipped : compiled).Add(type);
        }
        skipped.ExceptWith(compiled);
        return new(count > 0 && count == skippedCount, skipped.ToImmutable());
    }
}
