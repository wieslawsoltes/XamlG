using System.Collections;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ConstructionFactoryTests
{
    private const string Model = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.Reflection;
        using XamlG.Runtime;
        namespace ConstructionCase {
          public class Root {
            public static readonly List<string> Log = new();
            public static string FailAt;
            public static int Next;
            public static Root Active;
            public static bool ExpectNamed;
            public Root() { Active = this; }
            public Bag Children { get; } = new();
            public static void Event(string value) {
              Log.Add(value);
              if (value == FailAt) throw new InvalidOperationException(value);
            }
          }
          public sealed class Bag : IEnumerable<Node> {
            private readonly List<Node> _items = new();
            public void Add(Node node) { Root.Event("add:" + node.Id); _items.Add(node); }
            public IEnumerator<Node> GetEnumerator() => _items.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
          }
          public class Node : ISupportInitialize {
            public int Id { get; } = ++Root.Next;
            public Node() {
              Root.Event("new:" + Id);
              var owner = new XamlRuntimeSession();
              owner.TrackCleanup(() => Root.Event("dispose:" + Id));
              owner.Attach(this);
            }
            public string Name { get; set; }
            private string _text;
            public string Text { get => _text; set { Root.Event("set:" + Id); _text = value; } }
            public void BeginInit() {
              if (Root.ExpectNamed && !ReferenceEquals(this, Root.Active.GetType()
                  .GetField("Named", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(Root.Active)))
                throw new InvalidOperationException("Named field was not installed before BeginInit");
              Root.Event("begin:" + Id);
            }
            public void EndInit() => Root.Event("end:" + Id);
          }
          [UsableDuringInitialization(true)] public class EarlyNode : Node { }
          public partial class View : Root { }
          public class SourceInfo {
            public SourceInfo(int line, int column, string path) { }
            public static void Set(object target, SourceInfo info) {
              if (target is Node node) Root.Event("source:" + node.Id);
            }
          }
        }
        public partial class GlobalView : ConstructionCase.Root { }
        """;

    private static CSharpCompilation Compilation() => CompilationFactory.Create(Model)
        .AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));

    private static XamlProjectDocument Document(string path = "First.xaml", string children = "<Node Text='first'/><Node Text='second'/>", string attributes = "") =>
        new(XamlSyntaxTree.Parse("<Root xmlns='clr-namespace:ConstructionCase' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " + attributes +
            "><Root.Children>" + children + "</Root.Children></Root>", path), path);

    private static Assembly Emit(CSharpCompilation compilation, XamlProjectCompilation project)
    {
        Assert.True(project.Success, string.Join("\n", project.Documents.SelectMany(document => document.Output.Diagnostics)));
        using var stream = new MemoryStream();
        var result = XamlCSharpCompilation.AddGeneratedSources(compilation, project).Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }

    private static object Build(Assembly assembly, XamlProjectCompilation project, string path = "First.xaml")
    {
        var output = project.Documents.Single(document => document.Input.LogicalPath == path).Output;
        return assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, new object?[] { null })!;
    }

    private static Type RootType(Assembly assembly) => assembly.GetType("ConstructionCase.Root")!;
    private static string Events(Assembly assembly) => string.Join(",", (IEnumerable<string>)RootType(assembly).GetField("Log")!.GetValue(null)!);
    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
    private static object[] Children(object root) => ((IEnumerable)Property(root, "Children")!).Cast<object>().ToArray();
    private static string Source(XamlProjectCompilation project) => string.Join("\n", project.Documents.Select(document => document.Output.Source));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void SharedConstructionPreservesOrderingEditingSourceLocationsAndOwnership(int concurrency)
    {
        var compilation = Compilation();
        var documents = new[] { Document(), Document("Second.xaml") };
        var project = new XamlProjectCompiler().Compile(documents, compilation, options: new() { MaxDegreeOfParallelism = concurrency });
        var assembly = Emit(compilation, project);
        Assert.Equal(1, Source(project).Split("out global::ConstructionCase.Node __value)", StringSplitOptions.None).Length - 1);
        var root = Build(assembly, project);
        Assert.Equal("new:1,begin:1,set:1,end:1,add:1,new:2,begin:2,set:2,end:2,add:2", Events(assembly));
        var children = Children(root);
        Assert.NotSame(children[0], children[1]);
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        var nodes = children.Select(child => session!.FindNode(child)!).ToArray();
        Assert.NotEqual(nodes[0].Key, nodes[1].Key);
        foreach (var node in nodes)
        {
            Assert.Equal(session!.FindNode(root)!.Key, node.ParentKey);
            Assert.Equal("First.xaml", node.Source!.Path);
        }
        Assert.NotEqual(nodes[0].Source!.Start, nodes[1].Source!.Start);
        Assert.True(session!.Apply(0, new[] { new XamlPropertyUpdate(nodes[0].Key, "Text", "edited") }).Applied);
        Assert.Equal("edited", Property(children[0], "Text"));
        Assert.Equal("second", Property(children[1], "Text"));
        session.Dispose(); session.Dispose();
        Assert.EndsWith("set:1,dispose:2,dispose:1", Events(assembly), StringComparison.Ordinal);
        var second = Build(assembly, project, "Second.xaml");
        Assert.Equal("first", Property(Children(second)[0], "Text"));
        Assert.True(XamlRuntimeSession.TryGet(second, out var secondSession));
        Assert.Equal("Second.xaml", secondSession!.FindNode(Children(second)[0])!.Source!.Path);
        secondSession.Dispose();
        foreach (var document in project.Documents)
            foreach (var literal in new[] { "first", "second" })
                Assert.Contains(document.Output.SourceMappings, mapping =>
                    document.Output.Source.Substring(mapping.GeneratedSpan.Start, mapping.GeneratedSpan.Length).Contains(literal, StringComparison.Ordinal) &&
                    document.Input.Syntax.Text.Substring(mapping.SourceSpan.Start, mapping.SourceSpan.Length).Contains(literal, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("new:2")]
    [InlineData("begin:2")]
    [InlineData("set:2")]
    [InlineData("end:2")]
    [InlineData("add:2")]
    public void FailuresRetainTheOriginalExceptionAndCleanupOrder(string phase)
    {
        var compilation = Compilation();
        var project = new XamlProjectCompiler().Compile(new[] { Document() }, compilation);
        var assembly = Emit(compilation, project);
        RootType(assembly).GetField("FailAt")!.SetValue(null, phase);
        var error = Assert.Throws<TargetInvocationException>(() => Build(assembly, project));
        Assert.Equal(phase, Assert.IsType<InvalidOperationException>(error.InnerException).Message);
        var order = "new:1,begin:1,set:1,end:1,add:1,new:2,begin:2,set:2,end:2,add:2";
        var prefix = order[..(order.IndexOf(phase, StringComparison.Ordinal) + phase.Length)];
        Assert.Equal(prefix + (phase == "new:2" ? ",dispose:1" : ",dispose:2,dispose:1"), Events(assembly));
    }

    [Fact]
    public void EarlyConsumptionRemainsBetweenBeginInitAndAssignments()
    {
        var compilation = Compilation();
        var project = new XamlProjectCompiler().Compile(new[] { Document(children: "<EarlyNode Text='first'/>") }, compilation);
        var assembly = Emit(compilation, project);
        var root = Build(assembly, project);
        Assert.Equal("new:1,begin:1,add:1,set:1,end:1", Events(assembly));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
    }

    [Theory]
    [InlineData("ConstructionCase.View", false)]
    [InlineData("GlobalView", false)]
    [InlineData("ConstructionCase.View", true)]
    [InlineData("GlobalView", true)]
    public void NamedFieldsAreInstalledBeforeBeginInit(string className, bool mappedName)
    {
        var compilation = Compilation();
        var profile = XamlFrameworkProfile.Portable with
        {
            NameDirectiveProperty = mappedName ? "Name" : null,
            ObjectBindingRules = mappedName ? [new RegisteredNameRule()] : []
        };
        var project = new XamlProjectCompiler().Compile(new[] { Document(children: "<Node x:Name='Named' Text='first'/>", attributes: "x:Class='" + className + "'") }, compilation, profile);
        var assembly = Emit(compilation, project);
        RootType(assembly).GetField("ExpectNamed")!.SetValue(null, true);
        var root = Build(assembly, project);
        Assert.Equal("new:1,begin:1,set:1,end:1,add:1", Events(assembly));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomSourceInfoCallbacksRetainOrderingAndFailureCleanup(bool fail)
    {
        var compilation = Compilation();
        var profile = XamlFrameworkProfile.Portable with { Runtime = new() { SourceInfo = new("ConstructionCase.SourceInfo", "Set") } };
        var project = new XamlProjectCompiler().Compile(new[] { Document() }, compilation, profile);
        var assembly = Emit(compilation, project);
        Assert.DoesNotContain("out global::ConstructionCase.Node __value)", Source(project));
        if (fail)
        {
            RootType(assembly).GetField("FailAt")!.SetValue(null, "source:2");
            var error = Assert.Throws<TargetInvocationException>(() => Build(assembly, project));
            Assert.Equal("source:2", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            Assert.Equal("new:1,source:1,begin:1,set:1,end:1,add:1,new:2,source:2,dispose:2,dispose:1", Events(assembly));
        }
        else
        {
            var root = Build(assembly, project);
            Assert.Equal("new:1,source:1,begin:1,set:1,end:1,add:1,new:2,source:2,begin:2,set:2,end:2,add:2", Events(assembly));
            Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void IncrementalHelperOwnershipMatchesFreshBuildsAfterEditsAndRemoval(int concurrency)
    {
        var compilation = Compilation(); var compiler = new XamlProjectCompiler();
        var options = new XamlCompilerOptions { MaxDegreeOfParallelism = concurrency };
        var first = Document(); var second = Document("Second.xaml");
        Emit(compilation, compiler.Compile(new[] { first, second }, compilation, options: options));
        foreach (var documents in new[] { new[] { Document(children: "<EarlyNode Text='edited'/>"), second }, new[] { second } })
        {
            var changed = compiler.Compile(documents, compilation, options: options);
            var fresh = new XamlProjectCompiler().Compile(documents, compilation, options: options);
            Assert.Equal(fresh.Documents.Select(document => document.Output.Source), changed.Documents.Select(document => document.Output.Source));
            var root = Build(Emit(compilation, changed), changed, "Second.xaml");
            Assert.Equal("first", Property(Children(root)[0], "Text"));
            Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
        }
    }

    // Exercise fields originating in a registered property assignment, with no
    // BoundObject.Name. Frameworks may choose either representation.
    private sealed class RegisteredNameRule : IXamlObjectBindingRule
    {
        public void Initialize(BindingContext context, ObjectBindingBuilder target) { }
        public void Complete(BindingContext context, ObjectBindingBuilder target)
        {
            for (var index = 0; index < target.Assignments.Count; index++)
                if (target.Assignments[index] is BoundSetAssignment { Member.Name: "Name" } assignment)
                    target.Assignments[index] = assignment with { RegisterName = true };
        }
    }
}
