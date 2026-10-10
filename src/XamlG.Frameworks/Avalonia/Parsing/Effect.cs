using System;

namespace XamlG.Frameworks.Avalonia.Parsing;

internal static class Effect
{
    static Exception ParseError(string s) => throw new ArgumentException("Unable to parse effect: " + s);
    public static IEffect Parse(string s)
    {
        var span = s.AsSpan();
        var r = new TokenParser(span);
        if (r.TryConsume("blur"))
        {
            if (!r.TryConsume('(') || !r.TryParseDouble(out var radius) || !r.TryConsume(')') || !r.IsEofWithWhitespace())
                throw ParseError(s);
            return new ImmutableBlurEffect(radius);
        }

       
        if (r.TryConsume("drop-shadow"))
        {
            if (!r.TryConsume('(') || !r.TryParseDouble(out var offsetX)
                                   || !r.TryParseDouble(out var offsetY))
                throw ParseError(s);
            double blurRadius = 0;
            var color = Color.FromUInt32(0xff000000);
            if (!r.TryConsume(')'))
            {
                if (!r.TryParseDouble(out blurRadius) || blurRadius < 0)
                    throw ParseError(s);
                if (!r.TryConsume(')'))
                {
                    var endOfExpression = s.LastIndexOf(")", StringComparison.Ordinal);
                    if (endOfExpression == -1)
                        throw ParseError(s);

                    if (!new TokenParser(span.Slice(endOfExpression + 1)).IsEofWithWhitespace())
                        throw ParseError(s);

                    if (!Color.TryParse(span.Slice(r.Position, endOfExpression - r.Position).TrimEnd(), out color))
                        throw ParseError(s);
                    return new ImmutableDropShadowEffect(offsetX, offsetY, blurRadius, color, 1);
                }
            }
            if (!r.IsEofWithWhitespace())
                throw ParseError(s);
            return new ImmutableDropShadowEffect(offsetX, offsetY, blurRadius, color, 1);
        }

        throw ParseError(s);
    }

}

internal interface IEffect { }
internal sealed record ImmutableBlurEffect(double Radius) : IEffect;
internal sealed record ImmutableDropShadowEffect(double OffsetX, double OffsetY, double BlurRadius, Color Color, double Opacity) : IEffect;
