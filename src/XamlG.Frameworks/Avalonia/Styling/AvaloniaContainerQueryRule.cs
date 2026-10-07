using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

public sealed class AvaloniaContainerQueryRule : IXamlPropertyBindingRule
{
    public bool TryBind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        ImmutableArray<XamlSyntaxNode> values, NamespaceScope scope, TextSpan span, bool isAttribute)
    {
        if (member.Name != "Query" || context.Types.Find(AvaloniaStyleMetadata.ContainerQuery) is not { } container ||
            !context.Types.Compilation.ClassifyCommonConversion(target.Type, container).IsImplicit) return false;
        if (values.Length != 1 || values[0] is not XamlTextSyntax text)
        { context.Report("XG3111", "ContainerQuery.Query requires exactly one text value.", span); return true; }
        var syntax = AvaloniaContainerQueryParser.Parse(text.Value, text.Span, context.Diagnostics.Add, context.Cancellation);
        var value = syntax == null ? null : new AvaloniaContainerQueryBinder(context).Bind(syntax);
        if (value != null) context.Members.AddSet(target, member, value, span);
        return true;
    }
}
