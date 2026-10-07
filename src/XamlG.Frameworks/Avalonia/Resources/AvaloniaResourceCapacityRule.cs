using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia.Resources;

/// <summary>Reserves a resource group's capacity before its assignments execute.</summary>
public sealed class AvaloniaResourceCapacityRule : IXamlObjectBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target) { }

    public void Complete(BindingContext context, ObjectBindingBuilder target)
    {
        var dictionary = context.Types.Find(AvaloniaResourceMetadata.Dictionary);
        var contract = context.Types.Find(AvaloniaResourceMetadata.DictionaryContract);
        if (dictionary == null || contract == null) return;
        var direct = context.Types.Compilation.ClassifyCommonConversion(target.Type, dictionary).IsImplicit;
        var resources = new List<BoundMember?>();
        foreach (var assignment in target.Assignments)
        {
            var member = assignment switch { BoundAddAssignment add => add.Collection, BoundSetAssignment set => set.Member, _ => null };
            if (member?.Name == "Resources" && member.Getter is { } getter &&
                context.Types.Compilation.ClassifyCommonConversion(getter.ReturnType, contract).IsImplicit)
                resources.Add(member);
            else if (direct && assignment is BoundAddAssignment { Collection: null, Arguments.Length: 2 }) resources.Add(null);
            else if (direct && assignment is BoundCallAssignment call && call.Method.ContainingType.HasMetadataName(AvaloniaResourceMetadata.Operations) &&
                call.Method.Name is AvaloniaResourceMetadata.SetResource or AvaloniaResourceMetadata.SetNotSharedDeferredResource) resources.Add(null);
        }
        if (resources.Count < 2 || resources.Any(member => !SymbolEqualityComparer.Default.Equals(member?.Getter, resources[0]?.Getter))) return;
        var method = context.Types.Find(AvaloniaResourceMetadata.Operations)?.GetMembers("EnsureCapacity").OfType<IMethodSymbol>()
            .FirstOrDefault(candidate => candidate.IsStatic && candidate.Parameters.Length == 2);
        if (method == null)
        { context.Report("XG3308", "Resource preallocation requires the matching runtime capacity helper.", target.Syntax.NameSpan); return; }
        target.Assignments.Insert(0, new BoundCallAssignment(method,
            ImmutableArray.Create<BoundExpression>(new BoundConstantExpression(resources.Count, context.Types.Special(SpecialType.System_Int32), target.Syntax.NameSpan)),
            true, target.Syntax.NameSpan) { TargetMember = resources[0] });
    }
}
