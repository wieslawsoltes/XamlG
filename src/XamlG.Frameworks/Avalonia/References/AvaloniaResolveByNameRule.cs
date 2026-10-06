using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.References;

/// <summary>Lowers ResolveByName literals through Avalonia's public markup extension.
/// Its FindAsync contract preserves forward references, template-local namescopes and
/// null results for missing names; it is deliberately not a strict x:Reference rewrite.</summary>
public sealed class AvaloniaResolveByNameRule : IXamlPropertyBindingRule
{
    private const string Attribute = "Avalonia.Controls.ResolveByNameAttribute";
    private const string Extension = "Avalonia.Markup.Xaml.MarkupExtensions.ResolveByNameExtension";

    public bool TryBind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        ImmutableArray<XamlSyntaxNode> values, NamespaceScope scope, TextSpan span, bool isAttribute)
    {
        if (values.Length != 1 || values[0] is not XamlTextSyntax text ||
            text.Value.StartsWith("{", StringComparison.Ordinal) || !HasMarker(member)) return false;
        var objectType = context.Types.Find(AvaloniaMetadata.Object);
        var extensionType = context.Types.Find(Extension);
        var provide = extensionType?.Members(context.Types.Configuration.MarkupExtensionMethod).OfType<IMethodSymbol>()
            .FirstOrDefault(method => !method.IsStatic && !method.IsGenericMethod && !method.ReturnsVoid &&
                method.Parameters.Length == 1 && method.Parameters[0].Type.HasMetadataName(ClrNames.IServiceProvider) &&
                context.Types.IsAccessible(method));
        var adapter = context.Types.Find(AvaloniaRegisteredSetterMetadata.Adapter)?.Members(AvaloniaRegisteredSetterMetadata.Assign)
            .OfType<IMethodSymbol>().SingleOrDefault(method => method.IsStatic && !method.IsGenericMethod && method.Parameters.Length == 3 &&
                method.Parameters[0].Type.HasMetadataName(AvaloniaMetadata.Object) &&
                method.Parameters[1].Type.HasMetadataName(AvaloniaMetadata.Property) &&
                method.Parameters[2].Type.SpecialType == SpecialType.System_Object && context.Types.IsAccessible(method));
        if (member.TargetDescriptor == null || objectType == null || extensionType == null || provide == null || adapter == null ||
            !context.Types.Compilation.ClassifyCommonConversion(target.Type, objectType).IsImplicit)
        {
            context.Report("XG3002", "ResolveByName assignment requires an Avalonia registered property and the matching public markup/runtime contracts.", span);
            return true;
        }
        var syntax = new XamlElementSyntax(extensionType.Name, text.Span, text.Span, new TextSpan(text.Span.End, 0),
            ImmutableArray<XamlAttributeSyntax>.Empty, ImmutableArray<XamlSyntaxNode>.Empty, true, text.Span);
        var extension = context.Objects.Bind(syntax, scope, extensionType, false, target.NameScopeId,
            ImmutableArray.Create<XamlSyntaxNode>(text));
        if (extension == null) return true;
        if (!target.AssignedScalars.Add(member.Symbol.ToDisplayString()))
        {
            context.Report("XG1014", $"Property '{member.Name}' is assigned more than once.", span);
            return true;
        }
        var value = new BoundMarkupExpression(extension, provide, provide.ReturnType, text.Span);
        target.Assignments.Add(new BoundCallAssignment(adapter, ImmutableArray.Create<BoundExpression>(member.TargetDescriptor, value), true, span)
        { OwnResult = true, TargetDescriptor = member.TargetDescriptor });
        return true;
    }

    internal static bool HasMarker(BoundMember member)
    {
        static bool Marked(ISymbol? symbol) => symbol?.GetAttributes().Any(a => a.AttributeClass?.HasMetadataName(Attribute) == true) == true;
        if (Marked(member.Symbol) || Marked(member.Getter) || Marked(member.Setter)) return true;
        for (var property = (member.Symbol as IPropertySymbol)?.OverriddenProperty; property != null; property = property.OverriddenProperty)
            if (Marked(property)) return true;
        return false;
    }
}
