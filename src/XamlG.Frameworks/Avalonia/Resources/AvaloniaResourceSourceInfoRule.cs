using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Resources;

/// <summary>Records keyed locations when entries are added, independently of deferred value construction.</summary>
public sealed class AvaloniaResourceSourceInfoRule : IXamlObjectBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target) { }

    public void Complete(BindingContext context, ObjectBindingBuilder target)
    {
        if (context.Runtime.SourceInfo is not { } source) return;
        var dictionary = context.Types.Find(AvaloniaResourceMetadata.Dictionary);
        var isDictionary = dictionary != null && context.Types.Compilation.ClassifyCommonConversion(target.Type, dictionary).IsImplicit;
        for (var index = 0; index < target.Assignments.Count; index++)
        {
            var add = target.Assignments[index] as BoundAddAssignment;
            var call = target.Assignments[index] as BoundCallAssignment;
            var merged = call?.Method.ContainingType.HasMetadataName(AvaloniaResourceMetadata.Operations) == true &&
                call.Method.Name is AvaloniaResourceMetadata.SetResource or AvaloniaResourceMetadata.SetNotSharedDeferredResource;
            var resource = add?.Arguments.Length == 2 && (isDictionary && add.Collection == null ||
                add.Collection?.Name == "Resources" && add.Collection.Getter?.ReturnType.HasMetadataName(AvaloniaResourceMetadata.DictionaryContract) == true);
            if (!resource && !merged) continue;
            var setter = source.ObjectSetter.ContainingType.GetMembers(source.ObjectSetter.Name).OfType<IMethodSymbol>().FirstOrDefault(method =>
                method.IsStatic && method.ReturnsVoid && context.Types.IsAccessible(method) && method.Parameters.Length == 3 &&
                method.Parameters[0].Type.HasMetadataName(AvaloniaResourceMetadata.DictionaryContract) &&
                method.Parameters[1].Type.SpecialType == SpecialType.System_Object &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[2].Type, source.Constructor.ContainingType));
            if (setter == null)
            { context.Report("XG3308", "Resource source information requires the public keyed metadata setter.", target.Assignments[index].Span); continue; }
            var value = (add?.Arguments ?? call!.Arguments)[1];
            var postCall = new BoundPostCall(setter, ImmutableArray.Create(0),
                ImmutableArray.Create<BoundExpression>(source.CreateValue(context.Syntax, Location(context.Syntax, value))));
            target.Assignments[index] = add != null
                ? add with { PostCall = postCall, Alternatives = ImmutableArray<IMethodSymbol>.Empty }
                : call! with { PostCall = postCall };
        }
    }

    private static TextSpan Location(XamlSyntaxTree syntax, BoundExpression value)
    {
        if (value is BoundDeferredExpression deferred) return Location(syntax, deferred.Content);
        if (value is BoundCastExpression cast) return Location(syntax, cast.Value);
        if (value.SourceInfoSpan is { } location) return location;
        if (value is BoundValueConverterExpression converter) return Location(syntax, converter.Value);
        if (value is BoundObjectExpression obj) return obj.Object.Syntax.NameSpan;
        if (value is BoundMarkupExpression markup) return markup.Extension.Syntax.NameSpan;
        if (value is BoundConstantExpression or BoundParseExpression or BoundConverterExpression or BoundNewExpression)
            return BoundSourceInfo.ValueLocation(syntax, value.Span);
        var element = syntax.FindElement(value.Span.Start);
        return element?.NameSpan ?? value.Span;
    }
}
