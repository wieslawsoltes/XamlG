using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

// Registration fields are a framework naming contract, unlike arbitrary same-spelled
// declarations. Recognize their actual Avalonia property/event types and Register calls.
internal sealed record XamlFrameworkRenameChanges(ImmutableArray<KeyValuePair<ISymbol, string>> Names, ImmutableArray<XamlDocumentEdits> Documents)
{
    internal static XamlFrameworkRenameChanges Create(CSharpCompilation compilation, CSharpRenameSymbols targets,
        ImmutableHashSet<string> editable, CancellationToken token)
    {
        var names = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var changes = new Dictionary<string, List<XamlTextChange>>(StringComparer.Ordinal);
        var registrations = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var symbol in targets.Items)
        {
            token.ThrowIfCancellationRequested();
            if (symbol.ContainingType is not { } owner || !SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly)) continue;
            string? logical = null; string? replacement = null; string suffix = "Property";
            var renamed = targets.MappedName(symbol);
            switch (symbol)
            {
                case IPropertySymbol { IsIndexer: false, IsStatic: false } property: logical = property.Name; replacement = renamed; break;
                case IEventSymbol ev: logical = ev.Name; replacement = renamed; suffix = "Event"; break;
                case IFieldSymbol field when RegistrationType(field.Type):
                    suffix = field.Name.EndsWith("Property", StringComparison.Ordinal) ? "Property" : "Event";
                    if (!field.Name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    if (!renamed.EndsWith(suffix, StringComparison.Ordinal) || renamed.Length == suffix.Length)
                        throw new InvalidOperationException("An Avalonia registration field rename must retain its Property/Event suffix.");
                    logical = field.Name.Substring(0, field.Name.Length - suffix.Length); replacement = renamed.Substring(0, renamed.Length - suffix.Length); break;
                case IMethodSymbol { IsStatic: true } method:
                    var prefix = method.Name.StartsWith("Get", StringComparison.Ordinal) ? "Get" : method.Name.StartsWith("Set", StringComparison.Ordinal) ? "Set" :
                        method.Name.StartsWith("Add", StringComparison.Ordinal) ? "Add" : method.Name.StartsWith("Remove", StringComparison.Ordinal) ? "Remove" : "";
                    if (prefix.Length == 0) continue;
                    var tail = prefix is "Add" or "Remove" ? "Handler" : "";
                    if (!method.Name.EndsWith(tail, StringComparison.Ordinal) || method.Name.Length <= prefix.Length + tail.Length) continue;
                    logical = method.Name.Substring(prefix.Length, method.Name.Length - prefix.Length - tail.Length);
                    suffix = tail.Length == 0 ? "Property" : "Event";
                    if (!owner.GetMembers(logical + suffix).OfType<IFieldSymbol>().Any(field => RegistrationType(field.Type))) continue;
                    if (!renamed.StartsWith(prefix, StringComparison.Ordinal) || !renamed.EndsWith(tail, StringComparison.Ordinal) || renamed.Length <= prefix.Length + tail.Length)
                        throw new InvalidOperationException("An Avalonia attached accessor rename must preserve its Get/Set or Add/Remove...Handler shape.");
                    replacement = renamed.Substring(prefix.Length, renamed.Length - prefix.Length - tail.Length); break;
            }
            if (logical == null || replacement == null || logical == replacement) continue;
            var registration = owner.GetMembers(logical + suffix).OfType<IFieldSymbol>().SingleOrDefault(field => RegistrationType(field.Type));
            if (registration == null) continue;
            Add(registration, replacement + suffix);
            foreach (var wrapper in owner.GetMembers(logical).Where(member => member is IPropertySymbol or IEventSymbol)) Add(wrapper, replacement);
            foreach (var prefix in suffix == "Property" ? new[] { "Get", "Set" } : new[] { "Add", "Remove" })
            {
                var tail = suffix == "Property" ? "" : "Handler";
                foreach (var method in owner.GetMembers(prefix + logical + tail).OfType<IMethodSymbol>().Where(method => method.IsStatic))
                    Add(method, prefix + replacement + tail);
            }
            if (!registrations.Add(registration)) continue;
            var declaration = registration.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(token)).OfType<VariableDeclaratorSyntax>().SingleOrDefault();
            if (declaration?.Initializer is not { } initializer || !editable.Contains(declaration.SyntaxTree.FilePath))
                throw new InvalidOperationException("The Avalonia registration has no editable initializer.");
            var model = compilation.GetSemanticModel(declaration.SyntaxTree);
            if (model.GetOperation(initializer.Value, token) is not IInvocationOperation invocation || !RegistrationType(invocation.TargetMethod.ContainingType) ||
                invocation.TargetMethod.Name is not ("Register" or "RegisterAttached" or "RegisterDirect"))
                throw new InvalidOperationException("This Avalonia property/event uses a shared or custom registration. Rename its owning registration contract first.");
            var argument = invocation.Arguments.SingleOrDefault(argument => argument.Parameter?.Name == "name");
            if (argument == null || !argument.Value.ConstantValue.HasValue || !Equals(argument.Value.ConstantValue.Value, logical))
                throw new InvalidOperationException("The Avalonia registration name does not match the selected CLR contract.");
            if (argument.Value.Syntax is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                if (!changes.TryGetValue(declaration.SyntaxTree.FilePath, out var edits)) changes.Add(declaration.SyntaxTree.FilePath, edits = new());
                edits.Add(new(new(literal.SpanStart, literal.Span.Length), Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(replacement, true)));
            }
            else if (argument.Value.Syntax is not InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } })
                throw new InvalidOperationException("Use a literal registration name or nameof its property before renaming this Avalonia contract.");
        }
        return new(names.ToImmutableArray(), changes.Select(item => new XamlDocumentEdits(item.Key,
            compilation.SyntaxTrees.Single(tree => tree.FilePath == item.Key).GetText(token).ToString(), null,
            item.Value.Distinct().OrderBy(change => change.Span.Start).ToImmutableArray())).ToImmutableArray());
        void Add(ISymbol symbol, string name)
        {
            symbol = CSharpRenameSymbols.Normalize(symbol);
            if (names.TryGetValue(symbol, out var existing) && existing != name) throw new InvalidOperationException("An Avalonia registration has conflicting rename targets.");
            names[symbol] = name;
        }
    }
    private static bool RegistrationType(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (current.Name == "AvaloniaProperty" && current.ContainingNamespace.ToDisplayString() == "Avalonia" ||
                current.Name == "RoutedEvent" && current.ContainingNamespace.ToDisplayString() == "Avalonia.Interactivity") return true;
        return false;
    }
}
