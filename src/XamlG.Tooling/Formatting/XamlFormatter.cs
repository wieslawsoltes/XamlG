using System.Collections.Immutable;
using System.Threading;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Tooling.Formatting;

/// <summary>Plans non-overlapping whitespace edits. Literal values, comments, CDATA,
/// mixed content and xml:space scopes retain their exact source spelling.</summary>
public static class XamlFormatter
{
    public static ImmutableArray<XamlTextChange> Format(XamlSyntaxTree syntax,
        XamlFormattingOptions? options = null, TextSpan? range = null,
        XamlAnalysis? analysis = null, CancellationToken cancellationToken = default)
    {
        if (syntax == null) throw new ArgumentNullException(nameof(syntax));
        options ??= new();
        if (options.TabSize is < 1 or > 16 || options.MaximumLineLength is < 20 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (range is { } requested && requested.End > syntax.Text.Length) throw new ArgumentOutOfRangeException(nameof(range));
        if (analysis != null && !ReferenceEquals(analysis.Syntax, syntax)) throw new InvalidOperationException("Formatting analysis belongs to another syntax revision.");
        cancellationToken.ThrowIfCancellationRequested();
        if (syntax.HasErrors || syntax.Root == null) return ImmutableArray<XamlTextChange>.Empty;
        var newline = syntax.Text.Contains("\r\n") ? "\r\n" : "\n";
        var indentation = options.InsertSpaces ? new string(' ', options.TabSize) : "\t";
        var protectedContent = new HashSet<int>();
        if (analysis != null)
        {
            var configuration = analysis.Document.Profile.TypeSystem;
            foreach (var obj in BoundDocumentTraversal.Objects(analysis.Document))
            {
                bool Significant(Microsoft.CodeAnalysis.ITypeSymbol type)
                {
                    for (var current = type as Microsoft.CodeAnalysis.INamedTypeSymbol; current != null; current = current.BaseType)
                        if (current.HasAttribute(configuration.WhitespaceSignificantCollectionAttributes)) return true;
                    return false;
                }
                if (Significant(obj.Type)) protectedContent.Add(obj.Syntax.Span.Start);
                foreach (var collection in obj.Assignments.OfType<XamlG.Compiler.BoundAddAssignment>()
                    .Select(a => a.Collection).Where(c => c != null && Significant(c.ValueType)))
                {
                    protectedContent.Add(obj.Syntax.Span.Start);
                    // A property element has no BoundObject of its own. Protect its literal
                    // inter-item whitespace as well as implicit content on the owner.
                    foreach (var property in obj.Syntax.Children.OfType<XamlElementSyntax>()
                        .Where(e => e.LocalName.EndsWith("." + collection!.Name, StringComparison.Ordinal)))
                        protectedContent.Add(property.Span.Start);
                }
            }
        }
        var edits = new List<XamlTextChange>();
        void Change(int start, int length, string text)
        {
            var span = new TextSpan(start, length);
            if (range is { } selected && !selected.Contains(span)) return;
            if (syntax.Text.Substring(start, length) != text) edits.Add(new(span, text));
        }
        string Indent(int level) => string.Concat(Enumerable.Repeat(indentation, level));
        var pending = new Stack<(XamlElementSyntax Element, NamespaceScope Parent, int Level)>();
        pending.Push((syntax.Root, NamespaceScope.Empty, 0));
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, parent, level) = pending.Pop();
            var scope = parent.Push(element);
            // Attribute values are always copied verbatim, including entities and multiline strings.
            var lineBreak = options.SplitAttributes || element.OpenTagSpan.Length + level * options.TabSize > options.MaximumLineLength ||
                element.Attributes.Any(a => syntax.Lines.GetPosition(a.NameSpan.Start).Line != syntax.Lines.GetPosition(element.NameSpan.Start).Line);
            var offset = element.NameSpan.End;
            foreach (var attribute in element.Attributes)
            {
                Change(offset, attribute.NameSpan.Start - offset, lineBreak ? newline + Indent(level + 1) : " ");
                Change(attribute.NameSpan.End, attribute.ValueSpan.Start - 1 - attribute.NameSpan.End, "=");
                offset = attribute.ValueSpan.End + 1;
            }
            var terminator = element.OpenTagSpan.End - (element.IsSelfClosing ? 2 : 1);
            if (terminator >= offset)
                Change(offset, terminator - offset, element.IsSelfClosing && options.SpaceBeforeSelfClosingSlash ? " " : string.Empty);

            var mixed = scope.PreserveSpace || protectedContent.Contains(element.Span.Start) ||
                element.Children.OfType<XamlTextSyntax>().Any(t => t.IsCData || !string.IsNullOrWhiteSpace(t.Value));
            var children = element.Children.Where(n => n is not XamlTextSyntax).ToArray();
            if (!element.IsSelfClosing && !mixed && children.Length > 0)
            {
                var cursor = element.OpenTagSpan.End;
                foreach (var child in children)
                {
                    var gap = syntax.Text.Substring(cursor, child.Span.Start - cursor);
                    if (gap.All(XmlWhitespace.IsWhitespace)) Change(cursor, gap.Length, newline + Indent(level + 1));
                    cursor = child.Span.End;
                }
                var tail = syntax.Text.Substring(cursor, element.CloseTagSpan.Start - cursor);
                if (tail.All(XmlWhitespace.IsWhitespace)) Change(cursor, tail.Length, newline + Indent(level));
            }
            foreach (var child in element.Children.OfType<XamlElementSyntax>().Reverse()) pending.Push((child, scope, level + 1));
        }
        if (options.InsertFinalNewline && syntax.Text.Length != 0 && syntax.Text[syntax.Text.Length - 1] is not ('\r' or '\n'))
            Change(syntax.Text.Length, 0, newline);
        return edits.OrderBy(e => e.Span.Start).ToImmutableArray();
    }
}
