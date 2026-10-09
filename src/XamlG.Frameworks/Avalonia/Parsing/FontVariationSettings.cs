using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace XamlG.Frameworks.Avalonia.Parsing;

internal readonly record struct FontVariation(OpenTypeTag Tag, double Value);

internal sealed class FontVariationSettings
{
    private readonly ImmutableArray<FontVariation> _variations;
    public ImmutableArray<FontVariation> Variations => _variations;
    private FontVariationSettings(ImmutableArray<FontVariation> values) { _variations = values; }
    public static FontVariationSettings Empty { get; } = new(ImmutableArray<FontVariation>.Empty);
    public FontVariationSettings(IEnumerable<FontVariation> variations)
    {
        if (variations is null)
        {
            throw new ArgumentNullException(nameof(variations));
        }

        var builder = ImmutableArray.CreateBuilder<FontVariation>();

        foreach (var variation in variations)
        {
            // Infinite values are usable — they clamp to the axis range like any other
            // out-of-range value when the settings are applied. NaN has no such meaning.
            if (double.IsNaN(variation.Value))
            {
                throw new ArgumentException(
                    $"Value for axis '{variation.Tag}' must not be NaN.",
                    nameof(variations));
            }

            // Last-wins for duplicate tags, matching CSS font-variation-settings.
            var replaced = false;

            for (var i = 0; i < builder.Count; i++)
            {
                if (builder[i].Tag == variation.Tag)
                {
                    builder[i] = variation;
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                builder.Add(variation);
            }
        }

        builder.Sort(static (a, b) => ((uint)a.Tag).CompareTo((uint)b.Tag));

        _variations = builder.ToImmutable();

    }
    public static FontVariationSettings Parse(string s)
    {
        if (s is null)
        {
            throw new ArgumentNullException(nameof(s));
        }

        if (string.IsNullOrWhiteSpace(s))
        {
            return Empty;
        }

        var variations = new List<FontVariation>();

        foreach (var part in s.Split(','))
        {
            var pair = part.AsSpan().Trim();

            if (pair.IsEmpty)
            {
                continue;
            }

            var separator = pair.IndexOf('=');

            if (separator <= 0 || separator == pair.Length - 1)
            {
                throw new FormatException(
                    $"Invalid font variation '{pair.ToString()}': expected tag=value.");
            }

            var tagText = pair.Slice(0, separator).Trim();
            var valueText = pair.Slice(separator + 1).Trim();

            if (tagText.IsEmpty || tagText.Length > 4)
            {
                throw new FormatException(
                    $"Invalid font variation axis tag '{tagText.ToString()}'.");
            }

            // The value text is already trimmed, so no whitespace styles — NumberStyles.Float
            // would silently re-allow leading/trailing whitespace inside the number itself.
            const NumberStyles valueStyles =
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

            if (!SpanHelpers.TryParseDouble(valueText, valueStyles, CultureInfo.InvariantCulture, out var value) ||
                double.IsNaN(value))
            {
                throw new FormatException(
                    $"Invalid font variation value '{valueText.ToString()}' for axis '{tagText.ToString()}'.");
            }

            variations.Add(new FontVariation(OpenTypeTag.Parse(tagText.ToString()), value));
        }

        return variations.Count == 0 ? Empty : new FontVariationSettings(variations);
    }
}
