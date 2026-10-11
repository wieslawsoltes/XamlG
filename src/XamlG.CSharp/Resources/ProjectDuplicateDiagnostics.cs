using System.Threading;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.CSharp.Resources;

/// <summary>Project-local duplicate bookkeeping without per-key grouping objects.
/// Input ordering and the URI-before-class diagnostic order remain unchanged.</summary>
internal static class ProjectDuplicateDiagnostics
{
    /// <remarks>Compile sorts inputs by LogicalPath before calling this method. The
    /// directive policy is still invoked once for every input, including duplicates.</remarks>
    public static HashSet<string>? CollectSortedPaths(XamlProjectDocument[] inputs,
        XamlFrameworkProfile profile, XamlCompilerOptions options)
    {
        HashSet<string>? duplicates = null;
        string? previous = null;
        var hasPrevious = false;
        foreach (var input in inputs)
        {
            if (!profile.Directives.ShouldCompile(input.Syntax, options)) continue;
            if (hasPrevious && StringComparer.Ordinal.Equals(previous, input.LogicalPath))
                (duplicates ??= new(StringComparer.Ordinal)).Add(input.LogicalPath);
            previous = input.LogicalPath;
            hasPrevious = true;
        }
        return duplicates;
    }

    public static void Apply(BoundDocument[] documents, string?[] addresses, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (documents.Length < 2) return;
        var uris = new DuplicateIndex(documents.Length);
        var classes = new DuplicateIndex(documents.Length);
        for (var i = 0; i < documents.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = documents[i];
            if (document.IsSkipped) continue;
            if (addresses[i] is { } uri) uris.Add(uri, i);
            if (document.ClassName is { } name) classes.Add(name, i);
        }
        if (!uris.HasDuplicates && !classes.HasDuplicates) return;
        // Records are immutable and each position belongs to only one group of
        // each kind. Group-order interleaving is unobservable: within each document
        // append the URI diagnostic, then the class diagnostic, as the old passes did.
        for (var i = 0; i < documents.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = documents[i];
            if (uris.Contains(i)) document = Error(document, "XG3300", "Duplicate resource URI: " + addresses[i]);
            if (classes.Contains(i)) document = Error(document, "XG2002", "More than one XAML document declares x:Class '" + document.ClassName + "'.");
            documents[i] = document;
        }
    }

    private static BoundDocument Error(BoundDocument document, string code, string message) => document with
    { Diagnostics = document.Diagnostics.Add(new XamlDiagnostic(code, message, document.Syntax.Root?.NameSpan ?? new TextSpan(0, 0))) };

    private struct DuplicateIndex(int length)
    {
        private Dictionary<string, int>? _first;
        private bool[]? _duplicates;
        public readonly bool HasDuplicates => _duplicates != null;
        public readonly bool Contains(int index) => _duplicates != null && _duplicates[index];
        public void Add(string key, int index)
        {
            var first = _first ??= new(StringComparer.Ordinal);
            if (!first.TryGetValue(key, out var previous)) { first.Add(key, index); return; }
            var duplicates = _duplicates ??= new bool[length];
            duplicates[previous] = true;
            duplicates[index] = true;
        }
    }
}
