using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Shares framework context plumbing; each call still creates its own context and services.</summary>
internal sealed class SharedRuntimeContextEmitter(BoundDocument document)
{
    public const string NamespaceMapType = "global::System.Collections.Generic.IReadOnlyDictionary<global::System.Type, object>";

    public void Emit(CSharpWriter writer, string serviceType)
    {
        var runtime = document.Runtime;
        writer.Open("internal static " + CSharpNames.Context + " CreateContext(" + CSharpNames.Provider +
            "? __services, object? __root, string? __baseUri, " + NamespaceMapType + "? __namespaces, bool __deferred, bool __hasRoot)");
        if (runtime.RootServiceProviderFactory is { } factory)
        {
            writer.Open("if (!__deferred)");
            writer.Line("var __rootServices = " + factory.ContainingType.CSharpName() + "." + CSharpNames.Method(factory) + "(__services);");
            writer.Line("var __inheritedServices = global::XamlG.Runtime.XamlServiceProviderChain.Combine(__rootServices, __services);");
            RootLookup(writer, "__rootServices");
            writer.Line("__services = __inheritedServices;");
            writer.Close();
            writer.Open("else");
            RootLookup(writer, "__services");
            writer.Close();
        }
        else RootLookup(writer, "__services");
        var checks = runtime.Services.Select(service => "__type == typeof(" + service.InterfaceType.CSharpName() + ")");
        var services = "static (__frame, __type) => (" + string.Join(" || ", checks) + ") ? new " + serviceType + "(__frame) : null";
        var inner = runtime.InnerServiceProviderFactory is { } innerFactory
            ? "static __compiled => " + innerFactory.ContainingType.CSharpName() + "." + CSharpNames.Method(innerFactory) + "(__compiled)" : "null";
        writer.Line("var __created = new " + CSharpNames.Context + "(__services, __root, " +
            "__baseUri == null ? null : new global::System.Uri(__baseUri, global::System.UriKind.RelativeOrAbsolute), " +
            services + ", " + inner + ", __namespaces, useTypeDescriptorStubs: " +
            (document.Profile.Runtime.UseTypeDescriptorStubs ? "true" : "false") + ");");
        if (runtime.NameScope != null) writer.Line("InitializeNameScope(__created, __services);");
        writer.Line("return __created;");
        writer.Close();
        if (runtime.NameScope is { } scope)
        {
            writer.Open("internal static void InitializeNameScope(" + CSharpNames.Context + " __frame, " + CSharpNames.Provider + "? __services)");
            var type = scope.ContractType.CSharpName();
            writer.Line("var __scope = (" + type + "?)__services?.GetService(typeof(" + type + ")) ?? new " + scope.ConcreteType.CSharpName() + "();");
            writer.Line("__frame.AddService(typeof(" + type + "), __scope);");
            writer.Close();
        }
        writer.Open("internal static void Complete(" + CSharpNames.Context + " __frame, object? __root)");
        writer.Line("__frame.Complete(__root);");
        if (runtime.NameScope is { } completed)
        {
            writer.Line("var __scope = (" + completed.ContractType.CSharpName() + ")__frame.GetService(typeof(" + completed.ContractType.CSharpName() + "))!;");
            writer.Line("__scope." + CSharpNames.Method(completed.Complete) + "();");
            if (completed.Attach is { } attach)
                writer.Line("if (__root is " + attach.Parameters[0].Type.CSharpName() + " __owner) " +
                    attach.ContainingType.CSharpName() + "." + CSharpNames.Method(attach) + "(__owner, __scope);");
        }
        writer.Close();
    }

    private void RootLookup(CSharpWriter writer, string provider)
    {
        var contract = document.Runtime.Services.FirstOrDefault(service => service.Properties.Any(property => property.Value == XamlServiceValue.RootObject));
        if (contract == null) return;
        var property = contract.Properties.First(property => property.Value == XamlServiceValue.RootObject).Property;
        // Keep the factory result's static type, including a custom provider's
        // public GetService implementation, rather than introducing an interface cast.
        writer.Line("if (!__hasRoot) __root = ((" + contract.InterfaceType.CSharpName() + "?)" + provider +
            "?.GetService(typeof(" + contract.InterfaceType.CSharpName() + ")))?." + CSharpNames.Identifier(property.Name) + ";");
    }
}
