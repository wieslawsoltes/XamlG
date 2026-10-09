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
        if (context.Document.Runtime.SourceInfo == null) return;
        var position = context.Document.Syntax.Lines.GetPosition(location.Start);
        context.Writer.Line(context.SourceInfoSetter() + "(" + target + ", " +
            (position.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ", " +
            (position.Character + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ");");
    }

    public string Get(BoundObject value) => context.SourceInfoTable + "[" +
        (context.ConstructionParameters?.SourceIndex(value) ?? Index(value).ToString(System.Globalization.CultureInfo.InvariantCulture)) + "]";

    public int Index(BoundObject value)
    {
        var syntax = context.Document.Syntax;
        var span = Clamp(value.Syntax.Span, syntax.Text.Length);
        var fingerprint = context.StableId(value.Type.CSharpName(), syntax.Text, span);
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
            var digest = context.StableId(syntax.Text, source);
            declarations[member] = declarations.TryGetValue(member, out var previous) ? context.StableId(previous + digest) : digest;
        }
        var record = new System.Text.StringBuilder();
        void Number(int number) => record.Append(number.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':');
        void Text(string? text) { Number(text?.Length ?? -1); record.Append(text); }
        Number(span.Start); Number(span.Length);
        Text(value.Name == null ? null : value.Key); Text(fingerprint); Number(declarations.Count);
        foreach (var pair in declarations.OrderBy(p => p.Key, StringComparer.Ordinal)) { Text(pair.Key); Text(pair.Value); }
        return context.SourceInfoIndex(record.ToString());
    }
    private static TextSpan Clamp(TextSpan span, int length)
    {
        var start = Math.Min(span.Start, length);
        return new(start, Math.Min(span.Length, length - start));
    }
}
