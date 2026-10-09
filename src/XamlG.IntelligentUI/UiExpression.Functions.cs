using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XamlG.IntelligentUI;

public sealed partial class UiExpression
{
    private static readonly HashSet<string> QueryFunctions = new(StringComparer.Ordinal)
    {
        "Where", "Select", "SelectMany", "Any", "All", "Count", "Sum", "Average", "Min", "Max", "OrderBy", "OrderByDescending",
        "First", "FirstOrDefault", "Last", "LastOrDefault", "Single", "SingleOrDefault", "GroupBy", "ToDictionary", "Aggregate"
    };
    private static readonly HashSet<string> InstanceFunctions = new(QueryFunctions.Concat(new[]
    {
        "Take", "Skip", "Distinct", "Reverse", "ToArray", "ToList", "Contains", "StartsWith", "EndsWith", "ToLowerInvariant", "ToUpperInvariant",
        "Trim", "TrimStart", "TrimEnd", "Substring", "Replace", "Split", "ToString", "IndexOf", "PadLeft", "PadRight"
    }), StringComparer.Ordinal);
    private static bool IsStaticType(string name) => name is "Math" or "decimal" or "Decimal" or "string" or "String" or "Enumerable";
    private static bool IsStaticFunction(string type, string name) => type switch
    {
        "Math" or "decimal" or "Decimal" => name is "Abs" or "Min" or "Max" or "Clamp" or "Round" or "Floor" or "Ceiling" or "Truncate" or "Sign" or "Pow" or "Sqrt",
        "string" or "String" => name is "IsNullOrEmpty" or "IsNullOrWhiteSpace" or "Concat" or "Join",
        "Enumerable" => name is "Range" or "Repeat" or "Empty",
        _ => false
    };
    private object? Invoke(InvocationExpressionSyntax call, Frame frame)
    {
        var method = (MemberAccessExpressionSyntax)call.Expression;
        var name = method.Name.Identifier.ValueText;
        var arguments = call.ArgumentList.Arguments.Select(a => a.Expression).ToArray();
        var type = method.Expression.ToString();
        if (IsStaticType(type)) return Static(type, name, arguments.Select(a => Eval(a, frame)).ToArray(), frame.Budget);
        var target = Eval(method.Expression, frame);
        if (target is JsonElement { ValueKind: JsonValueKind.Array }) return Query(target, name, arguments, frame);
        var values = arguments.Select(a => Eval(a, frame)).ToArray();
        if (name == "ToString") { Arity(values, 0, 1); return Format(target, values.Length == 0 ? null : TextArgument(values[0])); }
        if (target is not string text) throw Error("evaluation_failed", "This pure function requires a string or JSON array.");
        frame.Budget.Text(text.Length);
        switch (name)
        {
            case "Contains": case "StartsWith": case "EndsWith": case "IndexOf":
                Arity(values, 1); var needle = TextArgument(values[0]);
                return name switch { "Contains" => text.Contains(needle, StringComparison.Ordinal), "StartsWith" => text.StartsWith(needle, StringComparison.Ordinal), "EndsWith" => text.EndsWith(needle, StringComparison.Ordinal), _ => (object)(decimal)text.IndexOf(needle, StringComparison.Ordinal) };
            case "ToLowerInvariant": Arity(values, 0); return text.ToLowerInvariant();
            case "ToUpperInvariant": Arity(values, 0); return text.ToUpperInvariant();
            case "Trim": Arity(values, 0); return text.Trim();
            case "TrimStart": Arity(values, 0); return text.TrimStart();
            case "TrimEnd": Arity(values, 0); return text.TrimEnd();
            case "Substring": Arity(values, 1, 2); return values.Length == 1 ? text[Integer(values[0])..] : text.Substring(Integer(values[0]), Integer(values[1]));
            case "Replace":
                Arity(values, 2); var old = TextArgument(values[0]); var replacement = TextArgument(values[1]);
                if (old.Length == 0) throw Error("evaluation_failed", "Replace requires a nonempty old value.");
                var output = new StringBuilder(); var offset = 0;
                while (text.IndexOf(old, offset, StringComparison.Ordinal) is var index && index >= 0)
                {
                    frame.Budget.Tick(); frame.Budget.Text(output.Length + index - offset + replacement.Length);
                    output.Append(text.AsSpan(offset, index - offset)).Append(replacement); offset = index + old.Length;
                }
                frame.Budget.Text(output.Length + text.Length - offset); return output.Append(text.AsSpan(offset)).ToString();
            case "Split":
                Arity(values, 1); var separator = TextArgument(values[0]);
                if (separator.Length == 0) throw Error("evaluation_failed", "Split requires a nonempty separator.");
                return Array(text.Split(separator, StringSplitOptions.None).Cast<object?>(), frame.Budget);
            case "PadLeft": case "PadRight":
                Arity(values, 1); var width = Integer(values[0]); frame.Budget.Text(width);
                return name == "PadLeft" ? text.PadLeft(width) : text.PadRight(width);
            default: throw Error("evaluation_failed", "Function is not defined for this value: " + name);
        }
    }
    private static object? Static(string type, string name, object?[] values, Budget budget)
    {
        if (type is "Math" or "decimal" or "Decimal")
        {
            Arity(values, name is "Min" or "Max" or "Pow" ? 2 : name == "Clamp" ? 3 : 1, name == "Round" ? 2 : -1);
            var a = Number(values[0]);
            return name switch
            {
                "Abs" => decimal.Abs(a), "Min" => decimal.Min(a, Number(values[1])), "Max" => decimal.Max(a, Number(values[1])),
                "Clamp" => decimal.Clamp(a, Number(values[1]), Number(values[2])),
                "Round" => decimal.Round(a, values.Length == 1 ? 0 : Integer(values[1]), MidpointRounding.ToEven),
                "Floor" => decimal.Floor(a), "Ceiling" => decimal.Ceiling(a), "Truncate" => decimal.Truncate(a), "Sign" => (decimal)decimal.Sign(a),
                "Pow" => (decimal)Math.Pow((double)a, (double)Number(values[1])), "Sqrt" => (decimal)Math.Sqrt((double)a),
                _ => throw Error("invalid_expression", "Unknown math function.")
            };
        }
        if (type is "string" or "String")
        {
            if (name is "IsNullOrEmpty" or "IsNullOrWhiteSpace")
            {
                Arity(values, 1); var text = values[0] == null ? null : TextArgument(values[0]);
                return name == "IsNullOrEmpty" ? string.IsNullOrEmpty(text) : string.IsNullOrWhiteSpace(text);
            }
            if (name == "Join") { Arity(values, 2); return Join(Sequence(values[1], budget), TextArgument(values[0]), budget); }
            return Join(values.Length == 1 && values[0] is JsonElement { ValueKind: JsonValueKind.Array } ? Sequence(values[0], budget) : values, "", budget);
        }
        if (name == "Empty") { Arity(values, 0); return Array([], budget); }
        Arity(values, 2); var count = Integer(values[1]);
        if (count < 0) throw Error("evaluation_failed", "Sequence count cannot be negative.");
        budget.Count(count);
        if (name == "Repeat") return Array(Enumerable.Repeat(values[0], count), budget);
        var start = Integer(values[0]);
        if (count != 0) _ = checked(start + count - 1);
        return Array(Enumerable.Range(start, count).Select(x => (object?)(decimal)x), budget);
    }
    private object? Query(object? target, string name, ExpressionSyntax[] args, Frame frame)
    {
        var items = Sequence(target, frame.Budget);
        object? Apply(ExpressionSyntax argument, object? value, int index, object? accumulator = null, bool aggregate = false)
        {
            if (argument is not LambdaExpressionSyntax lambda) throw Error("evaluation_failed", name + " requires an expression lambda.");
            var names = Parameters(lambda);
            var locals = new Dictionary<string, object?>(frame.Values, StringComparer.Ordinal);
            if (aggregate)
            {
                if (names.Length != 2) throw Error("evaluation_failed", "Aggregate requires (accumulator, item). ");
                locals[names[0]] = accumulator; locals[names[1]] = value;
            }
            else { locals[names[0]] = value; if (names.Length == 2) locals[names[1]] = (decimal)index; }
            return Eval(LambdaBody(lambda), new(locals, frame.Budget));
        }
        object?[] Projected()
        {
            Arity(args, 0, 1);
            return args.Length == 0 ? items : items.Select((item, index) => Apply(args[0], item, index)).ToArray();
        }
        object?[] Filtered()
        {
            Arity(args, 0, 1);
            return args.Length == 0 ? items : items.Where((item, index) => Bool(Apply(args[0], item, index))).ToArray();
        }
        switch (name)
        {
            case "Where": Arity(args, 1); return Array(Filtered(), frame.Budget);
            case "Select": Arity(args, 1); return Array(Projected(), frame.Budget);
            case "SelectMany":
                Arity(args, 1); var flattened = new List<object?>();
                for (var i = 0; i < items.Length; i++) { flattened.AddRange(Sequence(Apply(args[0], items[i], i), frame.Budget)); frame.Budget.Count(flattened.Count); }
                return Array(flattened, frame.Budget);
            case "Any":
                Arity(args, 0, 1); if (args.Length == 0) return items.Length != 0;
                for (var i = 0; i < items.Length; i++) if (Bool(Apply(args[0], items[i], i))) return true;
                return false;
            case "All":
                Arity(args, 1); for (var i = 0; i < items.Length; i++) if (!Bool(Apply(args[0], items[i], i))) return false;
                return true;
            case "Count": return (decimal)Filtered().Length;
            case "Sum": case "Average":
                var projected = Projected(); decimal sum = 0;
                foreach (var value in projected) { frame.Budget.Tick(); sum = checked(sum + Number(value)); }
                return name == "Sum" ? sum : projected.Length == 0 ? throw Error("evaluation_failed", "Average of an empty sequence.") : sum / projected.Length;
            case "Min": case "Max":
                var candidates = Projected(); if (candidates.Length == 0) throw Error("evaluation_failed", "Empty sequence.");
                var extreme = candidates[0];
                foreach (var value in candidates.Skip(1)) { frame.Budget.Tick(); if (Compare(value, extreme) * (name == "Min" ? 1 : -1) < 0) extreme = value; }
                return extreme;
            case "Take": case "Skip":
                Arity(args, 1); var count = Math.Max(0, Integer(Eval(args[0], frame)));
                return Array(name == "Take" ? items.Take(count) : items.Skip(count), frame.Budget);
            case "ToArray": case "ToList": Arity(args, 0); return Array(items, frame.Budget);
            case "Reverse": Arity(args, 0); return Array(items.Reverse(), frame.Budget);
            case "Contains":
                Arity(args, 1); var needle = Eval(args[0], frame);
                foreach (var item in items) { frame.Budget.Tick(); if (Equal(item, needle)) return true; }
                return false;
            case "Distinct":
                Arity(args, 0); var distinct = new List<object?>();
                foreach (var item in items)
                {
                    var seen = false;
                    foreach (var existing in distinct) { frame.Budget.Tick(); if (Equal(existing, item)) { seen = true; break; } }
                    if (!seen) distinct.Add(item);
                }
                return Array(distinct, frame.Budget);
            case "OrderBy": case "OrderByDescending":
                Arity(args, 1); var decorated = items.Select((value, index) => (Value: value, Key: Apply(args[0], value, index), Index: index)).ToArray();
                System.Array.Sort(decorated, (a, b) => { frame.Budget.Tick(); var comparison = Compare(a.Key, b.Key); return comparison == 0 ? a.Index.CompareTo(b.Index) : name == "OrderBy" ? comparison : -comparison; });
                return Array(decorated.Select(x => x.Value), frame.Budget);
            case "First": case "FirstOrDefault": case "Last": case "LastOrDefault": case "Single": case "SingleOrDefault":
                var filtered = Filtered();
                if (name.StartsWith("Single", StringComparison.Ordinal) && filtered.Length > 1) throw Error("evaluation_failed", "Sequence contains more than one matching element.");
                if (filtered.Length == 0) return name.EndsWith("OrDefault", StringComparison.Ordinal) ? null : throw Error("evaluation_failed", "Sequence contains no matching elements.");
                return name.StartsWith("Last", StringComparison.Ordinal) ? filtered[^1] : filtered[0];
            case "GroupBy":
                Arity(args, 1); var groups = new List<(object? Key, List<object?> Items)>();
                for (var i = 0; i < items.Length; i++)
                {
                    var key = Apply(args[0], items[i], i); var found = -1;
                    for (var j = 0; j < groups.Count; j++) { frame.Budget.Tick(); if (Equal(groups[j].Key, key)) { found = j; break; } }
                    if (found < 0) groups.Add((key, [items[i]])); else groups[found].Items.Add(items[i]);
                }
                return Array(groups.Select(group => (object?)UiJson.Element(new { group.Key, Items = group.Items })), frame.Budget);
            case "ToDictionary":
                Arity(args, 1, 2); var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < items.Length; i++)
                {
                    var key = TextArgument(Apply(args[0], items[i], i));
                    if (!dictionary.TryAdd(key, args.Length == 1 ? items[i] : Apply(args[1], items[i], i))) throw Error("evaluation_failed", "Duplicate dictionary key.");
                }
                return UiJson.Element(dictionary);
            case "Aggregate":
                Arity(args, 2); var accumulator = Eval(args[0], frame);
                for (var i = 0; i < items.Length; i++) accumulator = Apply(args[1], items[i], i, accumulator, true);
                return accumulator;
            default: throw Error("evaluation_failed", "Function is not defined for arrays: " + name);
        }
    }
    private static object?[] Sequence(object? value, Budget budget)
    {
        if (value is not JsonElement { ValueKind: JsonValueKind.Array } array) throw Error("evaluation_failed", "A JSON array is required.");
        budget.Count(array.GetArrayLength());
        return array.EnumerateArray().Select(item => { budget.Tick(); return UiJson.Value(item); }).ToArray();
    }
    private static JsonElement Array(IEnumerable<object?> values, Budget budget)
    {
        var result = new List<object?>();
        foreach (var value in values) { budget.Tick(); budget.Count(result.Count + 1); result.Add(value); }
        return UiJson.Element(result);
    }
    private static string Join(IEnumerable<object?> values, string separator, Budget budget)
    {
        var result = new StringBuilder(); var first = true;
        foreach (var item in values)
        {
            budget.Tick(); var text = UiJson.Text(item);
            budget.Text(result.Length + text.Length + (first ? 0 : separator.Length));
            if (!first) result.Append(separator); result.Append(text); first = false;
        }
        return result.ToString();
    }
    private static string TextArgument(object? value) => value as string ?? throw Error("evaluation_failed", "A string argument is required.");
    private static void Arity<T>(T[] args, int minimum, int maximum = -1)
    { if (args.Length < minimum || args.Length > (maximum < 0 ? minimum : maximum)) throw Error("evaluation_failed", "Incorrect number of function arguments."); }
}
