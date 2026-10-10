using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Roslyn;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class RuntimeContractCacheTests
{
    private const string Model = "namespace Model { public interface IRoot { object RootObject { get; } } public class Item { } }";
    private static XamlFrameworkProfile Profile(string contract = "Model.IRoot") => new()
    {
        Runtime = new() { Services = ImmutableArray.Create(new XamlServiceMapping(contract, XamlServiceKind.RootObject)) }
    };
    private static BindingContext Context(RoslynTypeSystem types, XamlFrameworkProfile profile, string ns = "urn:first") =>
        new(XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model' xmlns:p='" + ns + "'/>"), types, profile, new(), default);

    [Fact]
    public void Contracts_are_shared_but_namespace_declarations_and_diagnostics_are_document_local()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var profile = Profile();
        var first = Context(types, profile, "using:First");
        var second = Context(types, profile, "using:Second");
        Assert.Same(first.Runtime.Services[0], second.Runtime.Services[0]);
        Assert.Contains(first.Runtime.NamespaceMappings, item => item.XmlNamespace == "using:First");
        Assert.DoesNotContain(second.Runtime.NamespaceMappings, item => item.XmlNamespace == "using:First");
        Assert.Contains(second.Runtime.NamespaceMappings, item => item.XmlNamespace == "using:Second");
        first.Report("TEST", "local", new(0, 0));
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public void Invalid_contract_diagnostics_are_replayed_once_and_in_the_original_order()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var profile = Profile("Model.Missing");
        var tree = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model' A='1' A='2'/>");
        var first = new BindingContext(tree, types, profile, new(), default);
        var second = new BindingContext(tree, types, profile, new(), default);
        Assert.Equal(new[] { "XG0006", "XG1100" }, first.Diagnostics.Select(d => d.Code));
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        _ = second.Runtime; _ = second.Runtime;
        Assert.Equal(2, second.Diagnostics.Count);
    }

    [Fact]
    public void Signature_probe_defers_services_but_custom_type_rules_can_request_them()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var profile = Profile("Model.Missing");
        var tree = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'/>");
        var probe = BindingContext.CreateSignatureProbe(tree, types, profile, new());
        Assert.Empty(probe.Diagnostics);
        Assert.Empty(probe.Runtime.Services);
        Assert.Equal("XG1100", Assert.Single(probe.Diagnostics).Code);
        var observer = new ObserveRuntime();
        var observed = Profile() with { TypeBindingRules = ImmutableArray.Create<IXamlTypeBindingRule>(observer) };
        var catalog = XamlResourceCatalogBuilder.Create(new[] { new XamlProjectDocument(tree, "View.xaml") }, types, observed, new());
        Assert.Single(catalog.Resources);
        Assert.True(observer.Observed);
    }

    [Fact]
    public void Compilation_and_runtime_configuration_changes_cannot_reuse_stale_contracts()
    {
        var profile = Profile();
        var firstTypes = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var first = Context(firstTypes, profile);
        var differentProfile = Context(firstTypes, Profile("Model.Missing"));
        Assert.Equal("XG1100", Assert.Single(differentProfile.Diagnostics).Code);
        var nextTypes = new RoslynTypeSystem(CompilationFactory.Create(Model.Replace("RootObject", "Unknown")));
        var next = Context(nextTypes, profile);
        Assert.Equal("XG1100", Assert.Single(next.Diagnostics).Code);
        Assert.NotSame(first.Runtime.Services[0], next.Runtime.Services[0]);
    }

    [Fact]
    public void Parallel_cache_publication_does_not_duplicate_diagnostics_or_capture_documents()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var profile = Profile() with { Runtime = new() { Services = ImmutableArray.Create(
            new XamlServiceMapping("Model.IRoot", XamlServiceKind.RootObject),
            new XamlServiceMapping("Model.Missing", XamlServiceKind.RootObject)) } };
        var contexts = new BindingContext[32];
        Parallel.For(0, contexts.Length, i => contexts[i] = Context(types, profile, "using:Namespace" + i));
        for (var i = 0; i < contexts.Length; i++)
        {
            Assert.Same(contexts[0].Runtime.Services[0], contexts[i].Runtime.Services[0]);
            Assert.Equal("XG1100", Assert.Single(contexts[i].Diagnostics).Code);
            Assert.Contains(contexts[i].Runtime.NamespaceMappings, mapping => mapping.XmlNamespace == "using:Namespace" + i);
        }
    }

    [Fact]
    public void Cancellation_is_checked_on_cache_hits_and_does_not_poison_the_contract()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var profile = Profile();
        var initial = Context(types, profile);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new BindingContext(initial.Syntax, types, profile, new(), cancelled.Token));
        Assert.Empty(Context(types, profile).Diagnostics);
    }

    [Fact]
    public void Cache_does_not_keep_its_type_system_owner_alive()
    {
        var profile = Profile();
        var reference = CreateCollectibleOwner(profile);
        for (var i = 0; i < 5 && reference.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(reference.IsAlive);
        GC.KeepAlive(profile);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCollectibleOwner(XamlFrameworkProfile profile)
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        _ = Context(types, profile).Runtime;
        return new(types);
    }

    private sealed class ObserveRuntime : IXamlTypeBindingRule
    {
        public bool Observed { get; private set; }
        public bool TryResolve(BindingContext context, XamlTypeNameSyntax syntax, NamespaceScope scope, out INamedTypeSymbol? type)
        { Observed = context.Runtime.Services.Length == 1; type = null; return false; }
    }
}
