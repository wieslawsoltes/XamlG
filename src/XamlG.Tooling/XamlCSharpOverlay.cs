using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace XamlG.Tooling;

/// <summary>Creates a semantic snapshot from unsaved C# text without modifying loaded documents,
/// executing source generators, writing files, or changing evaluated parse options.</summary>
public static class XamlCSharpOverlay
{
    public static XamlCompilationSession Apply(XamlCompilationSession compiler,
        IEnumerable<KeyValuePair<string, string>> buffers, CancellationToken cancellationToken = default)
    {
        if (compiler == null) throw new ArgumentNullException(nameof(compiler));
        if (buffers == null) throw new ArgumentNullException(nameof(buffers));
        var compilation = compiler.Types.Compilation;
        var comparer = System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var originals = compilation.SyntaxTrees.Where(t => !string.IsNullOrEmpty(t.FilePath))
            .ToDictionary(t => t.FilePath.Replace('\\', '/'), comparer);
        var seen = new HashSet<string>(comparer);
        var fallback = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? CSharpParseOptions.Default;
        foreach (var buffer in buffers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(buffer.Key) || buffer.Value == null || !seen.Add(buffer.Key.Replace('\\', '/')))
                throw new ArgumentException("C# overlays require unique file paths and non-null text.", nameof(buffers));
            if (originals.TryGetValue(buffer.Key.Replace('\\', '/'), out var previous))
            {
                if (previous.GetText(cancellationToken).ToString() == buffer.Value) continue;
                var changed = CSharpSyntaxTree.ParseText(buffer.Value, (CSharpParseOptions)previous.Options,
                    previous.FilePath, previous.Encoding, cancellationToken);
                compilation = compilation.ReplaceSyntaxTree(previous, changed);
            }
            else compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(buffer.Value, fallback,
                buffer.Key, cancellationToken: cancellationToken));
        }
        return ReferenceEquals(compilation, compiler.Types.Compilation) ? compiler :
            new(compilation, compiler.Profile, compiler.Options, projectDocuments: compiler.ProjectDocuments);
    }
}
