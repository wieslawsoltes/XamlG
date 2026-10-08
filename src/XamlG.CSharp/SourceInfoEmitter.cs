using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp;

internal sealed class SourceInfoEmitter(EmissionContext context)
{
    public void EmitConstructed(BoundObject value, string target)
    {
        if (context.Document.Runtime.SourceInfo == null || value.Type.IsValueType || value.FactoryMethod != null) return;
        var syntax = context.Document.Syntax;
        var attribute = syntax.FindElement(value.Syntax.Span.Start)?.Attributes.FirstOrDefault(item =>
            item.ValueSpan.Start <= value.Syntax.NameSpan.Start && value.Syntax.NameSpan.Start < item.ValueSpan.End);
        EmitConstructed(target, attribute?.NameSpan ?? value.Syntax.NameSpan);
    }

    public void EmitConstructed(string target, TextSpan location)
    {
        if (context.Document.Runtime.SourceInfo is not { } source) return;
        var metadata = source.CreateValue(context.Document.Syntax, location);
        var arguments = metadata.Arguments.Cast<BoundConstantExpression>().Select(argument => argument.Value switch
        {
            null => "null", string text => CSharpNames.Literal(text),
            int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("Unexpected source-information constructor argument.")
        });
        context.Writer.Line(source.ObjectSetter.ContainingType.CSharpName() + "." + CSharpNames.Method(source.ObjectSetter) + "(" + target +
            ", new " + source.Constructor.ContainingType.CSharpName() + "(" + string.Join(", ", arguments) + "));");
    }

    public void Emit(BoundObject value, string frame)
    {
        var syntax = context.Document.Syntax;
        var span = Clamp(value.Syntax.Span, syntax.Text.Length);
        var fingerprint = CSharpNames.StableId(value.Type.CSharpName() + "\0" + syntax.Text.Substring(span.Start, span.Length));
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in value.Assignments)
        {
            var member = assignment switch
            {
                BoundSetAssignment set => set.Member.Name,
                BoundAdaptedSetAssignment set => set.Member.Name,
                BoundDynamicSetAssignment set => set.Target.Name,
                _ => null
            };
            if (member == null) continue;
            var source = Clamp(assignment.Span, syntax.Text.Length);
            var digest = CSharpNames.StableId(syntax.Text.Substring(source.Start, source.Length));
            declarations[member] = declarations.TryGetValue(member, out var previous) ? CSharpNames.StableId(previous + digest) : digest;
        }
        var record = new System.Text.StringBuilder();
        void Number(int number) => record.Append(number.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':');
        void Text(string? text) { Number(text?.Length ?? -1); record.Append(text); }
        Number(span.Start); Number(span.Length);
        Text(value.Name == null ? null : value.Key); Text(fingerprint); Number(declarations.Count);
        foreach (var pair in declarations.OrderBy(p => p.Key, StringComparer.Ordinal)) { Text(pair.Key); Text(pair.Value); }
        context.Writer.Line(frame + ".Session.RegisterSource(" + CSharpNames.Literal(value.Key) + ", " + context.SourceInfo(record.ToString()) + ");");
    }
    private static TextSpan Clamp(TextSpan span, int length)
    {
        var start = Math.Min(span.Start, length);
        return new(start, Math.Min(span.Length, length - start));
    }
}
