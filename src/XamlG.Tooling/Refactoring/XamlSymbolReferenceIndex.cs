using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

internal sealed record XamlSymbolReference(ISymbol Symbol, TextSpan Span, string Spelling, string Role)
{
    internal string RenameName => Role is "attached-property" or "attached-event" or "registered-property" ? Spelling : CSharpRenameSymbols.Name(Symbol);
    internal string SymbolName(string name)
    {
        if (name.StartsWith("@", StringComparison.Ordinal)) name = name.Substring(1);
        if (Role == "attached-property" && Symbol is IMethodSymbol method && method.Name.Length == Spelling.Length + 3)
            return method.Name.Substring(0, 3) + name;
        if (Role == "attached-event" && Symbol is IMethodSymbol handler)
            return (handler.Name.StartsWith("Add", StringComparison.Ordinal) ? "Add" : "Remove") + name + "Handler";
        if (Role == "registered-property" && Symbol.Name.StartsWith(Spelling, StringComparison.Ordinal))
            return name + Symbol.Name.Substring(Spelling.Length);
        return name;
    }
    internal string Replacement(string name)
    {
        if (Role == "attached-event" && Symbol is IMethodSymbol handler)
        {
            var prefix = handler.Name.StartsWith("Add", StringComparison.Ordinal) ? "Add" : "Remove";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith("Handler", StringComparison.Ordinal) || name.Length <= prefix.Length + 7)
                throw new InvalidOperationException("An attached event accessor must preserve its Add/Remove...Handler shape.");
            return name.Substring(prefix.Length, name.Length - prefix.Length - 7);
        }
        if (Role == "attached-property" && Symbol is IMethodSymbol method)
        {
            var prefix = method.Name.StartsWith("Set", StringComparison.Ordinal) ? "Set" : method.Name.StartsWith("Get", StringComparison.Ordinal) ? "Get" : "";
            if (prefix.Length != 0)
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length)
                    throw new InvalidOperationException("An attached accessor rename must preserve its Get/Set prefix.");
                return name.Substring(prefix.Length);
            }
        }
        if (Role == "registered-property" && Symbol is IFieldSymbol field)
        {
            var suffix = field.Name.EndsWith("Property", StringComparison.Ordinal) ? "Property" : field.Name.EndsWith("Event", StringComparison.Ordinal) ? "Event" : "";
            if (suffix.Length != 0 && Spelling != field.Name)
            {
                if (!name.EndsWith(suffix, StringComparison.Ordinal) || name.Length == suffix.Length)
                    throw new InvalidOperationException("A registered property/event field must retain its Property/Event suffix for XAML references.");
                return name.Substring(0, name.Length - suffix.Length);
            }
        }
        if (Role == "type" && Symbol.Name.EndsWith("Extension", StringComparison.Ordinal) && Spelling == Symbol.Name.Substring(0, Symbol.Name.Length - 9) && name.Length > 9 && name.EndsWith("Extension", StringComparison.Ordinal))
            return name.Substring(0, name.Length - 9);
        return Role is "event-handler" or "method" ? CSharpLanguageService.Escape(name) : name;
    }
}

