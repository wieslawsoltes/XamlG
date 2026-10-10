using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class MarkupAssignmentTests
{
    internal const string Model = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using XamlG.Runtime;
        namespace MarkupCase;
        public class Root
        {
            public static readonly List<string> Log = new();
            public static string FailAt;
            public static int Descriptors;
            [Content] public List<Item> Children { get; } = new();
            public static void Event(string value) { Log.Add(value); if (value == FailAt) throw new InvalidOperationException(value); }
        }
        public class Item
        {
            private object _value;
            public string Label { get; set; }
            public static object Descriptor { get { Root.Event("descriptor:" + ++Root.Descriptors); return "Value"; } }
            public virtual object Value { get => _value; set { Root.Event("set:" + value); _value = value; } }
        }
        public class TrackedExtension : ISupportInitialize
        {
            public string Name { get; }
            private string _extra;
            public string Extra { get => _extra; set { Root.Event("extra:" + Name); _extra = value; } }
            public TrackedExtension(string name)
            {
                Root.Event("new:" + name); Name = name;
                var session = new XamlRuntimeSession();
                session.TrackCleanup(() => Root.Event("dispose:" + Name)); session.Attach(this);
            }
            public void BeginInit() => Root.Event("begin:" + Name);
            public void EndInit() => Root.Event("end:" + Name);
            public object ProvideValue(IServiceProvider provider)
            {
                var frame = (XamlRuntimeContext)provider.GetService(typeof(XamlRuntimeContext));
                if (frame.TargetObject is not Item item || item.Label != Name || !Equals(frame.TargetProperty, "Value") ||
                    frame.Session.FindNode(this) == null || frame.RootObject is not Root)
                    throw new InvalidOperationException("lost services");
                Root.Event("provide:" + Name); return this;
            }
            public override string ToString() => Name;
        }
        public class SourceInfo
        {
            public SourceInfo(int line, int column, string path) { }
            public static void Set(object target, SourceInfo source) { }
        }
        """;

    private const string Xaml = "<Root xmlns='clr-namespace:MarkupCase'><Item Label='one' Value='{Tracked one, Extra=a}'/>" +
        "<Item Label='two' Value='{Tracked two, Extra=b}'/></Root>";
    private const string First = "descriptor:1,new:one,begin:one,extra:one,end:one,provide:one,set:one";
    private const string Second = "descriptor:2,new:two,begin:two,extra:two,end:two,provide:two,set:two";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_and_unshared_assignments_preserve_services_order_source_and_editing(bool project)
    {
        foreach (var enabled in new[] { false, true })
        {
            using var code = Compile(enabled, project);
            var root = code.Build();
            Assert.Equal(First + "," + Second, Events(code));
            Assert.Equal(enabled, code.Emission.Source.Contains("private static void __XamlGAssignMarkup_", StringComparison.Ordinal));
            var children = (IList)root.GetType().GetProperty("Children")!.GetValue(root)!;
            var left = children[0]!; var right = children[1]!;
            var first = left.GetType().GetProperty("Value")!.GetValue(left)!;
            var second = right.GetType().GetProperty("Value")!.GetValue(right)!;
            Assert.NotSame(first, second);
            Assert.True(XamlRuntimeSession.TryGet(root, out var session));
            var firstNode = session!.FindNode(first)!;
            var secondNode = session.FindNode(second)!;
            Assert.Equal(session.FindNode(left)!.Key, firstNode.ParentKey);
            Assert.Equal(session.FindNode(right)!.Key, secondNode.ParentKey);
            Assert.NotEqual(firstNode.Source!.Start, secondNode.Source!.Start);
            Assert.Equal("Test.axaml", firstNode.Source.Path);
            Assert.True(session.Apply(0, [new XamlPropertyUpdate(firstNode.Key, "Extra", "edited")]).Applied);
            Assert.Equal("edited", first.GetType().GetProperty("Extra")!.GetValue(first));
            Assert.Equal("b", second.GetType().GetProperty("Extra")!.GetValue(second));
            Assert.True(session.Apply(1, [new XamlPropertyUpdate(session.FindNode(left)!.Key, "Value", second)]).Applied);
            session.Dispose(); session.Dispose();
            Assert.EndsWith("dispose:two,dispose:one", Events(code), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("descriptor:2", "descriptor:2,dispose:one")]
    [InlineData("new:two", "descriptor:2,new:two,dispose:one")]
    [InlineData("begin:two", "descriptor:2,new:two,begin:two,dispose:two,dispose:one")]
    [InlineData("extra:two", "descriptor:2,new:two,begin:two,extra:two,dispose:two,dispose:one")]
    [InlineData("end:two", "descriptor:2,new:two,begin:two,extra:two,end:two,dispose:two,dispose:one")]
    [InlineData("provide:two", "descriptor:2,new:two,begin:two,extra:two,end:two,provide:two,dispose:two,dispose:one")]
    [InlineData("set:two", "descriptor:2,new:two,begin:two,extra:two,end:two,provide:two,set:two,dispose:two,dispose:one")]
    public void Failure_phase_and_reverse_cleanup_match_unshared_emission(string phase, string tail)
    {
        foreach (var enabled in new[] { false, true })
        {
            using var code = Compile(enabled);
            code.Assembly.GetType("MarkupCase.Root")!.GetField("FailAt")!.SetValue(null, phase);
            var error = Assert.Throws<TargetInvocationException>(() => code.Build());
            Assert.Equal(phase, Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            Assert.Equal(First + "," + tail, Events(code));
        }
    }

    [Fact]
    public void Source_info_callbacks_use_the_unshared_path()
    {
        var profile = Profile with { Runtime = Profile.Runtime with { SourceInfo = new("MarkupCase.SourceInfo", "Set") } };
        using var code = CompiledXaml.Create(Xaml, Model, profile);
        Assert.DoesNotContain("__XamlGAssignMarkup_", code.Emission.Source, StringComparison.Ordinal);
        var root = code.Build();
        Assert.Equal(First + "," + Second, Events(code));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
    }

    [Fact]
    public void Occurrence_scalar_mappings_point_to_original_literals_not_helper_bodies()
    {
        using var code = Compile(true);
        foreach (var literal in new[] { "one", "two", "a", "b" })
            Assert.Contains(code.Emission.SourceMappings, mapping =>
                mapping.GeneratedSpan.End <= code.Emission.Source.Length && mapping.SourceSpan.End <= Xaml.Length &&
                code.Emission.Source.Substring(mapping.GeneratedSpan.Start, mapping.GeneratedSpan.Length) == "\"" + literal + "\"" &&
                Xaml.Substring(mapping.SourceSpan.Start, mapping.SourceSpan.Length) == literal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void String_constants_keep_their_type_inside_object_valued_extension_properties(bool project)
    {
        var model = Model.Replace("private string _extra;", "private object _extra;", StringComparison.Ordinal)
            .Replace("public string Extra", "public object Extra", StringComparison.Ordinal);
        foreach (var enabled in new[] { false, true })
        {
            using var code = CompiledXaml.Create(Xaml, model, Profile, shareAcrossDocuments: project,
                options: new() { ShareMarkupAssignments = enabled });
            var root = code.Build();
            Assert.Equal(First + "," + Second, Events(code));
            Assert.Equal(enabled, code.Emission.Source.Contains("private static void __XamlGAssignMarkup_", StringComparison.Ordinal));
            var children = (IList)root.GetType().GetProperty("Children")!.GetValue(root)!;
            var extension = children[0]!.GetType().GetProperty("Value")!.GetValue(children[0])!;
            Assert.Equal("a", extension.GetType().GetProperty("Extra")!.GetValue(extension));
            Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
        }
    }

    private static XamlFrameworkProfile Profile => XamlFrameworkProfile.Portable with
    { MemberBindingRules = ImmutableArray.Create<IXamlMemberBindingRule>(new DescriptorRule()) };
    private static CompiledXaml Compile(bool enabled, bool project = false) => CompiledXaml.Create(Xaml, Model, Profile,
        shareAcrossDocuments: project, options: new() { ShareMarkupAssignments = enabled });
    private static string Events(CompiledXaml code) => string.Join(",",
        ((IEnumerable)code.Assembly.GetType("MarkupCase.Root")!.GetField("Log")!.GetValue(null)!).Cast<string>());

    private sealed class DescriptorRule : IXamlMemberBindingRule
    {
        public BoundMember Bind(BindingContext context, ITypeSymbol targetType, BoundMember member, NamespaceScope scope)
        {
            if (targetType.Name != "Item" || member.Name != "Value") return member;
            var descriptor = (IPropertySymbol)targetType.GetMembers("Descriptor").Single();
            return member with { TargetDescriptor = new BoundStaticExpression(descriptor, descriptor.Type, member.Span) };
        }
    }
}
