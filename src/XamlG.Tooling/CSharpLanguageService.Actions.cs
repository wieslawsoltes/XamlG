using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XamlG.Syntax;
using XamlG.Tooling.Refactoring;

namespace XamlG.Tooling;

public sealed partial class CSharpLanguageService
{
    public ImmutableArray<XamlCodeAction> GetActions(string path, int offset, CancellationToken cancellationToken = default)
    {
        if (!_editable.Contains(path)) return ImmutableArray<XamlCodeAction>.Empty;
        var tree = Tree(path); var token = Token(tree, offset, cancellationToken); var node = token.Parent;
        var result = GetLocalTypeActions(path, offset, cancellationToken).ToBuilder();
        if (node == null) return result.ToImmutable();
        var model = _compilation.GetSemanticModel(tree);
        var verifier = new ActionVerifier(this, tree, cancellationToken);
        void Offer(string title, SyntaxNode original, Func<SyntaxNode, SyntaxNode> rewrite, bool format = false)
        {
            var change = verifier.Create(original, rewrite, format);
            if (change != null) result.Add(new(title, "refactor.rewrite", false, ImmutableArray.Create(change)));
        }

        var creation = node.AncestorsAndSelf().OfType<BaseObjectCreationExpressionSyntax>().FirstOrDefault();
        if (creation is ObjectCreationExpressionSyntax { ArgumentList: not null } explicitCreation && !MeaningfulTrivia(explicitCreation.Type))
            Offer("Use target-typed new", explicitCreation, current =>
            {
                var value = (ObjectCreationExpressionSyntax)current;
                return SyntaxFactory.ImplicitObjectCreationExpression(value.ArgumentList!, value.Initializer).WithNewKeyword(value.NewKeyword).WithTriviaFrom(value);
            });
        else if (creation is ImplicitObjectCreationExpressionSyntax implicitCreation && model.GetTypeInfo(creation, cancellationToken).Type is { } createdType && !createdType.IsAnonymousType)
            Offer("Use explicit object creation", implicitCreation, current =>
            {
                var value = (ImplicitObjectCreationExpressionSyntax)current;
                return SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(TypeDisplay(createdType)))
                    .WithNewKeyword(value.NewKeyword.WithTrailingTrivia(value.NewKeyword.TrailingTrivia.Add(SyntaxFactory.Space)))
                    .WithArgumentList(value.ArgumentList).WithInitializer(value.Initializer).WithTriviaFrom(value);
            });

