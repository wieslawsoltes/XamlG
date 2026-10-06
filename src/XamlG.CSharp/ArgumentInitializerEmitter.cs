using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Roslyn;
namespace XamlG.CSharp;

internal static class ArgumentInitializerEmitter
{
    public static void Emit(EmissionContext context, string target,
        ImmutableArray<BoundArgumentInitialization> initializers, IReadOnlyList<string> arguments)
    {
        foreach (var initializer in initializers)
        {
            var property = initializer.Property;
            if (initializer.ValueArgumentIndex < 0 || initializer.ValueArgumentIndex >= arguments.Count ||
                property.IsStatic || property.IsIndexer || property.SetMethod == null)
                throw new InvalidOperationException("Invalid bound argument initialization contract.");
            context.Writer.Line("((" + property.ContainingType.CSharpName() + ")" + target + ")." +
                CSharpNames.Identifier(property.Name) + " = " + arguments[initializer.ValueArgumentIndex] + ";");
        }
    }
}