// Only compiler-resolved references participate. The source map retains raw entity
// boundaries; names in ordinary strings and reflection bindings are not guessed.
internal static class XamlSymbolReferenceIndex
{
    internal static ImmutableArray<XamlSymbolReference> Create(XamlAnalysis analysis, XamlCompilationSession compiler, CancellationToken token)
    {
        var result = new List<XamlSymbolReference>(); var syntax = analysis.Syntax;
        if (syntax.Root == null) return ImmutableArray<XamlSymbolReference>.Empty;
        var elements = syntax.Root.DescendantsAndSelf().ToArray();
        var scopes = new Dictionary<XamlElementSyntax, NamespaceScope>();
        var parents = new Dictionary<XamlElementSyntax, XamlElementSyntax?>();
        var typeIds = analysis.Document.Symbols.Where(symbol => symbol.Role == "type").Select(symbol => CSharpRenameSymbols.DeclarationId(symbol.Symbol)).ToImmutableHashSet(StringComparer.Ordinal);
        var implicitMembers = BoundDocumentTraversal.Objects(analysis.Document).SelectMany(obj => obj.Assignments).Select(assignment => assignment switch
        { BoundSetAssignment set => set.Member, BoundAddAssignment add => add.Collection, BoundEventAssignment ev => ev.Event, BoundAdaptedSetAssignment adapted => adapted.Member, _ => null })
            .Where(member => member?.IsImplicitContent == true).Select(member => (member!.Span, CSharpRenameSymbols.DeclarationId(member.Symbol))).ToImmutableHashSet();
        var pending = new Stack<(XamlElementSyntax Element, NamespaceScope Scope, XamlElementSyntax? Parent)>(); pending.Push((syntax.Root, NamespaceScope.Empty, null));
        while (pending.Count != 0)
        {
            token.ThrowIfCancellationRequested(); var (element, parentScope, parent) = pending.Pop(); var scope = parentScope.Push(element);
            scopes.Add(element, scope); parents.Add(element, parent);
            foreach (var child in element.Children.OfType<XamlElementSyntax>()) pending.Push((child, scope, element));
            foreach (var attribute in element.Attributes.Where(attribute => attribute.IsNamespace)) Namespace(attribute);
            foreach (var attribute in element.Attributes.Where(attribute => !attribute.IsNamespace && attribute.Name.Contains('.'))) Owner(attribute.NameSpan, scope);
            if (element.LocalName.Contains('.')) Owner(element.NameSpan, scope);
            if (scope.Directive(element, "TypeArguments") is { } typeArguments) TypeArguments(typeArguments.ValueSpan, scope);
            foreach (var attribute in element.Attributes.Where(attribute => attribute.Value.StartsWith("{", StringComparison.Ordinal)))
            {
                var map = XamlDecodedTextMap.Create(syntax.Text, attribute.ValueSpan);
                var markup = MarkupExtensionParser.Parse(map.Text, new(0, map.Text.Length), _ => { });
                if (markup == null) continue;
                foreach (var argument in markup.Arguments.Where(argument => argument.Name != null))
                {
                    var name = scope.Expand(argument.Name!, true);
                    if (name.Namespace != null && XamlNames.IsLanguage(name.Namespace) && name.LocalName == "TypeArguments")
                        TypeArguments(map.ToSource(argument.ValueSpan ?? argument.Span), scope);
                }
            }
        }
        if (analysis.Document.ClassSymbol is { } codeBehind && scopes[syntax.Root].Directive(syntax.Root, "Class") is { } directive)
            Qualified(directive.ValueSpan, QualifiedSymbols(codeBehind), "class");
        var enumReferences = BoundDocumentTraversal.Expressions(analysis.Document).OfType<BoundEnumExpression>()
            .SelectMany(expression => expression.Fields.Select(field => new BoundSymbolInfo(expression.Span, field, "enum")));
        foreach (var bound in analysis.Document.Symbols.Concat(enumReferences))
        {
            token.ThrowIfCancellationRequested();
            if (bound.Role == "constructor") continue;
            var ownerElement = OwnerElement(bound.Span);
            if (ownerElement == null) continue;
            var span = bound.Span;
            if (bound.Role == "factory")
            {
                if (scopes[ownerElement].Directive(ownerElement, "FactoryMethod") is not { } factory) continue;
                span = factory.ValueSpan;
            }
            // A property's implicit content slot shares its child's type span; that is
            // a binding observation, not a written property name.
            if (bound.Role == "property" && (implicitMembers.Contains((span, CSharpRenameSymbols.DeclarationId(bound.Symbol))) ||
                ownerElement.NameSpan == span && !ownerElement.LocalName.Contains('.'))) continue;
            var map = XamlDecodedTextMap.Create(syntax.Text, span); var text = map.Text;
            var sourceSymbol = bound.Role == "type" && bound.Symbol is INamedTypeSymbol nullable &&
                nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && text.TrimEnd().EndsWith("?", StringComparison.Ordinal)
                ? nullable.TypeArguments[0] : bound.Symbol;
            var search = new TextSpan(0, text.Length);
            var markup = text.StartsWith("{", StringComparison.Ordinal) ? MarkupExtensionParser.Parse(text, search, _ => { }) : null;
            if (markup != null)
            {
                var expanded = scopes[ownerElement].Expand(markup.Name);
                if (expanded.Namespace != null && XamlNames.IsLanguage(expanded.Namespace) && expanded.LocalName is "Type" or "Static")
                {
                    var argument = markup.Arguments.FirstOrDefault(item => item.Name == null || item.Name is "Member" or "TypeName");
                    if (argument != null) search = argument.ValueSpan ?? argument.Span;
                }
                else if (bound.Role == "type" && compiler.Types.Resolve(expanded.Namespace ?? "", expanded.LocalName).Type is { } extension &&
                    CSharpRenameSymbols.SameDeclaration(extension, bound.Symbol))
                    search = new(text.IndexOf(markup.Name, StringComparison.Ordinal), markup.Name.Length);
            }
            if (bound.Role is "property" or "event" or "attached-property" or "attached-event")
            {
                var equals = text.IndexOf('=', search.Start, search.Length);
                if (equals >= 0) search = TextSpan.FromBounds(search.Start, equals);
            }
            if (bound.Role == "enum")
            {
                var equals = text.IndexOf('=', search.Start, search.Length);
                if (equals >= 0) search = TextSpan.FromBounds(equals + 1, search.End);
                if (text.StartsWith("<", StringComparison.Ordinal) && text.IndexOf('>') is var close && close >= 0 && text.LastIndexOf('<') > close)
                    search = TextSpan.FromBounds(close + 1, text.LastIndexOf('<'));
            }
            var name = CSharpRenameSymbols.Name(sourceSymbol);
            var spellings = new List<string> { name };
            if (bound.Role == "type" && name.EndsWith("Extension", StringComparison.Ordinal)) spellings.Add(name.Substring(0, name.Length - 9));
            if (bound.Role == "attached-property" && (name.StartsWith("Set", StringComparison.Ordinal) || name.StartsWith("Get", StringComparison.Ordinal))) spellings.Add(name.Substring(3));
            if (bound.Role == "attached-event" && name.StartsWith("Add", StringComparison.Ordinal) && name.EndsWith("Handler", StringComparison.Ordinal)) spellings.Add(name.Substring(3, name.Length - 10));
            if (bound.Role == "registered-property")
            {
                if (name.EndsWith("Property", StringComparison.Ordinal)) spellings.Add(name.Substring(0, name.Length - 8));
                if (name.EndsWith("Event", StringComparison.Ordinal)) spellings.Add(name.Substring(0, name.Length - 5));
            }
            var words = Words(text, search).Where(word => spellings.Contains(word.Name, StringComparer.Ordinal)).ToArray();
            if (words.Length == 0) continue;
            if (bound.Role == "enum")
            {
                foreach (var field in words) Add(new(sourceSymbol, map.ToSource(field.Span), field.Name, bound.Role));
                continue;
            }
            var word = bound.Role is "type" or "property" ? words[0] : words[words.Length - 1];
            Add(new(sourceSymbol, map.ToSource(word.Span), word.Name, bound.Role));
        }
        return result.Distinct().OrderBy(item => item.Span.Start).ThenBy(item => item.Span.Length).ToImmutableArray();

        void Add(XamlSymbolReference reference)
        {
            result.Add(reference);
            if (OwnerElement(reference.Span) is { } element && element.NameSpan.Contains(reference.Span) && element.EndNameSpan.Length != 0)
                result.Add(reference with { Span = new(element.EndNameSpan.Start + reference.Span.Start - element.NameSpan.Start, reference.Span.Length) });
        }
        XamlElementSyntax? OwnerElement(TextSpan span)
        {
            var low = 0; var high = elements.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (elements[middle].Span.Start <= span.Start) low = middle + 1; else high = middle;
            }
            var owner = low == 0 ? null : elements[low - 1];
            while (owner != null && !owner.Span.Contains(span)) owner = parents.TryGetValue(owner, out var parent) ? parent : null;
            return owner;
        }
        void Qualified(TextSpan span, IReadOnlyList<ISymbol> symbols, string role)
        {
            var map = XamlDecodedTextMap.Create(syntax.Text, span); var words = Words(map.Text, new(0, map.Text.Length)).ToArray();
            if (words.Length != symbols.Count || words.Where((word, index) => word.Name != symbols[index].Name).Any()) return;
            for (var index = 0; index < words.Length; index++) Add(new(symbols[index], map.ToSource(words[index].Span), words[index].Name, role));
        }
        void Namespace(XamlAttributeSyntax attribute)
        {
            var map = XamlDecodedTextMap.Create(syntax.Text, attribute.ValueSpan); var value = map.Text;
            var start = value.StartsWith("using:", StringComparison.Ordinal) ? 6 : value.StartsWith("clr-namespace:", StringComparison.Ordinal) ? 14 : -1;
            if (start < 0) return;
            var end = value.IndexOf(';', start); if (end < 0) end = value.Length;
            var words = Words(value, TextSpan.FromBounds(start, end)).ToArray(); var current = compiler.Types.Compilation.GlobalNamespace;
            foreach (var word in words)
            {
                current = current.GetNamespaceMembers().SingleOrDefault(item => item.Name == word.Name);
                if (current == null) break;
                result.Add(new(current, map.ToSource(word.Span), word.Name, "namespace"));
            }
        }
        void Owner(TextSpan span, NamespaceScope scope)
        {
            var value = syntax.Text.Substring(span.Start, span.Length); var dot = value.LastIndexOf('.'); if (dot < 0) return;
            var owner = value.Substring(0, dot); var expanded = scope.Expand(owner);
            if (expanded.Namespace == null || compiler.Types.Resolve(expanded.Namespace, expanded.LocalName).Type is not { } type) return;
            var words = Words(owner, new(0, owner.Length)).ToArray();
            if (words.Length != 0 && words[words.Length - 1].Name == type.Name)
                Add(new(type, new(span.Start + words[words.Length - 1].Span.Start, words[words.Length - 1].Span.Length), type.Name, "type"));
        }
        void TypeArguments(TextSpan span, NamespaceScope scope)
        {
            var map = XamlDecodedTextMap.Create(syntax.Text, span);
            var parsed = XamlTypeNameParser.ParseList(map.Text, new(0, map.Text.Length), _ => { });
            var types = new Stack<XamlTypeNameSyntax>(parsed.Reverse());
            while (types.Count != 0)
            {
                token.ThrowIfCancellationRequested(); var typeName = types.Pop(); var expanded = scope.Expand(typeName.Name);
                if (expanded.Namespace != null && compiler.Types.Resolve(expanded.Namespace, expanded.LocalName, typeName.Arguments.Length).Type is { } type &&
                    typeIds.Contains(CSharpRenameSymbols.DeclarationId(type)))
                {
                    var word = Words(map.Text, new(typeName.Span.Start, typeName.Name.Length)).LastOrDefault();
                    if (word.Name == type.Name) Add(new(type, map.ToSource(word.Span), type.Name, "type"));
                }
                foreach (var argument in typeName.Arguments.Reverse()) types.Push(argument);
            }
        }
    }
    private static IReadOnlyList<ISymbol> QualifiedSymbols(INamedTypeSymbol type)
    {
        var result = new List<ISymbol>();
        for (ISymbol? current = type; current is INamedTypeSymbol or INamespaceSymbol { IsGlobalNamespace: false }; current = current.ContainingSymbol) result.Add(current);
        result.Reverse(); return result;
    }
    private static IEnumerable<(string Name, TextSpan Span)> Words(string text, TextSpan span)
    {
        for (var index = span.Start; index < span.End;)
        {
            var start = index;
            if (text[index] == '@' && index + 1 < span.End && SyntaxFacts.IsIdentifierStartCharacter(text[index + 1])) index++;
            if (!SyntaxFacts.IsIdentifierStartCharacter(text[index])) { index++; continue; }
            var name = index++;
            while (index < span.End && SyntaxFacts.IsIdentifierPartCharacter(text[index])) index++;
            if (index < span.End && text[index] is ':' or '|') continue; // XML prefix, not a CLR name.
            yield return (text.Substring(name, index - name), new(start, index - start));
        }
    }
}
