using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Runtime;
using XamlG.Syntax;

/// <summary>Identical-revision probe for composite lifetime
/// analysis and metadata-heavy emission. Setup and verification are not timed.</summary>
internal static class EmissionAnalysisProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        const string model = """
            using System.Collections.Generic;
            using XamlG.Runtime;
            namespace EmissionProbeModel {
                public class Node {
                    [Content] public List<Node> Children { get; } = new();
                    public string Text { get; set; } = "";
                    public object Payload { get; set; }
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("EmissionAnalysisProbe", [CSharpSyntaxTree.ParseText(model)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        const string open = "<Node xmlns='clr-namespace:EmissionProbeModel'>";
        var compiler = new XamlCompiler();
        var seed = compiler.Bind(XamlSyntaxTree.Parse(open + "</Node>", "Analysis.xaml"), compilation);
        Require(seed.Success && seed.Root != null, "Unable to bind analysis fixture");
        var root = seed.Root!;
        var property = (IPropertySymbol)root.Type.GetMembers("Payload").Single();
        var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type,
            property.GetMethod, property.SetMethod, default);
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);
        var arrayType = compilation.CreateArrayTypeSymbol(objectType);
        var signature = new StringBuilder();

        // This probe intentionally has no friend access. Compile bridges once
        // outside measurements so both revisions execute direct delegates rather
        // than reflection per operation.
        var (createContext, containsReference) = AnalysisBridges();
        foreach (var reference in new[] { false, true })
        {
            BoundExpression Leaf() => reference
                ? new BoundReferenceExpression("named", objectType, default)
                : new BoundConstantExpression(null, objectType, default);
            foreach (var depth in new[] { 10, 16 })
            {
                var expression = Leaf();
                for (var i = 0; i < depth; i++) expression = new BoundArrayExpression([expression, expression], arrayType, default);
                ProbeAnalysis("analyze-shared-dag-" + depth + (reference ? "-reference" : "-literal"), expression, reference, 2);
            }
            foreach (var depth in new[] { 8, 12 })
            {
                BoundExpression Tree(int level) => level == 0 ? Leaf() :
                    new BoundArrayExpression([Tree(level - 1), Tree(level - 1)], arrayType, default);
                ProbeAnalysis("analyze-unshared-tree-" + depth + (reference ? "-reference" : "-literal"), Tree(depth), reference, 2);
            }
        }

        foreach (var count in new[] { 100, 1000 })
        foreach (var properties in new[] { 0, 1 })
        {
            var child = properties == 0 ? "<Node/>" : "<Node Text='value'/>";
            var text = open + string.Concat(Enumerable.Repeat(child, count)) + "</Node>";
            var bound = compiler.Bind(XamlSyntaxTree.Parse(text, "Metadata.xaml"), compilation);
            Require(bound.Success, "Unable to bind metadata fixture");
            var output = new CSharpEmitter().Emit(bound);
            Require(output.Success, "Unable to emit metadata fixture");
            var generated = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source,
                new CSharpParseOptions(LanguageVersion.Preview)));
            using (var assembly = new MemoryStream())
            {
                var result = generated.Emit(assembly);
                Require(result.Success, "Generated metadata fixture does not compile: " +
                    string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            }
            var name = "emit-metadata-" + count + "-properties-" + properties;
            measure(name, count == 100 ? 4 : 1, () => new CSharpEmitter().Emit(bound).Source.Length);
            signature.Append(name).Append('\n').Append(output.Source).Append('\n')
                .Append(JsonSerializer.Serialize(output.SourceMappings)).Append('\n')
                .Append(JsonSerializer.Serialize(output.Diagnostics)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));

        void ProbeAnalysis(string name, BoundExpression expression, bool expectedReference, int operations)
        {
            var assignment = new BoundSetAssignment(member, expression, default);
            var document = seed with { Root = root with { Assignments = ImmutableArray.Create<BoundAssignment>(assignment) } };
            using (var context = createContext(document))
                Require(containsReference(context, assignment) == expectedReference, "Wrong lifetime result for " + name);
            // A fresh context for every operation measures discovery, not a warm
            // assignment cache. Context creation/disposal is intentionally included.
            measure(name, operations, () =>
            {
                using var context = createContext(document);
                return containsReference(context, assignment) ? 1 : 0;
            });
            signature.Append(name).Append('=').Append(expectedReference ? '1' : '0').Append('\n');
        }
    }

    private static (Func<BoundDocument, IDisposable> Create, Func<IDisposable, BoundAssignment, bool> Contains) AnalysisBridges()
    {
        var contextType = typeof(CSharpEmitter).Assembly.GetType("XamlG.CSharp.EmissionContext", throwOnError: true)!;
        var constructor = contextType.GetConstructor([typeof(BoundDocument), typeof(CancellationToken)])
            ?? throw new MissingMethodException(contextType.FullName, ".ctor");
        var locals = contextType.GetProperty("Locals", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(contextType.FullName, "Locals");
        var contains = locals.PropertyType.GetMethod("ContainsReference", [typeof(BoundAssignment)])
            ?? throw new MissingMethodException(locals.PropertyType.FullName, "ContainsReference");
        var document = Expression.Parameter(typeof(BoundDocument));
        var create = Expression.Lambda<Func<BoundDocument, IDisposable>>(Expression.Convert(
            Expression.New(constructor, document, Expression.Constant(CancellationToken.None)), typeof(IDisposable)), document).Compile();
        var context = Expression.Parameter(typeof(IDisposable));
        var assignment = Expression.Parameter(typeof(BoundAssignment));
        var call = Expression.Call(Expression.Property(Expression.Convert(context, contextType), locals), contains, assignment);
        return (create, Expression.Lambda<Func<IDisposable, BoundAssignment, bool>>(call, context, assignment).Compile());
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
