using System.Collections;
using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ServiceContractTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        using XamlG.Runtime;
        namespace ContractFixture
        {
            public interface IRoot { object RootObject { get; } object IntermediateRootObject { get; } }
            public interface ITarget { object TargetObject { get; } object TargetProperty { get; } }
            public interface IParents { IEnumerable<object> Parents { get; } }
            public interface IUri { Uri BaseUri { get; set; } }
            public interface INamespaces { IReadOnlyDictionary<string, IReadOnlyList<NamespaceInfo>> XmlNamespaces { get; } }
            public class NamespaceInfo { public string ClrNamespace { get; set; } public string ClrAssemblyName { get; set; } }
            public interface IScope { void Register(string name, object value); void Complete(); }
            public class Scope : IScope
            {
                public Dictionary<string, object> Values { get; } = new();
                public bool Completed { get; private set; }
                public void Register(string name, object value) { if (Completed) throw new Exception("Completed scope"); Values.Add(name, value); }
                public void Complete() { Completed = true; }
                public static void Attach(Panel owner, IScope scope) { owner.Scope = (Scope)scope; }
            }
            public class Panel
            {
                [Content] public List<Panel> Children { get; } = new();
                public object Value { get; set; }
                public string Namespace { get; set; }
                public string Name { get; set; }
                public Scope Scope { get; set; }
            }
            public class TargetExtension { public object ProvideValue(IServiceProvider services) => ((ITarget)services.GetService(typeof(ITarget))).TargetObject; }
            public class RootExtension { public object ProvideValue(IServiceProvider services) => ((IRoot)services.GetService(typeof(IRoot))).RootObject; }
            public class NamespaceExtension
            {
                public string ProvideValue(IServiceProvider services)
                {
                    var aliases = ((INamespaces)services.GetService(typeof(INamespaces))).XmlNamespaces;
                    return aliases["vm"][0].ClrNamespace;
                }
            }
        }
        """;

    private static XamlFrameworkProfile Profile => XamlFrameworkProfile.Portable with
    {
        Runtime = new()
        {
            Services = ImmutableArray.Create(
                new XamlServiceMapping("ContractFixture.IRoot", XamlServiceKind.RootObject),
                new XamlServiceMapping("ContractFixture.ITarget", XamlServiceKind.ProvideValueTarget),
                new XamlServiceMapping("ContractFixture.IParents", XamlServiceKind.ParentStack),
                new XamlServiceMapping("ContractFixture.IUri", XamlServiceKind.UriContext),
                new XamlServiceMapping("ContractFixture.INamespaces", XamlServiceKind.XmlNamespaces)),
            NameScope = new("ContractFixture.Scope", "ContractFixture.IScope")
            {
                Attach = new("ContractFixture.Scope", "Attach")
            }
        }
    };

    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    [Fact]
    public void AdaptersResolveRootAndTargetAgainstTheirOwnFrames()
    {
        const string xaml = "<Panel xmlns='clr-namespace:ContractFixture' Value='{Root}'><Panel Value='{Target}'/></Panel>";
        using var code = CompiledXaml.Create(xaml, Model, Profile);
        var root = code.Build();
        var child = ((IList)Property(root, "Children")!)[0]!;
        Assert.Same(root, Property(root, "Value"));
        Assert.Same(child, Property(child, "Value"));
    }

    [Fact]
    public void NamespaceServiceReflectsLexicalPrefixShadowing()
    {
        const string xaml = "<Panel xmlns='clr-namespace:ContractFixture' xmlns:vm='clr-namespace:First' Namespace='{Namespace}'><Panel xmlns:vm='clr-namespace:Second' Namespace='{Namespace}'/></Panel>";
        using var code = CompiledXaml.Create(xaml, Model, Profile);
        var root = code.Build();
        Assert.Equal("First", Property(root, "Namespace"));
        Assert.Equal("Second", Property(((IList)Property(root, "Children")!)[0]!, "Namespace"));
    }

    [Fact]
    public void RegistersAndCompletesTheFrameworkNameScope()
    {
        const string xaml = "<Panel xmlns='clr-namespace:ContractFixture' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><Panel x:Name='child'/></Panel>";
        using var code = CompiledXaml.Create(xaml, Model, Profile);
        var root = code.Build();
        var scope = Property(root, "Scope")!;
        Assert.Equal(true, Property(scope, "Completed"));
        var values = (IDictionary)Property(scope, "Values")!;
        Assert.Same(((IList)Property(root, "Children")!)[0], values["child"]);
    }

    private const string EagerModel = """
        namespace ContractFixture
        {
            public interface IEagerParents : IParents
            {
                System.Collections.Generic.IReadOnlyList<object> DirectParentsStack { get; }
                IEagerParents ParentProvider { get; }
            }
            public interface IBadParents : IParents { int DirectParentsStack { get; } }
            public static class ParentAdapters
            {
                public static IEagerParents Wrap(IParents parents) => (IEagerParents)parents;
                public static object WrongReturn(IParents parents) => parents;
                public static IEagerParents WrongArgument(string parents) => null;
                public static IEagerParents Generic<T>(IParents parents) => null;
            }
            public class EagerExtension
            {
                public object ProvideValue(System.IServiceProvider services)
                {
                    var parents = (IEagerParents)services.GetService(typeof(IParents));
                    return parents.ParentProvider == null && parents.DirectParentsStack.Count == 1 &&
                        object.ReferenceEquals(parents.DirectParentsStack[0], System.Linq.Enumerable.First(parents.Parents)) &&
                        services.GetService(typeof(IEagerParents)) == null;
                }
            }
        }
        """;

    private static XamlFrameworkProfile EagerProfile(string implementation, string? adapter) => XamlFrameworkProfile.Portable with
    {
        Runtime = new()
        {
            Services = ImmutableArray.Create(new XamlServiceMapping("ContractFixture.IParents", XamlServiceKind.ParentStack)
            {
                ImplementationInterfaceMetadataName = implementation,
                ParentProviderAdapter = adapter == null ? null : new("ContractFixture.ParentAdapters", adapter)
            })
        }
    };

    [Fact]
    public void ServiceImplementationCanExtendItsLookupContract()
    {
        using var code = CompiledXaml.Create("<Panel xmlns='clr-namespace:ContractFixture' Value='{Eager}'/>", Model + EagerModel,
            EagerProfile("ContractFixture.IEagerParents", "Wrap"));
        Assert.Equal(true, Property(code.Build(), "Value"));
    }

    [Theory]
    [InlineData("ContractFixture.Missing", "Wrap")]
    [InlineData("ContractFixture.Panel", "Wrap")]
    [InlineData("ContractFixture.IRoot", "Wrap")]
    [InlineData("ContractFixture.IBadParents", "Wrap")]
    [InlineData("ContractFixture.IEagerParents", null)]
    [InlineData("ContractFixture.IEagerParents", "WrongReturn")]
    [InlineData("ContractFixture.IEagerParents", "WrongArgument")]
    [InlineData("ContractFixture.IEagerParents", "Generic")]
    public void InvalidServiceImplementationsAreDiagnosedBeforeEmission(string implementation, string? adapter)
    {
        var result = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Panel xmlns='clr-namespace:ContractFixture'/>", "Services.xaml"),
            CompilationFactory.Create(Model + EagerModel), EagerProfile(implementation, adapter));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "XG1100");
    }
}
