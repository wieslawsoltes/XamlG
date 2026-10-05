using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Resources;

namespace XamlG.CSharp.Integration;

/// <summary>Host-neutral source composition. Preserves application parse settings and adds
/// only the namespace capability needed by XamlG's generated interceptors.</summary>
public static class XamlCSharpCompilation
{
    public const string InterceptorNamespace = "XamlG.Generated.Loaders";
    public static CSharpParseOptions GeneratedParseOptions(CSharpParseOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        var features = options.Features.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        features.TryGetValue("InterceptorsNamespaces", out var existing);
        features["InterceptorsNamespaces"] = string.Join(";", (existing ?? string.Empty).Split(';')
            .Where(n => n.Length != 0).Concat(new[] { InterceptorNamespace }).Distinct(StringComparer.Ordinal));
        return options.WithFeatures(features);
    }
    public static IEnumerable<XamlGeneratedSource> Sources(XamlProjectCompilation project)
    {
        if (project == null) throw new ArgumentNullException(nameof(project));
        foreach (var document in project.Documents)
            if (document.Output.Success) yield return new(document.Output.HintName, document.Output.Source);
        if (project.SourceIntegration.Source.Length != 0)
            yield return new(XamlSourceIntegrationResult.HintName, project.SourceIntegration.Source);
    }
    public static CSharpCompilation AddGeneratedSources(CSharpCompilation application, XamlProjectCompilation project,
        CSharpParseOptions? parseOptions = null, CancellationToken cancellationToken = default)
    {
        if (application == null) throw new ArgumentNullException(nameof(application));
        parseOptions ??= application.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? new CSharpParseOptions(LanguageVersion.Preview);
        var generatedOptions = GeneratedParseOptions(parseOptions);
        var trees = Sources(project).Select(source => CSharpSyntaxTree.ParseText(source.Source, generatedOptions,
            source.HintName, cancellationToken: cancellationToken)).ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();
        return application.AddSyntaxTrees(trees);
    }
}
