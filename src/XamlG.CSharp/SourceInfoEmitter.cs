using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp;

internal sealed class SourceInfoEmitter(EmissionContext context)
{
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
        var map = "new global::System.Collections.Generic.Dictionary<string, string>(global::System.StringComparer.Ordinal) { " +
            string.Join(", ", declarations.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => "{ " + CSharpNames.Literal(p.Key) + ", " + CSharpNames.Literal(p.Value) + " }")) + " }";
        var identity = value.Name == null ? "null" : CSharpNames.Literal(value.Key);
        context.Writer.Line(frame + ".Session.RegisterSource(" + CSharpNames.Literal(value.Key) + ", new global::XamlG.Runtime.XamlSourceInfo(" +
            CSharpNames.Literal(syntax.Path) + ", " + span.Start + ", " + span.Length + ", " + identity + ", " + CSharpNames.Literal(fingerprint) + ", " + map +
            ", version: " + syntax.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L));");
    }
    private static TextSpan Clamp(TextSpan span, int length)
    {
        var start = Math.Min(span.Start, length);
        return new(start, Math.Min(span.Length, length - start));
    }
}
