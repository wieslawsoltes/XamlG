using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Roslyn;
using XamlG.Runtime;
using XamlG.Syntax;

internal static class MemberBindingProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        var fields = Enumerable.Range(0, 1000).Select(i => "P" + i).ToArray();
        var model = "using System; using System.Collections.Generic; using XamlG.Runtime; namespace Probe { " +
            "public class Base { public string Value {get;set;} = \"\"; protected string Protected {get;set;} = \"\"; " +
            "public event Action Changed {add{} remove{}} } " +
            string.Concat(Enumerable.Range(1, 32).Select(i => "public class Deep" + i + " : " + (i == 1 ? "Base" : "Deep" + (i - 1)) + " {}")) +
            "public class Wide { " + string.Concat(fields.Select(name => "public int " + name + " {get;set;}")) + " } " +
            "public class Item { " + string.Concat(Enumerable.Range(0, 8).Select(i => "public string P" + i + " {get;set;} = \"\";")) + " } " +
            "public class Panel { [Content] public List<Item> Children {get;} = new(); } }";
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("MemberBindingProbe", new[] { CSharpSyntaxTree.ParseText(model, parse) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Require(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Invalid probe model");
        var syntax = XamlSyntaxTree.Parse("<Base xmlns='clr-namespace:Probe'/>", "Lookup.xaml");
        var scope = NamespaceScope.Empty.Push(syntax.Root!);
        var types = new RoslynTypeSystem(compilation);
        BindingContext Context(RoslynTypeSystem symbols) => BindingContext.CreateSignatureProbe(syntax, symbols, XamlFrameworkProfile.Portable, new());
        var target = compilation.GetTypeByMetadataName("Probe.Base")!;
        var deep = compilation.GetTypeByMetadataName("Probe.Deep32")!;
        var wide = compilation.GetTypeByMetadataName("Probe.Wide")!;
        var names = new[] { "Value", "Changed", "Protected", "Missing" };
        var missing = Enumerable.Range(0, 1000).Select(i => "Missing" + i).ToArray();
        var warm = Context(types);
        int Lookup(BindingContext context, ITypeSymbol type, string[] values, int count)
        {
            context.Symbols.Clear();
            var found = 0;
            for (var i = 0; i < count; i++)
                if (context.Members.Resolve(type, values[i % values.Length], scope, new(1, 2), report: false) != null) found++;
            return found;
        }
        measure("member-lookup-warm-one", 10000, () => Lookup(warm, target, names, 1));
        measure("member-lookup-cold-one", 50, () => Lookup(Context(new RoslynTypeSystem(compilation)), target, names, 1));
        measure("member-lookup-warm-repeated-1000", 10, () => Lookup(warm, target, names, 1000));
        measure("member-lookup-cold-repeated-1000", 3, () => Lookup(Context(new RoslynTypeSystem(compilation)), target, names, 1000));
        measure("member-lookup-warm-unique-1000", 5, () => Lookup(warm, wide, fields, 1000));
        measure("member-lookup-cold-unique-1000", 3, () => Lookup(Context(new RoslynTypeSystem(compilation)), wide, fields, 1000));
        measure("member-lookup-warm-missing-1000", 10, () => Lookup(warm, target, missing, 1000));
        measure("member-lookup-cold-missing-1000", 3, () => Lookup(Context(new RoslynTypeSystem(compilation)), target, missing, 1000));
        measure("member-lookup-warm-deep-1000", 10, () => Lookup(warm, deep, names, 1000));
        var saturated = Context(new RoslynTypeSystem(compilation));
        for (var i = 0; i < 4600; i++) saturated.Members.Resolve(deep, "Saturated" + i, scope, default, report: false);
        measure("member-lookup-saturated-deep-missing-1000", 5, () => Lookup(saturated, deep, missing, 1000));

        var assignmentContext = Context(types);
        var owner = new ObjectBindingBuilder(wide, syntax.Root!, scope, "n0", true, 0);
        var members = fields.Select(name => assignmentContext.Members.Resolve(wide, name, scope, default)!).ToArray();
        var value = new BoundConstantExpression(1, compilation.GetSpecialType(SpecialType.System_Int32), default);
        measure("member-assignment-repeated-1000", 10, () =>
        {
            for (var i = 0; i < 1000; i++)
            {
                owner.AssignedScalars.Clear(); owner.Assignments.Clear();
                assignmentContext.Members.AddSet(owner, members[0], value, default);
            }
            return owner.Assignments.Count;
        });
        measure("member-assignment-unique-1000", 3, () =>
        {
            owner.AssignedScalars.Clear(); owner.Assignments.Clear();
            foreach (var member in members) assignmentContext.Members.AddSet(owner, member, value, default);
            return owner.Assignments.Count;
        });
        Require(owner.Assignments.Count == 1000 && assignmentContext.Diagnostics.Count == 0, "Invalid assignment behavior");

        var signatures = new StringBuilder();
        foreach (var type in new ITypeSymbol[] { target, deep, wide })
        {
            var context = Context(types);
            foreach (var name in names.Concat(fields.Take(3)))
            {
                var result = context.Members.Resolve(type, name, scope, new(4, 3));
                signatures.Append(JsonSerializer.Serialize(new { Target = type.ToDisplayString(), Name = name,
                    Symbol = result?.Symbol.ToDisplayString(), Type = result?.ValueType.ToDisplayString(), Kind = result?.Kind,
                    Span = result?.Span, Getter = result?.Getter?.ToDisplayString(), Setter = result?.Setter?.ToDisplayString() }));
            }
            signatures.Append(JsonSerializer.Serialize(context.Diagnostics));
            signatures.Append(JsonSerializer.Serialize(context.Symbols.Select(symbol => new { symbol.Span, Symbol = symbol.Symbol.ToDisplayString(), symbol.Role })));
        }
        foreach (var count in new[] { 32, 1024 })
        {
            var item = "<Item " + string.Join(" ", Enumerable.Range(0, 8).Select(i => "P" + i + "='value'")) + "/>";
            var tree = XamlSyntaxTree.Parse("<Panel xmlns='clr-namespace:Probe'>" + string.Concat(Enumerable.Repeat(item, count)) + "</Panel>", "Members.xaml");
            var bound = new XamlCompiler().Bind(tree, types, XamlFrameworkProfile.Portable);
            Require(bound.Success, "Binding fixture failed: " + string.Join("\n", bound.Diagnostics));
            var output = new CSharpEmitter().Emit(bound);
            Require(output.Success, "Emission fixture failed: " + string.Join("\n", output.Diagnostics));
            using var image = new MemoryStream();
            var emitted = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, parse, output.HintName)).Emit(image);
            Require(emitted.Success, "Generated fixture did not compile: " + string.Join("\n", emitted.Diagnostics));
            signatures.Append(output.Source).Append(JsonSerializer.Serialize(output.SourceMappings)).Append(JsonSerializer.Serialize(output.Diagnostics));
            measure("member-bind-warm-" + count + "x8", count == 32 ? 5 : 1, () => new XamlCompiler().Bind(tree, types, XamlFrameworkProfile.Portable).Symbols.Length);
            measure("member-bind-cold-" + count + "x8", count == 32 ? 5 : 1,
                () => new XamlCompiler().Bind(tree, new RoslynTypeSystem(compilation), XamlFrameworkProfile.Portable).Symbols.Length);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatures.ToString())));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
