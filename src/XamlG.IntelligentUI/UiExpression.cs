using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XamlG.IntelligentUI;

/// <summary>Deterministic C# expression AST evaluation over JSON. Pure functions and collection
/// lambdas share one operation/allocation budget. No reflection, dynamic dispatch, or user assembly loading.</summary>
public sealed partial class UiExpression
{
    private readonly ExpressionSyntax _syntax;
    private readonly UiLimits _limits;
    public string Source { get; }
    private UiExpression(string source, ExpressionSyntax syntax, UiLimits limits) { Source = source; _syntax = syntax; _limits = limits; }
    public static UiExpression Parse(string source, UiLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= new(); limits.Validate();
        if (source.Length > limits.ExpressionCharacters) throw Error("expression_limit", "C# expression is too long.");
        var syntax = SyntaxFactory.ParseExpression(source);
        if (syntax.ContainsDiagnostics || syntax.FullSpan.Length != source.Length || syntax.DescendantNodesAndSelf().Count() > limits.ExpressionNodes)
            throw Error("invalid_expression", "Invalid or oversized C# expression.");
        Validate(syntax, new HashSet<string>(StringComparer.Ordinal) { "state", "data", "item" }, 0);
        return new(source, syntax, limits);
    }
    private static void Validate(ExpressionSyntax node, HashSet<string> names, int depth)
    {
        if (depth > 32) throw Error("expression_limit", "C# expression is too deep.");
        void V(ExpressionSyntax child) => Validate(child, names, depth + 1);
        switch (node)
        {
            case LiteralExpressionSyntax literal when literal.Kind() is SyntaxKind.StringLiteralExpression or SyntaxKind.CharacterLiteralExpression or SyntaxKind.NumericLiteralExpression or SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression or SyntaxKind.NullLiteralExpression: return;
            case IdentifierNameSyntax identifier when names.Contains(identifier.Identifier.ValueText): return;
            case ParenthesizedExpressionSyntax parentheses: V(parentheses.Expression); return;
            case MemberAccessExpressionSyntax member when member.IsKind(SyntaxKind.SimpleMemberAccessExpression) && member.Name is IdentifierNameSyntax:
                if (member.Expression.ToString() == "Math" && member.Name.Identifier.ValueText is "PI" or "E") return;
                V(member.Expression); return;
            case ElementAccessExpressionSyntax index when index.ArgumentList.Arguments.Count == 1:
                V(index.Expression); ValidateArgument(index.ArgumentList.Arguments[0]); V(index.ArgumentList.Arguments[0].Expression); return;
            case ConditionalAccessExpressionSyntax access:
                V(access.Expression); ValidateConditional(access.WhenNotNull, names, depth + 1); return;
            case PrefixUnaryExpressionSyntax unary when unary.Kind() is SyntaxKind.UnaryMinusExpression or SyntaxKind.UnaryPlusExpression or SyntaxKind.LogicalNotExpression:
                V(unary.Operand); return;
            case BinaryExpressionSyntax binary when binary.Kind() is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression or SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression:
                V(binary.Left); V(binary.Right); return;
            case ConditionalExpressionSyntax conditional: V(conditional.Condition); V(conditional.WhenTrue); V(conditional.WhenFalse); return;
            case CastExpressionSyntax cast when cast.Type is PredefinedTypeSyntax type && type.Keyword.ValueText is "int" or "long" or "decimal" or "double" or "float" or "string" or "bool": V(cast.Expression); return;
            case InterpolatedStringExpressionSyntax interpolation:
                foreach (var part in interpolation.Contents.OfType<InterpolationSyntax>())
                {
                    V(part.Expression);
                    if (part.AlignmentClause != null) V(part.AlignmentClause.Value);
                    if (part.FormatClause != null) ValidateFormat(part.FormatClause.FormatStringToken.ValueText);
                }
                return;
            case ImplicitArrayCreationExpressionSyntax array: foreach (var item in array.Initializer.Expressions) V(item); return;
            case CollectionExpressionSyntax collection:
                foreach (var item in collection.Elements)
                    if (item is ExpressionElementSyntax expression) V(expression.Expression);
                    else if (item is SpreadElementSyntax spread) V(spread.Expression);
                    else throw Error("invalid_expression", "Unsupported collection element.");
                return;
            case AnonymousObjectCreationExpressionSyntax anonymous:
                foreach (var item in anonymous.Initializers) { _ = AnonymousName(item); V(item.Expression); }
                return;
            case InvocationExpressionSyntax call when call.Expression is MemberAccessExpressionSyntax method && method.Name is IdentifierNameSyntax:
                var name = method.Name.Identifier.ValueText;
                var typeName = method.Expression.ToString();
                if (IsStaticType(typeName))
                {
                    if (!IsStaticFunction(typeName, name)) throw Error("invalid_expression", "Unregistered pure function: " + typeName + "." + name);
                }
                else
                {
                    if (!InstanceFunctions.Contains(name)) throw Error("invalid_expression", "Unregistered pure function: " + name);
                    V(method.Expression);
                }
                foreach (var argument in call.ArgumentList.Arguments)
                {
                    ValidateArgument(argument);
                    if (argument.Expression is LambdaExpressionSyntax lambda)
                    {
                        if (!QueryFunctions.Contains(name) || IsStaticType(typeName)) throw Error("invalid_expression", "Lambdas are only accepted by collection functions.");
                        var parameters = Parameters(lambda);
                        if (parameters.Length is < 1 or > 2 || parameters.Distinct(StringComparer.Ordinal).Count() != parameters.Length || parameters.Any(names.Contains))
                            throw Error("invalid_expression", "Use distinct, unshadowed lambda parameter names.");
                        var nested = new HashSet<string>(names, StringComparer.Ordinal); nested.UnionWith(parameters);
                        Validate(LambdaBody(lambda), nested, depth + 1);
                    }
                    else V(argument.Expression);
                }
                return;
            default: throw Error("invalid_expression", "C# syntax is not permitted in a pure UI expression: " + node.Kind());
        }
    }
    private static void ValidateConditional(ExpressionSyntax node, HashSet<string> names, int depth)
    {
        switch (node)
        {
            case MemberBindingExpressionSyntax member when member.Name is IdentifierNameSyntax: return;
            case ElementBindingExpressionSyntax index when index.ArgumentList.Arguments.Count == 1:
                ValidateArgument(index.ArgumentList.Arguments[0]); Validate(index.ArgumentList.Arguments[0].Expression, names, depth + 1); return;
            case MemberAccessExpressionSyntax member: ValidateConditional(member.Expression, names, depth + 1); return;
            case ConditionalAccessExpressionSyntax nested: ValidateConditional(nested.Expression, names, depth + 1); ValidateConditional(nested.WhenNotNull, names, depth + 1); return;
            default: throw Error("invalid_expression", "Null-conditional access supports JSON members and indices.");
        }
    }
    private static void ValidateArgument(ArgumentSyntax argument)
    { if (argument.NameColon != null || argument.RefKindKeyword.RawKind != 0) throw Error("invalid_expression", "Use positional value arguments."); }
    private static string[] Parameters(LambdaExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple when simple.Parameter.Type == null && simple.Parameter.Modifiers.Count == 0 => [simple.Parameter.Identifier.ValueText],
        ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.All(p => p.Type == null && p.Modifiers.Count == 0) => parenthesized.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToArray(),
        _ => throw Error("invalid_expression", "Use inferred value lambda parameters.")
    };
    private static ExpressionSyntax LambdaBody(LambdaExpressionSyntax lambda) => lambda.Body as ExpressionSyntax ?? throw Error("invalid_expression", "Collection lambdas must be expressions, not statement blocks.");
    private static string AnonymousName(AnonymousObjectMemberDeclaratorSyntax item) => item.NameEquals?.Name.Identifier.ValueText ?? item.Expression switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => throw Error("invalid_expression", "Give the projected object member a name.")
    };
    public JsonElement Evaluate(JsonElement state, JsonElement data, JsonElement? item = null)
    {
        try
        {
            var frame = new Frame(new Dictionary<string, object?>(StringComparer.Ordinal) { ["state"] = state, ["data"] = data, ["item"] = item is { } value ? UiJson.Value(value) : null }, new Budget(_limits));
            var result = UiJson.Element(Eval(_syntax, frame));
            if (Encoding.UTF8.GetByteCount(result.GetRawText()) > _limits.DataBytes) throw Error("expression_limit", "Expression result exceeds the data budget.");
            return result;
        }
        catch (Exception error) when (error is OverflowException or DivideByZeroException or InvalidOperationException or FormatException or ArgumentOutOfRangeException or InvalidCastException)
        { throw Error("evaluation_failed", error.Message); }
    }
    private object? Eval(ExpressionSyntax node, Frame frame)
    {
        frame.Budget.Tick();
        object? E(ExpressionSyntax child) => Eval(child, frame);
        object? result = node switch
        {
            LiteralExpressionSyntax literal => literal.Token.Value switch { char character => character.ToString(), string or bool or null => literal.Token.Value, _ => Convert.ToDecimal(literal.Token.Value, CultureInfo.InvariantCulture) },
            IdentifierNameSyntax identifier => frame.Values[identifier.Identifier.ValueText],
            ParenthesizedExpressionSyntax parentheses => E(parentheses.Expression),
            MemberAccessExpressionSyntax member when member.Expression.ToString() == "Math" => member.Name.Identifier.ValueText == "PI" ? (decimal)Math.PI : (decimal)Math.E,
            MemberAccessExpressionSyntax member => Member(E(member.Expression), member.Name.Identifier.ValueText),
            ElementAccessExpressionSyntax access => Member(E(access.Expression), E(access.ArgumentList.Arguments[0].Expression)),
            ConditionalAccessExpressionSyntax access => Conditional(E(access.Expression), access.WhenNotNull, frame),
            PrefixUnaryExpressionSyntax unary => unary.Kind() switch { SyntaxKind.LogicalNotExpression => !Bool(E(unary.Operand)), SyntaxKind.UnaryMinusExpression => -Number(E(unary.Operand)), _ => Number(E(unary.Operand)) },
            ConditionalExpressionSyntax conditional => Bool(E(conditional.Condition)) ? E(conditional.WhenTrue) : E(conditional.WhenFalse),
            BinaryExpressionSyntax binary => Binary(binary, E),
            CastExpressionSyntax cast => Cast(((PredefinedTypeSyntax)cast.Type).Keyword.ValueText, E(cast.Expression)),
            InterpolatedStringExpressionSyntax interpolation => Interpolate(interpolation, E, frame.Budget),
            InvocationExpressionSyntax call => Invoke(call, frame),
            ImplicitArrayCreationExpressionSyntax array => Array(array.Initializer.Expressions.Select(E), frame.Budget),
            CollectionExpressionSyntax collection => Collection(collection, frame),
            AnonymousObjectCreationExpressionSyntax anonymous => Project(anonymous, frame),
            _ => throw Error("invalid_expression", "Unsupported C# expression.")
        };
        if (result is string text) frame.Budget.Text(text.Length);
        return result;
    }
    private object? Conditional(object? target, ExpressionSyntax node, Frame frame)
    {
        if (target == null || target is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }) return null;
        return node switch
        {
            MemberBindingExpressionSyntax member => Member(target, member.Name.Identifier.ValueText),
            ElementBindingExpressionSyntax index => Member(target, Eval(index.ArgumentList.Arguments[0].Expression, frame)),
            MemberAccessExpressionSyntax member => Member(Conditional(target, member.Expression, frame), member.Name.Identifier.ValueText),
            ConditionalAccessExpressionSyntax nested => Conditional(Conditional(target, nested.Expression, frame), nested.WhenNotNull, frame),
            _ => throw Error("invalid_expression", "Unsupported null-conditional access.")
        };
    }
    private JsonElement Collection(CollectionExpressionSyntax syntax, Frame frame)
    {
        var values = new List<object?>();
        foreach (var element in syntax.Elements)
        {
            if (element is ExpressionElementSyntax expression) values.Add(Eval(expression.Expression, frame));
            else if (element is SpreadElementSyntax spread) values.AddRange(Sequence(Eval(spread.Expression, frame), frame.Budget));
            frame.Budget.Count(values.Count);
        }
        return Array(values, frame.Budget);
    }
    private JsonElement Project(AnonymousObjectCreationExpressionSyntax syntax, Frame frame)
    {
        frame.Budget.Count(syntax.Initializers.Count);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in syntax.Initializers)
            if (!result.TryAdd(AnonymousName(item), Eval(item.Expression, frame))) throw Error("invalid_expression", "Duplicate projected object member.");
        return UiJson.Element(result);
    }
    private static object? Member(object? target, object? key)
    {
        if (target is string text)
        {
            if (key is "Length") return (decimal)text.Length;
            if (key is decimal index && index == decimal.Truncate(index) && index >= 0 && index < text.Length) return text[(int)index].ToString();
        }
        if (target is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Object && key is string name) return json.TryGetProperty(name, out var value) ? UiJson.Value(value) : null;
            if (json.ValueKind == JsonValueKind.Array)
            {
                if (key is "Length" or "Count") return (decimal)json.GetArrayLength();
                if (key is decimal index && index == decimal.Truncate(index) && index >= 0 && index < json.GetArrayLength()) return UiJson.Value(json[(int)index]);
            }
        }
        throw Error("evaluation_failed", "Member/index access requires a matching JSON value or string.");
    }
    private static object? Cast(string type, object? value) => type switch
    {
        "string" when value is string or null => value, "bool" => Bool(value),
        "int" => (decimal)checked((int)Number(value)), "long" => (decimal)checked((long)Number(value)),
        "double" or "float" or "decimal" => Number(value), _ => throw Error("evaluation_failed", "Invalid scalar cast.")
    };
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
            SyntaxKind.EqualsExpression => Equal(left, right), SyntaxKind.NotEqualsExpression => !Equal(left, right),
            SyntaxKind.LessThanExpression => Compare(left, right) < 0, SyntaxKind.LessThanOrEqualExpression => Compare(left, right) <= 0,
            SyntaxKind.GreaterThanExpression => Compare(left, right) > 0, SyntaxKind.GreaterThanOrEqualExpression => Compare(left, right) >= 0,
            _ => throw Error("invalid_expression", "Unsupported operator.")
        };
    }
    private static bool Equal(object? left, object? right) => left is JsonElement a && right is JsonElement b ? JsonElement.DeepEquals(a, b) : Equals(left, right);
    private static int Compare(object? left, object? right) => (left, right) switch
    {
        (null, null) => 0, (null, _) => -1, (_, null) => 1, (decimal a, decimal b) => a.CompareTo(b),
        (string a, string b) => string.CompareOrdinal(a, b), (bool a, bool b) => a.CompareTo(b),
        _ => throw Error("evaluation_failed", "Compare values of the same scalar type.")
    };
    private static string Interpolate(InterpolatedStringExpressionSyntax syntax, Func<ExpressionSyntax, object?> evaluate, Budget budget)
    {
        var result = new StringBuilder();
        foreach (var part in syntax.Contents)
        {
            string text;
            if (part is InterpolatedStringTextSyntax literal) text = literal.TextToken.ValueText;
            else
            {
                var hole = (InterpolationSyntax)part;
                text = Format(evaluate(hole.Expression), hole.FormatClause?.FormatStringToken.ValueText);
                if (hole.AlignmentClause != null)
                {
                    var width = Integer(evaluate(hole.AlignmentClause.Value));
                    if (width is < -256 or > 256) throw Error("expression_limit", "Interpolation alignment is limited to 256 columns.");
                    text = width < 0 ? text.PadRight(-width) : text.PadLeft(width);
                }
            }
            budget.Text(result.Length + text.Length); result.Append(text);
        }
        return result.ToString();
    }
    private static void ValidateFormat(string format)
    {
        if (format.Length > 64) throw Error("expression_limit", "Format string is too long.");
        // .NET standard format precision can request gigabytes of output. Bound it before formatting.
        if (format.Length > 1 && char.IsAsciiLetter(format[0]) && format.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 &&
            (!int.TryParse(format.AsSpan(1), out var precision) || precision > 16)) throw Error("expression_limit", "Format precision is limited to 16.");
    }
    private static string Format(object? value, string? format)
    {
        if (format == null) return UiJson.Text(value);
        ValidateFormat(format);
        return value is IFormattable formattable ? formattable.ToString(format, CultureInfo.InvariantCulture) : UiJson.Text(value);
    }
    internal static bool Bool(object? value) => value is bool flag ? flag : throw Error("evaluation_failed", "Boolean expression required.");
    private static decimal Number(object? value) => value is decimal number ? number : throw Error("evaluation_failed", "Numeric expression required.");
    private static int Integer(object? value)
    {
        var number = Number(value);
        return number == decimal.Truncate(number) ? checked((int)number) : throw Error("evaluation_failed", "Integer argument required.");
    }
    private static UiException Error(string code, string message) => new(code, message);
    private sealed record Frame(Dictionary<string, object?> Values, Budget Budget);
    private sealed class Budget(UiLimits limits)
    {
        private int _remaining = 131072;
        internal void Tick() { if (--_remaining < 0) throw Error("expression_limit", "Expression operation budget exhausted."); }
        internal void Count(int count) { if (count > limits.Nodes) throw Error("expression_limit", "Collection exceeds the configured item limit."); }
        internal void Text(int count) { if (count > limits.TextCharacters) throw Error("expression_limit", "Expression text exceeds the configured limit."); }
    }
}
