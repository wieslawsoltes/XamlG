using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XamlG.IntelligentUI;

/// <summary>Interprets a deliberately bounded C# expression AST. Never compiles, reflects, loads assemblies, or executes model code.</summary>
public sealed class UiExpression
{
    private readonly ExpressionSyntax _syntax;
    private readonly int _textLimit;
    public string Source { get; }
    private UiExpression(string source, ExpressionSyntax syntax, int textLimit) { Source = source; _syntax = syntax; _textLimit = textLimit; }
    public static UiExpression Parse(string source, UiLimits? limits = null)
    {
        limits ??= new(); limits.Validate();
        if (source.Length > limits.ExpressionCharacters) throw new UiException("expression_limit", "C# expression is too long.");
        var syntax = SyntaxFactory.ParseExpression(source);
        if (syntax.ContainsDiagnostics || syntax.FullSpan.Length != source.Length || syntax.DescendantNodesAndSelf().Count() > limits.ExpressionNodes)
            throw new UiException("invalid_expression", "Invalid or oversized C# expression.");
        Validate(syntax, 0);
        return new(source, syntax, limits.TextCharacters);
    }
    private static void Validate(ExpressionSyntax node, int depth)
    {
        if (depth > 32) throw new UiException("expression_limit", "C# expression is too deep.");
        switch (node)
        {
            case LiteralExpressionSyntax literal when literal.Kind() is SyntaxKind.StringLiteralExpression or SyntaxKind.NumericLiteralExpression or SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression or SyntaxKind.NullLiteralExpression: return;
            case IdentifierNameSyntax identifier when identifier.Identifier.ValueText is "state" or "data" or "item": return;
            case ParenthesizedExpressionSyntax parentheses: Validate(parentheses.Expression, depth + 1); return;
            case MemberAccessExpressionSyntax member when member.IsKind(SyntaxKind.SimpleMemberAccessExpression): Validate(member.Expression, depth + 1); return;
            case ElementAccessExpressionSyntax index when index.ArgumentList.Arguments.Count == 1:
                Validate(index.Expression, depth + 1); Validate(index.ArgumentList.Arguments[0].Expression, depth + 1); return;
            case PrefixUnaryExpressionSyntax unary when unary.Kind() is SyntaxKind.UnaryMinusExpression or SyntaxKind.UnaryPlusExpression or SyntaxKind.LogicalNotExpression:
                Validate(unary.Operand, depth + 1); return;
            case BinaryExpressionSyntax binary when binary.Kind() is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression or SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression:
                Validate(binary.Left, depth + 1); Validate(binary.Right, depth + 1); return;
            case ConditionalExpressionSyntax conditional:
                Validate(conditional.Condition, depth + 1); Validate(conditional.WhenTrue, depth + 1); Validate(conditional.WhenFalse, depth + 1); return;
            case InterpolatedStringExpressionSyntax interpolation:
                foreach (var part in interpolation.Contents)
                    if (part is InterpolationSyntax hole)
                    {
                        if (hole.AlignmentClause != null || hole.FormatClause != null) throw new UiException("invalid_expression", "Interpolation alignment and format strings are not supported.");
                        Validate(hole.Expression, depth + 1);
                    }
                return;
            default: throw new UiException("invalid_expression", "C# syntax is not permitted: " + node.Kind());
        }
    }
    public JsonElement Evaluate(JsonElement state, JsonElement data, JsonElement? item = null)
    {
        try { return UiJson.Element(Eval(_syntax, state, data, item)); }
        catch (Exception error) when (error is OverflowException or DivideByZeroException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        { throw new UiException("evaluation_failed", error.Message); }
    }
    private object? Eval(ExpressionSyntax node, JsonElement state, JsonElement data, JsonElement? item)
    {
        object? E(ExpressionSyntax child) => Eval(child, state, data, item);
        object? result = node switch
        {
            LiteralExpressionSyntax literal => literal.Token.Value is string or bool or null ? literal.Token.Value : Convert.ToDecimal(literal.Token.Value, CultureInfo.InvariantCulture),
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText switch { "state" => state, "data" => data, "item" => item, _ => null },
            ParenthesizedExpressionSyntax parentheses => E(parentheses.Expression),
            MemberAccessExpressionSyntax member => Member(E(member.Expression), member.Name.Identifier.ValueText),
            ElementAccessExpressionSyntax access => Member(E(access.Expression), E(access.ArgumentList.Arguments[0].Expression)),
            PrefixUnaryExpressionSyntax unary => unary.Kind() switch { SyntaxKind.LogicalNotExpression => !Bool(E(unary.Operand)), SyntaxKind.UnaryMinusExpression => -Number(E(unary.Operand)), _ => Number(E(unary.Operand)) },
            ConditionalExpressionSyntax conditional => Bool(E(conditional.Condition)) ? E(conditional.WhenTrue) : E(conditional.WhenFalse),
            BinaryExpressionSyntax binary => Binary(binary, E),
            InterpolatedStringExpressionSyntax interpolation => Interpolate(interpolation, E),
            _ => throw new UiException("invalid_expression", "Unsupported C# expression.")
        };
        if (result is string text && text.Length > _textLimit) throw new UiException("expression_limit", "Expression text exceeds the configured limit.");
        return result;
    }
    private string Interpolate(InterpolatedStringExpressionSyntax syntax, Func<ExpressionSyntax, object?> evaluate)
    {
        var result = new System.Text.StringBuilder();
        foreach (var part in syntax.Contents)
        {
            result.Append(part is InterpolatedStringTextSyntax text ? text.TextToken.ValueText : UiJson.Text(evaluate(((InterpolationSyntax)part).Expression)));
            if (result.Length > _textLimit) throw new UiException("expression_limit", "Interpolation exceeds the text limit.");
        }
        return result.ToString();
    }
    private static object? Member(object? target, object? key)
    {
        if (target is not JsonElement json) throw new UiException("evaluation_failed", "Member access requires JSON data.");
        if (json.ValueKind == JsonValueKind.Object && key is string name)
            return json.TryGetProperty(name, out var value) ? UiJson.Value(value) : null;
        if (json.ValueKind == JsonValueKind.Array && key is decimal index && index == decimal.Truncate(index) && index >= 0 && index < json.GetArrayLength())
            return UiJson.Value(json[(int)index]);
        throw new UiException("evaluation_failed", "JSON index is invalid.");
    }
    private static object? Binary(BinaryExpressionSyntax syntax, Func<ExpressionSyntax, object?> evaluate)
    {
        var left = evaluate(syntax.Left);
        if (syntax.IsKind(SyntaxKind.LogicalAndExpression)) return Bool(left) && Bool(evaluate(syntax.Right));
        if (syntax.IsKind(SyntaxKind.LogicalOrExpression)) return Bool(left) || Bool(evaluate(syntax.Right));
        if (syntax.IsKind(SyntaxKind.CoalesceExpression)) return left ?? evaluate(syntax.Right);
        var right = evaluate(syntax.Right);
        return syntax.Kind() switch
        {
            SyntaxKind.AddExpression when left is string || right is string => UiJson.Text(left) + UiJson.Text(right),
            SyntaxKind.AddExpression => Number(left) + Number(right), SyntaxKind.SubtractExpression => Number(left) - Number(right),
            SyntaxKind.MultiplyExpression => Number(left) * Number(right), SyntaxKind.DivideExpression => Number(left) / Number(right), SyntaxKind.ModuloExpression => Number(left) % Number(right),
            SyntaxKind.EqualsExpression => Equals(left, right), SyntaxKind.NotEqualsExpression => !Equals(left, right),
            SyntaxKind.LessThanExpression => Number(left) < Number(right), SyntaxKind.LessThanOrEqualExpression => Number(left) <= Number(right),
            SyntaxKind.GreaterThanExpression => Number(left) > Number(right), SyntaxKind.GreaterThanOrEqualExpression => Number(left) >= Number(right),
            _ => throw new UiException("invalid_expression", "Unsupported operator.")
        };
    }
    internal static bool Bool(object? value) => value is bool flag ? flag : throw new UiException("evaluation_failed", "Boolean expression required.");
    private static decimal Number(object? value) => value is decimal number ? number : throw new UiException("evaluation_failed", "Numeric expression required.");
}
