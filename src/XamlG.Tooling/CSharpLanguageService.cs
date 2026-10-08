using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XamlG.Syntax;
using XamlG.Tooling.Refactoring;
using Span = Microsoft.CodeAnalysis.Text.TextSpan;

namespace XamlG.Tooling;

/// <summary>In-memory C# authoring over the host's actual Roslyn compilation, including generated
/// sources. Uses compiler APIs only: no MSBuild evaluation, desktop workspace services or code execution.
/// All positions are UTF-16 offsets into the supplied immutable syntax trees.</summary>
public sealed partial class CSharpLanguageService
{
    private readonly CSharpCompilation _compilation;
    private readonly ImmutableHashSet<string> _editable;
    public CSharpLanguageService(CSharpCompilation compilation, IEnumerable<string>? editablePaths = null)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _editable = (editablePaths ?? compilation.SyntaxTrees.Select(t => t.FilePath)).ToImmutableHashSet(StringComparer.Ordinal);
    }

    public SyntaxTree Tree(string path) => _compilation.SyntaxTrees.SingleOrDefault(t => t.FilePath == path)
        ?? throw new KeyNotFoundException("Unknown C# source or generated path: " + path);

    public CSharpSymbolInfo? GetSymbol(string path, int offset, CancellationToken cancellationToken = default)
    {
        var tree = Tree(path); var token = Token(tree, offset, cancellationToken);
        if (token.Parent == null) return null;
        var model = _compilation.GetSemanticModel(tree);
        var symbol = SymbolAt(model, token, cancellationToken);
        var node = token.Parent;
        var type = model.GetTypeInfo(node, cancellationToken).Type ?? SymbolType(symbol);
        var constant = model.GetConstantValue(node, cancellationToken);
        if (!constant.HasValue && symbol is IFieldSymbol { HasConstantValue: true } field) constant = new(field.ConstantValue);
        if (!constant.HasValue && symbol is ILocalSymbol { HasConstantValue: true } local) constant = new(local.ConstantValue);
        var candidates = model.GetSymbolInfo(node, cancellationToken).CandidateSymbols;
        if (symbol == null && type == null && candidates.IsEmpty && !constant.HasValue) return null;
        return new(new(token.SpanStart, token.Span.Length), symbol?.Name, symbol?.Kind.ToString(), symbol?.ToDisplayString(),
            type?.ToDisplayString(), symbol?.DeclaredAccessibility.ToString(), symbol?.IsStatic ?? false,
            symbol?.IsImplicitlyDeclared ?? false, constant.HasValue, constant.HasValue ? ConstantValue(constant.Value) : null,
            Clip(symbol?.GetDocumentationCommentXml(cancellationToken: cancellationToken) ?? "", 16_384),
            symbol == null ? ImmutableArray<CSharpLocation>.Empty : Locations(symbol, cancellationToken),
            candidates.Take(100).Select(s => s.ToDisplayString()).ToImmutableArray());
    }

    public ISymbol? ResolveSymbol(string path, int offset, CancellationToken cancellationToken = default)
    {
        var tree = Tree(path);
        return SymbolAt(_compilation.GetSemanticModel(tree), Token(tree, offset, cancellationToken), cancellationToken);
    }

    public ImmutableArray<CSharpLocation> GetDefinitions(string path, int offset, CancellationToken cancellationToken = default)
    {
        var symbol = ResolveSymbol(path, offset, cancellationToken);
        return symbol == null ? ImmutableArray<CSharpLocation>.Empty : Locations(symbol, cancellationToken);
    }

    public CSharpReferenceResult GetReferences(string path, int offset, bool includeDeclaration = true, int maximumResults = 1000,
        CancellationToken cancellationToken = default)
    {
        Bound(maximumResults); var symbol = ResolveSymbol(path, offset, cancellationToken);
        return symbol == null ? new(ImmutableArray<CSharpLocation>.Empty, false) : FindReferences(symbol, includeDeclaration, maximumResults, cancellationToken);
    }

    public CSharpReferenceResult FindReferences(ISymbol symbol, bool includeDeclaration = true, int maximumResults = 1000,
        CancellationToken cancellationToken = default)
    {
        Bound(maximumResults);
        var result = new HashSet<CSharpLocation>(); var truncated = false;
        if (includeDeclaration)
            foreach (var location in Locations(symbol, cancellationToken)) Add(location);
        foreach (var tree in _compilation.SyntaxTrees.OrderBy(t => t.FilePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = _compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot(cancellationToken).DescendantNodes(descendIntoTrivia: true).OfType<SimpleNameSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (name.Identifier.ValueText == symbol.Name && SameSymbol(symbol, SymbolAt(model, name.Identifier, cancellationToken)))
                    Add(Location(tree, name.Identifier.Span, false, cancellationToken));
                if (truncated) break;
            }
            if (truncated) break;
        }
        return new(result.OrderBy(l => l.Path, StringComparer.Ordinal).ThenBy(l => l.Start).ToImmutableArray(), truncated);
        void Add(CSharpLocation item)
        {
            // A declaration's identifier is also a SimpleNameSyntax for aliases/namespaces.
            if (result.Any(r => r.Path == item.Path && r.Start == item.Start && r.Length == item.Length)) return;
            if (result.Count == maximumResults) { truncated = true; return; }
            result.Add(item);
        }
    }

    public CSharpCompletionResult GetCompletions(string path, int offset, int maximumResults = 200, CancellationToken cancellationToken = default)
    {
        Bound(maximumResults); var tree = Tree(path); var token = Token(tree, offset, cancellationToken);
        var text = tree.GetText(cancellationToken); var model = _compilation.GetSemanticModel(tree);
        var start = offset;
        while (start > 0 && SyntaxFacts.IsIdentifierPartCharacter(text[start - 1])) start--;
        if (start > 0 && text[start - 1] == '@') start--;
        var prefix = text.ToString(Span.FromBounds(start, offset)).TrimStart('@');
        var end = offset;
        while (end < text.Length && SyntaxFacts.IsIdentifierPartCharacter(text[end])) end++;
        var span = new XamlG.Syntax.TextSpan(start, end - start);
        ExpressionSyntax? receiver = null;
        foreach (var ancestor in token.Parent?.AncestorsAndSelf() ?? Enumerable.Empty<SyntaxNode>())
        {
            if (ancestor is MemberAccessExpressionSyntax member && offset >= member.OperatorToken.Span.End && offset <= member.Name.FullSpan.End)
            { receiver = member.Expression; break; }
            if (ancestor is MemberBindingExpressionSyntax binding && offset >= binding.OperatorToken.Span.End)
            { receiver = binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression; break; }
        }
        INamespaceOrTypeSymbol? container = null; var staticOnly = false;
        if (receiver != null)
        {
            var receiverSymbol = model.GetSymbolInfo(receiver, cancellationToken).Symbol;
            container = receiverSymbol as INamespaceOrTypeSymbol;
            if (receiverSymbol is IAliasSymbol alias) container = alias.Target;
            staticOnly = container != null;
            container ??= model.GetTypeInfo(receiver, cancellationToken).Type;
            if (container == null) return new(span, ImmutableArray<CSharpCompletionItem>.Empty, false);
        }
        var symbols = model.LookupSymbols(offset, container, includeReducedExtensionMethods: true)
            .Where(s => s.CanBeReferencedByName && !s.IsImplicitlyDeclared && s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(s => receiver == null || container is INamespaceSymbol || (staticOnly ? s.IsStatic || s is INamedTypeSymbol : !s.IsStatic && s is not INamedTypeSymbol));
        var items = symbols.Select(s => new CSharpCompletionItem(s.Name, Escape(s.Name), s.Kind.ToString(), s.ToDisplayString()))
            .Distinct().OrderBy(s => s.Label, StringComparer.Ordinal).ThenBy(s => s.Detail, StringComparer.Ordinal).Take(maximumResults + 1).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(span, items.Take(maximumResults).ToImmutableArray(), items.Length > maximumResults);
    }

    public CSharpSignatureResult GetSignatures(string path, int offset, CancellationToken cancellationToken = default)
    {
        var tree = Tree(path); var token = Token(tree, offset, cancellationToken); var model = _compilation.GetSemanticModel(tree);
        var arguments = token.Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>().FirstOrDefault(a =>
            offset >= a.OpenParenToken.Span.End && (a.CloseParenToken.IsMissing || offset <= a.CloseParenToken.SpanStart));
        if (arguments == null) return new(0, ImmutableArray<CSharpSignature>.Empty);
        var index = arguments.Arguments.GetSeparators().Count(s => s.SpanStart < offset);
        IEnumerable<IMethodSymbol> methods = arguments.Parent switch
        {
            InvocationExpressionSyntax invocation => model.GetMemberGroup(invocation.Expression, cancellationToken).OfType<IMethodSymbol>(),
            ObjectCreationExpressionSyntax creation => (model.GetTypeInfo(creation, cancellationToken).Type as INamedTypeSymbol)?.InstanceConstructors ?? ImmutableArray<IMethodSymbol>.Empty,
            ImplicitObjectCreationExpressionSyntax creation => (model.GetTypeInfo(creation, cancellationToken).Type as INamedTypeSymbol)?.InstanceConstructors ?? ImmutableArray<IMethodSymbol>.Empty,
            _ => Enumerable.Empty<IMethodSymbol>()
        };
        var within = model.GetEnclosingSymbol(offset, cancellationToken);
        var selected = model.GetSymbolInfo(arguments.Parent!, cancellationToken).Symbol;
        return new(index, methods.Where(m => within == null || _compilation.IsSymbolAccessibleWithin(m, within.ContainingType ?? (ISymbol)within.ContainingAssembly))
            .Take(100).Select(m => new CSharpSignature(m.ToDisplayString(), m.Parameters.Select(p => p.ToDisplayString()).ToImmutableArray(),
                SameSymbol(m, selected))).ToImmutableArray());
    }

    /// <summary>Formats syntax with Roslyn's whitespace normalizer. Comments, directives and token
    /// contents remain syntax-owned. This deliberately does not emulate desktop .editorconfig options.</summary>
    public ImmutableArray<XamlTextChange> Format(string path, int tabSize = 2, CancellationToken cancellationToken = default)
    {
        if (!_editable.Contains(path)) throw new InvalidOperationException("Generated source is read-only.");
        if (tabSize is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(tabSize));
        var tree = Tree(path); var text = tree.GetText(cancellationToken).ToString();
        var formatted = tree.GetRoot(cancellationToken).NormalizeWhitespace(new string(' ', tabSize), text.Contains("\r\n") ? "\r\n" : "\n").ToFullString();
        return XamlTextDiffer.GetChanges(text, formatted);
    }

    private ImmutableArray<XamlCodeAction> GetLocalTypeActions(string path, int offset, CancellationToken cancellationToken)
    {
        if (!_editable.Contains(path)) return ImmutableArray<XamlCodeAction>.Empty;
        var tree = Tree(path); var token = Token(tree, offset, cancellationToken);
        var declaration = token.Parent?.AncestorsAndSelf().OfType<VariableDeclarationSyntax>().FirstOrDefault();
        if (declaration?.Parent is not LocalDeclarationStatementSyntax || declaration.Variables.Count != 1 || declaration.Variables[0].Initializer?.Value is not { } initializer)
            return ImmutableArray<XamlCodeAction>.Empty;
        var model = _compilation.GetSemanticModel(tree); var type = model.GetTypeInfo(declaration.Type, cancellationToken).Type;
        if (type == null || type.TypeKind == TypeKind.Error || type.IsAnonymousType || type.TypeKind == TypeKind.Pointer || declaration.Type is RefTypeSyntax || MeaningfulTrivia(declaration.Type))
            return ImmutableArray<XamlCodeAction>.Empty;
        var explicitType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string replacement, title;
        if (declaration.Type.IsVar && model.GetAliasInfo(declaration.Type, cancellationToken) == null)
        { replacement = explicitType; title = "Use explicit type"; }
        else
        {
            if (!SymbolEqualityComparer.IncludeNullability.Equals(type, model.GetTypeInfo(initializer, cancellationToken).Type) ||
                initializer is ImplicitObjectCreationExpressionSyntax or CollectionExpressionSyntax || initializer.IsKind(SyntaxKind.NullLiteralExpression))
                return ImmutableArray<XamlCodeAction>.Empty;
            replacement = "var"; title = "Use var";
        }
        var change = new XamlTextChange(new(declaration.Type.SpanStart, declaration.Type.Span.Length), replacement);
        var candidate = tree.WithChangedText(tree.GetText(cancellationToken).WithChanges(new Microsoft.CodeAnalysis.Text.TextChange(
            declaration.Type.Span, replacement)));
        var candidateCompilation = _compilation.ReplaceSyntaxTree(tree, candidate);
        var candidateModel = candidateCompilation.GetSemanticModel(candidate);
        var variablePosition = declaration.Variables[0].Identifier.SpanStart + replacement.Length - declaration.Type.Span.Length;
        var candidateVariable = Token(candidate, variablePosition, cancellationToken).Parent as VariableDeclaratorSyntax;
        var candidateType = candidateVariable == null ? null : (candidateModel.GetDeclaredSymbol(candidateVariable, cancellationToken) as ILocalSymbol)?.Type;
        var display = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
        if (candidateType == null || type.ToDisplayString(display) != candidateType.ToDisplayString(display) ||
            candidateModel.GetDiagnostics(cancellationToken: cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error))
            return ImmutableArray<XamlCodeAction>.Empty;
        return ImmutableArray.Create(new XamlCodeAction(title, "refactor.rewrite", false, ImmutableArray.Create(change)));
    }

    internal static SyntaxToken Token(SyntaxTree tree, int offset, CancellationToken cancellationToken)
    {
        var root = tree.GetRoot(cancellationToken);
        if (offset < 0 || offset > root.FullSpan.End) throw new ArgumentOutOfRangeException(nameof(offset));
        var token = root.FindToken(offset, findInsideTrivia: true);
        if (offset > 0 && (token.IsKind(SyntaxKind.EndOfFileToken) || token.SpanStart == offset && !token.IsKind(SyntaxKind.IdentifierToken)))
        {
            var previous = root.FindToken(offset - 1, findInsideTrivia: true);
            if (previous.Span.End == offset) token = previous;
        }
        return token;
    }

    internal static ISymbol? SymbolAt(SemanticModel model, SyntaxToken token, CancellationToken cancellationToken)
    {
        var node = token.Parent;
        if (node == null || !token.IsKind(SyntaxKind.IdentifierToken) && node is not PredefinedTypeSyntax) return null;
        if (node is NameSyntax name && model.GetAliasInfo(name, cancellationToken) is { } alias) return alias;
        if (node is IdentifierNameSyntax aliasName && aliasName.Parent is NameEqualsSyntax { Parent: UsingDirectiveSyntax usingDirective } && usingDirective.Alias?.Name == aliasName)
            return model.GetDeclaredSymbol(usingDirective, cancellationToken);
        if (node.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { } namespaceDeclaration && namespaceDeclaration.Name.GetLastToken() == token)
            return model.GetDeclaredSymbol(namespaceDeclaration, cancellationToken);
        // Only declaration identifiers own declared symbols. Walking to an enclosing method
        // would incorrectly resolve punctuation, a string, or an unresolved identifier to it.
        var declaration = node switch
        {
            BaseTypeDeclarationSyntax d when d.Identifier == token => node,
            DelegateDeclarationSyntax d when d.Identifier == token => node,
            MethodDeclarationSyntax d when d.Identifier == token => node,
            LocalFunctionStatementSyntax d when d.Identifier == token => node,
            ConstructorDeclarationSyntax d when d.Identifier == token => node,
            DestructorDeclarationSyntax d when d.Identifier == token => node,
            PropertyDeclarationSyntax d when d.Identifier == token => node,
            EventDeclarationSyntax d when d.Identifier == token => node,
            VariableDeclaratorSyntax d when d.Identifier == token => node,
            ParameterSyntax d when d.Identifier == token => node,
            TypeParameterSyntax d when d.Identifier == token => node,
            SingleVariableDesignationSyntax d when d.Identifier == token => node,
            CatchDeclarationSyntax d when d.Identifier == token => node,
            ForEachStatementSyntax d when d.Identifier == token => node,
            EnumMemberDeclarationSyntax d when d.Identifier == token => node,
            LabeledStatementSyntax d when d.Identifier == token => node,
            FromClauseSyntax d when d.Identifier == token => node,
            JoinClauseSyntax d when d.Identifier == token => node,
            JoinIntoClauseSyntax d when d.Identifier == token => node,
            LetClauseSyntax d when d.Identifier == token => node,
            QueryContinuationSyntax d when d.Identifier == token => node,
            _ => null
        };
        return declaration != null ? model.GetDeclaredSymbol(declaration, cancellationToken) : model.GetSymbolInfo(node, cancellationToken).Symbol;
    }

    internal static bool SameSymbol(ISymbol? left, ISymbol? right)
    {
        if (left == null || right == null) return false;
        static ISymbol Normalize(ISymbol value) => value is IMethodSymbol method ?
            (method.ReducedFrom ?? method.PartialDefinitionPart ?? method).OriginalDefinition : value.OriginalDefinition;
        left = Normalize(left); right = Normalize(right);
        return SymbolEqualityComparer.Default.Equals(left, right);
    }
    private ImmutableArray<CSharpLocation> Locations(ISymbol symbol, CancellationToken token) => symbol.Locations
        .Where(l => l.IsInSource && l.SourceTree != null).Select(l => Location(l.SourceTree!, l.SourceSpan, true, token)).ToImmutableArray();
    private CSharpLocation Location(SyntaxTree tree, Span span, bool declaration, CancellationToken token)
    {
        var lines = tree.GetText(token).Lines.GetLinePositionSpan(span);
        return new(tree.FilePath, span.Start, span.Length, lines.Start.Line, lines.Start.Character,
            lines.End.Line, lines.End.Character, declaration, !_editable.Contains(tree.FilePath));
    }
    private static ITypeSymbol? SymbolType(ISymbol? symbol) => symbol switch
    { ITypeSymbol type => type, IFieldSymbol field => field.Type, IPropertySymbol property => property.Type, IEventSymbol ev => ev.Type,
        ILocalSymbol local => local.Type, IParameterSymbol parameter => parameter.Type, IMethodSymbol method => method.ReturnType, _ => null };
    internal static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
    private static void Bound(int value) { if (value is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(value), "Choose a result limit between 1 and 10000."); }
    private static string Clip(string value, int length) => value.Length <= length ? value : value.Substring(0, length);
    private static object? ConstantValue(object? value) => value switch
    {
        double number when double.IsNaN(number) || double.IsInfinity(number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        float number when float.IsNaN(number) || float.IsInfinity(number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value
    };
}

public sealed record CSharpLocation(string Path, int Start, int Length, int StartLine, int StartColumn, int EndLine, int EndColumn, bool IsDeclaration, bool IsGenerated);
public sealed record CSharpSymbolInfo(XamlG.Syntax.TextSpan Span, string? Name, string? Kind, string? Display, string? Type,
    string? Accessibility, bool IsStatic, bool IsImplicit, bool HasConstant, object? Constant, string Documentation,
    ImmutableArray<CSharpLocation> Locations, ImmutableArray<string> Candidates);
public sealed record CSharpReferenceResult(ImmutableArray<CSharpLocation> Locations, bool Truncated);
public sealed record CSharpCompletionItem(string Label, string InsertText, string Kind, string Detail);
public sealed record CSharpCompletionResult(XamlG.Syntax.TextSpan Span, ImmutableArray<CSharpCompletionItem> Items, bool Truncated);
public sealed record CSharpSignature(string Label, ImmutableArray<string> Parameters, bool IsSelected);
public sealed record CSharpSignatureResult(int ActiveParameter, ImmutableArray<CSharpSignature> Signatures);
