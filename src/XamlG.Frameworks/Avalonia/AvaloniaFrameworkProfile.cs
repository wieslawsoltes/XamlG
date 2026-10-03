using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia;

public static class AvaloniaFrameworkProfile
{
    public static XamlFrameworkProfile Create(bool compileBindingsByDefault = true)
    {
        var bindings = new AvaloniaBindingScopeRule(compileBindingsByDefault);
        return XamlFrameworkProfile.Portable with
        {
            Name = "Avalonia",
            TypeSystem = new XamlTypeSystemConfiguration
            {
                XmlnsDefinitionAttributes = ImmutableArray.Create(AvaloniaMetadata.XmlnsDefinition),
                ContentAttributes = ImmutableArray.Create(AvaloniaMetadata.Content),
                DeferredContentAttributes = ImmutableArray.Create(AvaloniaMetadata.DeferredContent),
                WhitespaceSignificantCollectionAttributes = ImmutableArray.Create(AvaloniaMetadata.WhitespaceSignificant),
                TrimSurroundingWhitespaceAttributes = ImmutableArray.Create(AvaloniaMetadata.TrimSurroundingWhitespace),
                UsableDuringInitializationAttributes = ImmutableArray.Create(AvaloniaMetadata.UsableDuringInitialization),
                AddChildInterfaces = ImmutableArray.Create(AvaloniaMetadata.AddChild, AvaloniaMetadata.AddChildGeneric),
                IgnoredNamespaces = ImmutableHashSet.Create(StringComparer.Ordinal, AvaloniaMetadata.DesignNamespace)
            },
            Runtime = new XamlRuntimeConfiguration
            {
                RootServiceProviderFactory = new(AvaloniaMetadata.RuntimeHelpers, "CreateRootServiceProviderV3"),
                InnerServiceProviderFactory = new(AvaloniaMetadata.RuntimeHelpers, "CreateInnerServiceProviderV1"),
                DeferredContentCustomizer = new(AvaloniaMetadata.RuntimeHelpers, "DeferredTransformationFactoryV3"),
                DeferredDefaultTypeArgument = AvaloniaMetadata.Control,
                DeferredTypeArgumentAttributeProperties = ImmutableArray.Create("TemplateResultType"),
                NameScope = new(AvaloniaMetadata.NameScope, AvaloniaMetadata.NameScopeContract) { Attach = new(AvaloniaMetadata.NameScope, "SetNameScope") },
                Services = ImmutableArray.Create(
                    new XamlServiceMapping(AvaloniaMetadata.RootProvider, XamlServiceKind.RootObject),
                    new XamlServiceMapping(AvaloniaMetadata.ValueTarget, XamlServiceKind.ProvideValueTarget),
                    new XamlServiceMapping(AvaloniaMetadata.UriContext, XamlServiceKind.UriContext),
                    new XamlServiceMapping(AvaloniaMetadata.ParentProvider, XamlServiceKind.ParentStack),
                    new XamlServiceMapping(AvaloniaMetadata.NamespaceProvider, XamlServiceKind.XmlNamespaces, AvaloniaMetadata.NamespaceItem))
            },
            BindingRules = ImmutableArray.Create<IXamlBindingRule>(bindings),
            TextConversionRules = ImmutableArray.Create<IXamlTextConversionRule>(new AvaloniaTextConversionRule()),
            ObjectBindingRules = ImmutableArray.Create<IXamlObjectBindingRule>(new AvaloniaStyleObjectRule(), bindings),
            MemberBindingRules = ImmutableArray.Create<IXamlMemberBindingRule>(new AvaloniaPropertyDescriptorRule()),
            PropertyBindingRules = ImmutableArray.Create<IXamlPropertyBindingRule>(new AvaloniaStylePropertyRule(), new AvaloniaBindingRule()),
            MarkupBindingRules = ImmutableArray.Create<IXamlMarkupBindingRule>(new AvaloniaCompiledBindingRule(), new AvaloniaBindingMarkupRule())
        };
    }
}
