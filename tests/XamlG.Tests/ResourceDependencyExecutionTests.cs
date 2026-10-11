using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceDependencyExecutionTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public void Compact_dependency_metadata_does_not_deduplicate_generated_factory_calls(bool sharedExpression, int concurrency)
    {
        const string model = """
            namespace DependencyExecution {
                public class Node { public static int Constructions; public Node() { Constructions++; } }
                public class Root { public Node A {get;set;} public Node B {get;set;} public Node C {get;set;} }
            }
            """;
        var compilation = CompilationFactory.Create(model);
        if (!compilation.References.OfType<PortableExecutableReference>().Any(r => r.FilePath == typeof(XamlRuntimeContext).Assembly.Location))
            compilation = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        var profile = XamlFrameworkProfile.Portable with
            { ObjectExpressionRules = ImmutableArray.Create<IXamlObjectExpressionRule>(new Include(sharedExpression)) };
        var caller = new XamlProjectDocument(XamlSyntaxTree.Parse("""
            <Root xmlns="clr-namespace:DependencyExecution">
              <Root.A><Include Source="Leaf.xaml"/></Root.A>
              <Root.B><Include Source="Leaf.xaml"/></Root.B>
              <Root.C><Include Source="Leaf.xaml"/></Root.C>
            </Root>
            """, "Root.xaml"), "Root.xaml");
        var leaf = new XamlProjectDocument(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:DependencyExecution'/>", "Leaf.xaml"), "Leaf.xaml");
        var project = new XamlProjectCompiler().Compile([caller, leaf], compilation, profile,
            new XamlCompilerOptions { MaxDegreeOfParallelism = concurrency });
        Assert.True(project.Success, string.Join("\n", project.Documents.SelectMany(d => d.Output.Diagnostics)));
        var root = project.Documents.Single(d => d.Input.LogicalPath == "Root.xaml");
        Assert.Single(ResourceReferenceCache.Get(root.Document, default));
        Assert.Equal(3, ResourceReferenceOracle.Scan(root.Document, default).Length);
        using var bytes = new MemoryStream();
        var emitted = XamlCSharpCompilation.AddGeneratedSources(compilation, project).Emit(bytes);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var load = new AssemblyLoadContext("ResourceDependency.Execution." + Guid.NewGuid(), isCollectible: true);
        load.Resolving += (_, name) => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
        var sessions = new List<XamlRuntimeSession>();
        try
        {
            bytes.Position = 0;
            var assembly = load.LoadFromStream(bytes);
            var build = assembly.GetType(root.Output.FactoryMetadataName)!.GetMethod(root.Output.BuildMethodName!)!;
            var values = new List<object>();
            for (var iteration = 0; iteration < 2; iteration++)
            {
                var instance = build.Invoke(null, new object?[] { null })!;
                Assert.True(XamlRuntimeSession.TryGet(instance, out var session));
                sessions.Add(session!);
                foreach (var name in new[] { "A", "B", "C" })
                    values.Add(instance.GetType().GetProperty(name)!.GetValue(instance)!);
            }
            Assert.Equal(6, assembly.GetType("DependencyExecution.Node")!.GetField("Constructions", BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
            Assert.All(values, value => Assert.NotNull(value));
            for (var i = 0; i < values.Count; i++)
                for (var j = i + 1; j < values.Count; j++) Assert.NotSame(values[i], values[j]);
        }
        finally
        {
            foreach (var session in sessions) session.Dispose();
            load.Unload();
        }
    }

    private sealed class Include(bool share) : IXamlObjectExpressionRule
    {
        private BoundResourceExpression? _shared;
        public bool TryBind(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType,
            NamespaceScope parentScope, int nameScope, out BoundExpression? expression)
        {
            expression = null;
            if (syntax.Name != "Include") return false;
            var source = syntax.Attributes.Single(attribute => attribute.Name == "Source");
            var lookup = context.Options.Resources!.Resolve(context.Options.ResourceUri, source.Value);
            if (lookup.Success)
            {
                var resource = new BoundResourceExpression(lookup.Resource!, source.ValueSpan);
                // Exactly one caller document uses this test rule, even in the
                // parallel project run. Deliberate IR sharing must NOT share execution.
                expression = share ? _shared ??= resource : resource;
            }
            else context.Report("TESTRESOURCE", lookup.Error!, source.ValueSpan);
            return true;
        }
    }
}
