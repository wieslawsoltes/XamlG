using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.XamlIl.Runtime;
using XamlG.Runtime;

namespace XamlG.AvaloniaRuntime;

/// <summary>Preserves construction services and owns resource lifetimes while Avalonia
/// supplies resource ancestry, child namescopes and sharing behavior.</summary>
public static class AvaloniaDeferredResourceFactory
{
    public static IDeferredContent Create(IntPtr builder, IServiceProvider services)
    {
        var context = (XamlRuntimeContext)services.GetService(typeof(XamlRuntimeContext))!;
        var content = XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<object>(builder, services);
        return new ResourceContent(content, context, new ConstructionServices(services));
    }

    private sealed class ConstructionServices(IServiceProvider services) : IServiceProvider
    {
        // Avalonia already captured the definition's resource parents. Preserve the other
        // construction services without appending that parent stack a second time.
        public object? GetService(Type serviceType) => typeof(IAvaloniaXamlIlParentStackProvider).IsAssignableFrom(serviceType)
            ? null : services.GetService(serviceType);
    }

    private sealed class ResourceContent(IDeferredContent content, XamlRuntimeContext owner, IServiceProvider services) : IDeferredContent
    {
        public object? Build(IServiceProvider? serviceProvider)
        {
            ObjectDisposedException.ThrowIf(owner.Session.IsDisposed, owner.Session);
            var result = content.Build(XamlServiceProviderChain.Combine(serviceProvider, services));
            var value = result is ITemplateResult template ? template.Result : result;
            if (value != null && XamlRuntimeSession.TryGet(value, out var session) && !ReferenceEquals(owner.Session, session))
                owner.Session.TrackCleanup(session!.Dispose);
            return result;
        }
    }
}
