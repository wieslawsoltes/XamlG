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
        // Zero/one distinct property is common for leaf objects. Keep the first
        // entry inline and promote only when a second distinct member appears.
        string? singleName = null, singleDigest = null;
        Dictionary<string, string>? declarations = null;
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
            if (declarations != null)
                declarations[member] = declarations.TryGetValue(member, out var previous) ? context.StableId(previous + digest) : digest;
            else if (singleName == null) { singleName = member; singleDigest = digest; }
            else if (singleName == member) singleDigest = context.StableId(singleDigest + digest);
            else declarations = new(StringComparer.Ordinal) { [singleName] = singleDigest!, [member] = digest };
        }
        var record = new System.Text.StringBuilder();
        void Number(int number) => MetadataRecordEncoding.AppendNumber(record, number);
        void Text(string? text) => MetadataRecordEncoding.AppendText(record, text);
        Number(span.Start); Number(span.Length);
        Text(value.Name == null ? null : value.Key); Text(fingerprint);
        Number(declarations?.Count ?? (singleName == null ? 0 : 1));
        if (declarations != null)
            foreach (var pair in declarations.OrderBy(p => p.Key, StringComparer.Ordinal)) { Text(pair.Key); Text(pair.Value); }
        else if (singleName != null) { Text(singleName); Text(singleDigest); }
        return context.SourceInfoIndex(record.ToString());
    }
    private static TextSpan Clamp(TextSpan span, int length)
    {
        var start = Math.Min(span.Start, length);
        return new(start, Math.Min(span.Length, length - start));
    }
}
