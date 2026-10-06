using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Resources;

namespace XamlG.CSharp.Integration;

/// <summary>Composes all compiler-owned sources without mixing compilation-wide parse features.
/// Application trees keep their paths, roots, annotations, encoding and individual preprocessor options.</summary>
public static class XamlCSharpCompilation
{
    public const string InterceptorNamespace = "XamlG.Generated.Loaders";
    private const string InterceptorFeature = "InterceptorsNamespaces";

    public static CSharpParseOptions GeneratedParseOptions(CSharpParseOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        options.Features.TryGetValue(InterceptorFeature, out var existing);
        var configured = string.Join(";", (existing ?? string.Empty).Split(';').Where(n => n.Length != 0)
            .Concat(new[] { InterceptorNamespace }).Distinct(StringComparer.Ordinal));
        if (StringComparer.Ordinal.Equals(existing, configured)) return options;
        var features = options.Features.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        features[InterceptorFeature] = configured;
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
        if (project == null) throw new ArgumentNullException(nameof(project));
        cancellationToken.ThrowIfCancellationRequested();
        var sources = Sources(project).ToImmutableArray();
        if (sources.IsEmpty) return application;
        var originals = application.SyntaxTrees.ToImmutableArray();
        parseOptions ??= originals.FirstOrDefault()?.Options as CSharpParseOptions ?? new CSharpParseOptions(LanguageVersion.Preview);
        var generatedOptions = GeneratedParseOptions(parseOptions);
        var trees = ImmutableArray.CreateBuilder<SyntaxTree>(originals.Length + sources.Length);
        var remapping = ImmutableDictionary.CreateBuilder<SyntaxTree, SyntaxTree>();
        foreach (var original in originals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = GeneratedParseOptions((CSharpParseOptions)original.Options);
            if (options.LanguageVersion != generatedOptions.LanguageVersion || !SameFeatures(options, generatedOptions))
                throw new ArgumentException("Generated parse options must match the application's language version and feature settings. Supply the evaluated project parse options.", nameof(parseOptions));
            if (options.Equals(original.Options)) trees.Add(original);
            else
            {
                var replacement = original.WithRootAndOptions(original.GetRoot(cancellationToken), options);
                trees.Add(replacement);
                remapping.Add(replacement, original);
            }
        }
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trees.Add(CSharpSyntaxTree.ParseText(source.Source, generatedOptions, source.HintName, cancellationToken: cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Replacing trees one by one also fails Roslyn's invariant in the intermediate state.
        var result = remapping.Count == 0
            ? application.AddSyntaxTrees(trees.Skip(originals.Length))
            : application.RemoveAllSyntaxTrees().AddSyntaxTrees(trees.ToImmutable());
        if (remapping.Count != 0 && application.Options.SyntaxTreeOptionsProvider is { } provider)
            result = result.WithOptions(result.Options.WithSyntaxTreeOptionsProvider(
                new RemappedSyntaxTreeOptionsProvider(provider, remapping.ToImmutable())));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static bool SameFeatures(CSharpParseOptions left, CSharpParseOptions right) =>
        left.Features.Count == right.Features.Count && left.Features.All(p =>
            right.Features.TryGetValue(p.Key, out var value) && StringComparer.Ordinal.Equals(p.Value, value));
}
