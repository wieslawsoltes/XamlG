using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp.Resources;

/// <summary>Project-wide resource linking independent of MSBuild, source generators, workspaces or a runtime host.</summary>
public sealed class XamlProjectCompiler
{
    public XamlProjectCompilation Compile(IEnumerable<XamlProjectDocument> documents, CSharpCompilation compilation,
        XamlFrameworkProfile? profile = null, XamlCompilerOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (documents == null) throw new ArgumentNullException(nameof(documents));
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        profile ??= XamlFrameworkProfile.Portable; options ??= new();
        // Referenced projects may contain the same logical path. Their generated factory types
        // must not collide with local types during ordinary C# symbol resolution.
        options = options with { GeneratedNamespace = options.GeneratedNamespace + ".Assembly_" + CSharpNames.StableId(compilation.Assembly.Identity.Name) };
        var inputs = documents.OrderBy(d => d.LogicalPath, StringComparer.Ordinal).ToArray();
        if (inputs.Length > 16384) throw new ArgumentException("A project cannot exceed 16384 XAML documents.", nameof(documents));
        var types = new RoslynTypeSystem(compilation, profile.TypeSystem);
        var catalog = XamlResourceCatalogBuilder.Create(inputs, types, profile, options, cancellationToken);
        var bound = new BoundDocument[inputs.Length];
        var addresses = new string?[inputs.Length];
        var identities = inputs.GroupBy(d => d.LogicalPath, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < inputs.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? addressError = null;
            try { addresses[i] = XamlResourceCatalogBuilder.Address(inputs[i], types, profile); }
            catch (ArgumentException error) { addressError = error.Message; }
            var itemOptions = options with
            {
                DocumentId = inputs[i].LogicalPath, ResourceUri = addresses[i],
                BaseUri = addresses[i] ?? options.BaseUri, Resources = catalog
            };
            bound[i] = new XamlCompiler().Bind(inputs[i].Syntax, types, profile, itemOptions, cancellationToken);
            if (addressError != null) bound[i] = AddError(bound[i], "XG3300", addressError);
            if (identities.Contains(inputs[i].LogicalPath)) bound[i] = AddError(bound[i], "XG3300", "Duplicate logical XAML path: " + inputs[i].LogicalPath);
        }
        foreach (var group in addresses.Select((uri, index) => (Uri: uri, Index: index)).Where(p => p.Uri != null).GroupBy(p => p.Uri, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var item in group) bound[item.Index] = AddError(bound[item.Index], "XG3300", "Duplicate resource URI: " + item.Uri);
        foreach (var group in bound.Select((d, i) => (Document: d, Index: i)).Where(p => p.Document.ClassName != null).GroupBy(p => p.Document.ClassName, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var item in group) bound[item.Index] = AddError(item.Document, "XG2002", "More than one XAML document declares x:Class '" + group.Key + "'.");
        XamlResourceGraph.Validate(bound, cancellationToken);
        var output = ImmutableArray.CreateBuilder<XamlProjectDocumentResult>(inputs.Length);
        for (var i = 0; i < inputs.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var emission = new CSharpEmitter().Emit(bound[i], cancellationToken);
            output.Add(new(inputs[i], addresses[i], bound[i], XamlResourceExports.Add(bound[i], emission)));
        }
        return new(output.ToImmutable(), catalog);
    }
    private static BoundDocument AddError(BoundDocument document, string code, string message) => document with
    { Diagnostics = document.Diagnostics.Add(new XamlDiagnostic(code, message, document.Syntax.Root?.NameSpan ?? new TextSpan(0, 0))) };
}
