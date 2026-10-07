using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

/// <summary>Parses the pinned Avalonia container-query grammar without loading framework code.</summary>
public static class AvaloniaContainerQueryParser
{
    public static ContainerQuerySyntax? Parse(string text, TextSpan span, Action<XamlDiagnostic> report,
        CancellationToken cancellationToken = default)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        var steps = ImmutableArray.CreateBuilder<ContainerQueryStepSyntax>();
        var position = 0;
        var feature = true;
        while (position < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            var start = position;
            if (feature)
            {
                if (position == text.Length) break;
                while (position < text.Length && (char.IsLetter(text[position]) || text[position] == '-')) position++;
                var name = text.Substring(start, position - start);
                if (name is not ("width" or "height" or "min-width" or "max-width" or "min-height" or "max-height"))
                    return Error("Expected width, height, min-width, max-width, min-height or max-height.", start);
                if (position == text.Length || text[position++] != ':') return Error("Expected ':' after the query feature.", position);
                while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
                var numberStart = position;
                while (position < text.Length && char.IsDigit(text[position])) position++;
                if (position == numberStart || !double.TryParse(text.Substring(numberStart, position - numberStart), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    return Error("Expected a nonnegative integer query value.", numberStart);
                // Avalonia 12.1.3 lowers bare 'height' as max-height. Keep the pinned
                // compiler's observable behavior; the differential suite guards this.
                var comparison = name == "width" ? "Equals" : name.StartsWith("min-", StringComparison.Ordinal) ? "GreaterThanOrEquals" : "LessThanOrEquals";
                steps.Add(new(name.EndsWith("width", StringComparison.Ordinal) ? ContainerQueryStepKind.Width : ContainerQueryStepKind.Height,
                    comparison, number, new(span.Start + start, position - start)));
                feature = false;
            }
            else if (position < text.Length && text[position] == ',')
            {
                position++;
                steps.Add(new(ContainerQueryStepKind.Or, string.Empty, 0, new(span.Start + start, 1)));
                feature = true;
            }
            else
            {
                while (position < text.Length && !char.IsWhiteSpace(text[position])) position++;
                if (text.Substring(start, position - start) != "and") return Error("Expected 'and' or ',' between query features.", start);
                steps.Add(new(ContainerQueryStepKind.And, string.Empty, 0, new(span.Start + start, position - start)));
                feature = true;
            }
        }
        return new(steps.ToImmutable(), span);

        ContainerQuerySyntax? Error(string message, int offset)
        {
            offset = Math.Min(offset, text.Length);
            report(new("XG3111", message, new(span.Start + offset, Math.Min(1, text.Length - offset))));
            return null;
        }
    }
}