        if (node is SimpleNameSyntax simple && model.GetSymbolInfo(simple, cancellationToken).Symbol is { } selected && model.GetAliasInfo(simple, cancellationToken) == null)
        {
            if (selected is INamedTypeSymbol named && simple.Parent is not QualifiedNameSyntax && simple.Parent is not AliasQualifiedNameSyntax && !MeaningfulTrivia(simple))
            {
                var display = TypeDisplay(named);
                if (simple.ToString() != display) Offer("Qualify type name", simple, _ => SyntaxFactory.ParseTypeName(display).WithTriviaFrom(simple));
            }
            else if (selected is IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol &&
                simple.Parent is not MemberAccessExpressionSyntax && simple.Parent is not MemberBindingExpressionSyntax && simple.Parent is not NameColonSyntax && simple.Parent is not NameEqualsSyntax &&
                selected.ContainingType is { } owner && !selected.IsImplicitlyDeclared && selected is not IMethodSymbol { MethodKind: MethodKind.LocalFunction })
            {
                var receiver = selected.IsStatic ? TypeDisplay(owner) : "this";
                Offer(selected.IsStatic ? "Qualify static member" : "Qualify member with this", simple,
                    current => SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ParseExpression(receiver), (SimpleNameSyntax)current.WithoutTrivia()).WithTriviaFrom(current));
            }
            TypeSyntax typeSyntax = simple;
            while (typeSyntax.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax) typeSyntax = (TypeSyntax)typeSyntax.Parent;
            if (!MeaningfulTrivia(typeSyntax) && model.GetTypeInfo(typeSyntax, cancellationToken).Type is { } type && Predefined(type.SpecialType) is { } keyword && typeSyntax.ToString() != keyword)
                Offer("Use predefined type", typeSyntax, _ => SyntaxFactory.ParseTypeName(keyword).WithTriviaFrom(typeSyntax));
        }
        if (node is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            var value = literal.Token.ValueText;
            if (value.Length is > 0 and <= 512 && SyntaxFacts.IsValidIdentifier(Escape(value)) &&
                model.LookupSymbols(literal.SpanStart, name: value).Any(symbol => symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol or INamedTypeSymbol))
                Offer("Use nameof(" + Escape(value) + ")", literal, _ => SyntaxFactory.ParseExpression("nameof(" + Escape(value) + ")").WithTriviaFrom(literal));
        }

        if (node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault() is { } method)
        {
            if (method.ExpressionBody is { } arrow && !MeaningfulTrivia(arrow) && !MeaningfulLeadingTrivia(method.SemicolonToken))
                Offer("Use block body", method, current =>
                {
                    var value = (MethodDeclarationSyntax)current;
                    var statement = BodyStatement(value.ExpressionBody!.Expression, model.GetDeclaredSymbol(method, cancellationToken)?.ReturnsVoid == true);
                    return value.WithExpressionBody(null).WithSemicolonToken(default).WithBody(SyntaxFactory.Block(statement));
                }, true);
            else if (method.Body is { Statements.Count: 1 } body && !MeaningfulTrivia(body) && BodyExpression(body.Statements[0]) != null)
                Offer("Use expression body", method, current =>
                {
                    var value = (MethodDeclarationSyntax)current;
                    return value.WithBody(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(BodyExpression(value.Body!.Statements[0])!)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                }, true);
        }
        if (node.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().FirstOrDefault() is { } property)
        {
            if (property.ExpressionBody is { } arrow && !MeaningfulTrivia(arrow) && !MeaningfulLeadingTrivia(property.SemicolonToken))
                Offer("Use property block body", property, current =>
                {
                    var value = (PropertyDeclarationSyntax)current;
                    var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(SyntaxFactory.Block(BodyStatement(value.ExpressionBody!.Expression, false)));
                    return value.WithExpressionBody(null).WithSemicolonToken(default).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter)));
                }, true);
            else if (property.AccessorList is { Accessors.Count: 1 } accessors && accessors.Accessors[0] is { Body.Statements.Count: 1 } get &&
                get.IsKind(SyntaxKind.GetAccessorDeclaration) && get.AttributeLists.Count == 0 && get.Modifiers.Count == 0 && !MeaningfulTrivia(accessors) && BodyExpression(get.Body.Statements[0]) != null)
                Offer("Use property expression body", property, current =>
                {
                    var value = (PropertyDeclarationSyntax)current;
                    return value.WithAccessorList(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(BodyExpression(value.AccessorList!.Accessors[0].Body!.Statements[0])!))
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                }, true);
        }
        return result.ToImmutable();
    }
    private static StatementSyntax BodyStatement(ExpressionSyntax expression, bool returnsVoid) => expression is ThrowExpressionSyntax thrown
        ? SyntaxFactory.ThrowStatement(thrown.Expression) : returnsVoid ? SyntaxFactory.ExpressionStatement(expression) : SyntaxFactory.ReturnStatement(expression);
    private static ExpressionSyntax? BodyExpression(StatementSyntax statement) => statement switch
    { ReturnStatementSyntax { Expression: { } expression } => expression, ExpressionStatementSyntax expression => expression.Expression,
        ThrowStatementSyntax { Expression: { } expression } => SyntaxFactory.ThrowExpression(expression), _ => null };
    private static bool MeaningfulTrivia(SyntaxNode node) => node.DescendantTrivia(descendIntoTrivia: true).Any(trivia =>
        !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia));
    // Trailing trivia after the semicolon lies outside the replaced declaration Span
    // and is retained by the source edit even when the semicolon becomes a block.
    private static bool MeaningfulLeadingTrivia(SyntaxToken token) => token.LeadingTrivia.Any(trivia =>
        !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia));
    private static string TypeDisplay(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
    private static string? Predefined(SpecialType type) => type switch
    {
        SpecialType.System_Boolean => "bool", SpecialType.System_Byte => "byte", SpecialType.System_SByte => "sbyte", SpecialType.System_Int16 => "short",
        SpecialType.System_UInt16 => "ushort", SpecialType.System_Int32 => "int", SpecialType.System_UInt32 => "uint", SpecialType.System_Int64 => "long",
        SpecialType.System_UInt64 => "ulong", SpecialType.System_Char => "char", SpecialType.System_Single => "float", SpecialType.System_Double => "double",
        SpecialType.System_Decimal => "decimal", SpecialType.System_String => "string", SpecialType.System_Object => "object", SpecialType.System_Void => "void", _ => null
    };

    // Annotations follow retained identifiers through syntax rewrites. Every surviving
    // identifier, including those outside the edit, must keep the same declaration.
    // New/removed syntax also has its type, constant and constructor/call checked.
    private sealed class ActionVerifier
    {
        private readonly CSharpLanguageService _owner;
        private readonly SyntaxTree _tree;
        private readonly SemanticModel _model;
        private readonly CancellationToken _token;
        private SyntaxNode? _annotated;
        private readonly Dictionary<int, string> _marks = new();
        private readonly List<(SyntaxToken Token, ISymbol Symbol, string Id)> _observations = new();
        private bool _overBudget;
        internal ActionVerifier(CSharpLanguageService owner, SyntaxTree tree, CancellationToken token)
        { _owner = owner; _tree = tree; _model = owner._compilation.GetSemanticModel(tree); _token = token; }
        private void Annotate()
        {
            if (_annotated != null || _overBudget) return;
            var root = _tree.GetRoot(_token);
            foreach (var item in root.DescendantTokens(descendIntoTrivia: true).Where(item => item.IsKind(SyntaxKind.IdentifierToken)))
            {
                _token.ThrowIfCancellationRequested();
                if (_observations.Count == 20000) { _overBudget = true; return; }
                if (SymbolAt(_model, item, _token) is not { } symbol) continue;
                var id = _observations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _observations.Add((item, symbol, id)); _marks.Add(item.SpanStart, id);
            }
            _annotated = root.ReplaceTokens(_observations.Select(item => item.Token), (item, _) => item.WithAdditionalAnnotations(new SyntaxAnnotation("xamlg-action-binding", _marks[item.SpanStart])));
        }
        internal XamlTextChange? Create(SyntaxNode original, Func<SyntaxNode, SyntaxNode> rewrite, bool format)
        {
            Annotate(); if (_overBudget) return null;
            _token.ThrowIfCancellationRequested();
            var current = _annotated!.FindNode(original.Span, findInsideTrivia: true, getInnermostNodeForTie: true);
            while (current.RawKind != original.RawKind && current.Parent != null && current.Parent.Span == original.Span) current = current.Parent;
            if (current.RawKind != original.RawKind) return null;
            var replacement = rewrite(current).WithAdditionalAnnotations(new SyntaxAnnotation("xamlg-action-target"));
            if (format) replacement = replacement.NormalizeWhitespace("  ", _tree.GetText(_token).ToString().Contains("\r\n") ? "\r\n" : "\n");
            var root = _annotated.ReplaceNode(current, replacement);
            var tree = CSharpSyntaxTree.Create((CSharpSyntaxNode)root, (CSharpParseOptions)_tree.Options, _tree.FilePath, _tree.Encoding);
            var compilation = _owner._compilation.ReplaceSyntaxTree(_tree, tree);
            if (compilation.GetDiagnostics(_token).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)) return null;
            var model = compilation.GetSemanticModel(tree); root = tree.GetRoot(_token);
            var target = root.GetAnnotatedNodes("xamlg-action-target").Single();
            var oldType = original is ExpressionSyntax oldExpression ? _model.GetTypeInfo(oldExpression, _token) : default;
            var newType = target is ExpressionSyntax newExpression ? model.GetTypeInfo(newExpression, _token) : default;
            if (!TypesEqual(oldType.Type, newType.Type) || !TypesEqual(oldType.ConvertedType, newType.ConvertedType)) return null;
            var oldConstant = original is ExpressionSyntax oldValue ? _model.GetConstantValue(oldValue, _token) : default;
            var newConstant = target is ExpressionSyntax newValue ? model.GetConstantValue(newValue, _token) : default;
            if (oldConstant.HasValue != newConstant.HasValue || oldConstant.HasValue && !Equals(oldConstant.Value, newConstant.Value)) return null;
            var tokens = root.GetAnnotatedTokens("xamlg-action-binding").ToDictionary(item => item.GetAnnotations("xamlg-action-binding").Single().Data!, StringComparer.Ordinal);
            var symbols = new Dictionary<ISymbol, ISymbol?>(SymbolEqualityComparer.Default);
            var service = new CSharpLanguageService(compilation, _owner._editable);
            var selected = original is ExpressionSyntax oldSyntax ? _model.GetSymbolInfo(oldSyntax, _token).Symbol : _model.GetDeclaredSymbol(original, _token);
            var nextSelected = target is ExpressionSyntax newSyntax ? model.GetSymbolInfo(newSyntax, _token).Symbol : model.GetDeclaredSymbol(target, _token);
            if (selected != null && !Equivalent(selected, nextSelected, service, tokens, symbols)) return null;
            foreach (var observation in _observations)
            {
                _token.ThrowIfCancellationRequested();
                if (!tokens.TryGetValue(observation.Id, out var updated))
                { if (!original.Span.Contains(observation.Token.Span)) return null; continue; }
                var actual = SymbolAt(model, updated, _token);
                if (!Equivalent(observation.Symbol, actual, service, tokens, symbols)) return null;
                if (!TypesEqual(SymbolType(observation.Symbol), SymbolType(actual))) return null;
            }
            var source = _tree.GetText(_token).ToString(); var text = target.WithoutLeadingTrivia().WithoutTrailingTrivia().ToFullString();
            if (format)
            {
                var lineStart = source.LastIndexOf('\n', Math.Max(0, original.SpanStart - 1)); var prefix = source.Substring(lineStart + 1, original.SpanStart - lineStart - 1);
                if (prefix.All(character => character is ' ' or '\t')) text = text.Replace("\n", "\n" + prefix);
            }
            return source.Substring(original.SpanStart, original.Span.Length) == text ? null : new(new(original.SpanStart, original.Span.Length), text);
        }
        private bool Equivalent(ISymbol previous, ISymbol? actual, CSharpLanguageService service, IReadOnlyDictionary<string, SyntaxToken> tokens,
            Dictionary<ISymbol, ISymbol?> symbols)
        {
            if (actual == null) return false;
            if (SameSymbol(previous, actual)) return true;
            var declaration = previous.GetDocumentationCommentId();
            if (declaration != null) return declaration == actual.GetDocumentationCommentId() && Equals(previous.ContainingAssembly?.Identity, actual.ContainingAssembly?.Identity);
            if (symbols.TryGetValue(previous, out var cached)) return SameSymbol(cached, actual) || previous.IsImplicitlyDeclared && ImplicitSame(previous, actual);
            var location = previous.Locations.FirstOrDefault(item => item.IsInSource);
            if (location?.SourceTree is { } source)
            {
                var position = location.SourceSpan.Start;
                if (ReferenceEquals(source, _tree))
                {
                    if (!_marks.TryGetValue(position, out var id)) return previous.IsImplicitlyDeclared && ImplicitSame(previous, actual);
                    if (!tokens.TryGetValue(id, out var mapped)) return false;
                    position = mapped.SpanStart;
                }
                var expected = service.ResolveSymbol(source.FilePath, position, _token); symbols.Add(previous, expected);
                return SameSymbol(expected, actual) || previous.IsImplicitlyDeclared && ImplicitSame(previous, actual);
            }
            return previous.Kind == actual.Kind && previous.ToDisplayString() == actual.ToDisplayString() && Equals(previous.ContainingAssembly?.Identity, actual.ContainingAssembly?.Identity);
        }
        private static bool ImplicitSame(ISymbol before, ISymbol after) => after.IsImplicitlyDeclared && before.Kind == after.Kind && before.ToDisplayString() == after.ToDisplayString();
        private static bool TypesEqual(ITypeSymbol? before, ITypeSymbol? after)
        {
            if (before == null || after == null) return before == null && after == null;
            if (before.TypeKind != after.TypeKind || before.NullableAnnotation != after.NullableAnnotation || TypeDisplay(before) != TypeDisplay(after)) return false;
            if (!Equals(before.ContainingAssembly?.Identity, after.ContainingAssembly?.Identity)) return false;
            if (before is INamedTypeSymbol left && after is INamedTypeSymbol right)
                return left.TypeArguments.Length == right.TypeArguments.Length && left.TypeArguments.Zip(right.TypeArguments, TypesEqual).All(equal => equal);
            if (before is IArrayTypeSymbol a && after is IArrayTypeSymbol b) return a.Rank == b.Rank && TypesEqual(a.ElementType, b.ElementType);
            if (before is IPointerTypeSymbol p && after is IPointerTypeSymbol q) return TypesEqual(p.PointedAtType, q.PointedAtType);
            return true;
        }
    }
}
