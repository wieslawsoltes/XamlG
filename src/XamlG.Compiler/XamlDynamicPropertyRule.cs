using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>
/// Binds a value once against object and dispatches it through ordered setter candidates.
/// A provider owns framework policy; neither type names nor receiver paths are hardcoded here.
/// </summary>
public sealed class XamlDynamicPropertyRule : IXamlPropertyBindingRule
{
    private readonly IXamlValueSetterProvider _provider;

    public XamlDynamicPropertyRule(IXamlValueSetterProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public bool TryBind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        ImmutableArray<XamlSyntaxNode> values, NamespaceScope scope, TextSpan span, bool isAttribute)
    {
        var setters = _provider.GetSetters(context, target, member);
        if (setters.IsDefaultOrEmpty) return false;
        if (values.Length != 1)
        {
            context.Report("XG1014", "A dynamic scalar assignment requires exactly one value.", span);
            return true;
        }
        if (!target.AssignedScalars.Add(member.Symbol.ToDisplayString()))
        {
            context.Report("XG1014", $"Property '{member.Name}' is assigned more than once.", span);
            return true;
        }
        var value = context.Values.BindNode(values[0], context.Types.Special(SpecialType.System_Object),
            scope, target.NameScopeId, normalizeText: !isAttribute);
        if (value != null) target.Assignments.Add(new BoundDynamicSetAssignment(member, value, setters, span));
        return true;
    }
}
