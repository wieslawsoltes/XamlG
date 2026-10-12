using System.Text;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Linear formatting of consecutive cast occurrences. Never removes a
/// conversion or memoizes runtime evaluation; only the intermediate strings go away.</summary>
internal static class CastChainEmitter
{
    public static string Emit(BoundCastExpression first, string frame, ValueEmitter emitter, EmissionContext context)
    {
        var text = new StringBuilder();
        BoundExpression value = first;
        var count = 0;
        while (value is BoundCastExpression cast)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            // Format outer types before lowering the terminal expression, exactly
            // as recursive concatenation did. All casts remain in the final text.
            text.Append("((").Append(cast.TargetType.CSharpName()).Append(")(");
            count++;
            value = cast.Value;
        }
        text.Append(emitter.Emit(value, frame));
        // Two appends avoid multiplying the count. The terminal is lowered once,
        // and every syntactic cast receives precisely its two closing parentheses.
        text.Append(')', count).Append(')', count);
        return text.ToString();
    }
}
