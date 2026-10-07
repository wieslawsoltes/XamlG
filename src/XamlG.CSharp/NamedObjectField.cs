using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;

namespace XamlG.CSharp;

internal sealed record NamedObjectField(string Name, BoundObject Object)
{
    public static ImmutableArray<NamedObjectField> Collect(BoundObject root)
    {
        var fields = ImmutableArray.CreateBuilder<NamedObjectField>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in BoundTraversal.Objects(root).Where(value => value.NameScopeId == root.NameScopeId))
        {
            void Add(string name)
            {
                // Runtime names need not be C# identifiers. Keep their original namescope keys.
                if (SyntaxFacts.IsValidIdentifier(name) && SyntaxFactory.ParseToken(CSharpNames.Identifier(name)).ValueText == name && names.Add(name))
                    fields.Add(new(name, value));
            }
            if (value.Name != null) Add(value.Name);
            foreach (var assignment in value.Assignments.OfType<BoundSetAssignment>().Where(assignment => assignment.RegisterName))
            {
                var expression = assignment.Value;
                while (expression is BoundCastExpression cast) expression = cast.Value;
                if (expression is BoundConstantExpression { Value: string name }) Add(name);
            }
        }
        return fields.ToImmutable();
    }
}
