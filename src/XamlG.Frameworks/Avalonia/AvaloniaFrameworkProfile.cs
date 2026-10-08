using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Frameworks.Avalonia.Resources;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia;

public static class AvaloniaFrameworkProfile
{
    public static XamlFrameworkProfile Create(bool compileBindingsByDefault = true, bool createSourceInfo = false)
    {
        var bindings = new AvaloniaBindingScopeRule(compileBindingsByDefault);
        var classes = new AvaloniaClassBindingRule();
        var resources = new AvaloniaResourceMergeRule();
        var deferredResources = new AvaloniaDeferredResourceRule();
        var styleWarnings = new AvaloniaStyleWarningsRule();
        var options = new AvaloniaOptionMarkupRule();
        var bindingValues = new AvaloniaBindingMarkupRule();
        var names = new References.AvaloniaNameScopeRule();
        var directives = new AvaloniaDirectivePolicy();
        return XamlFrameworkProfile.Portable with
        {
            Name = "Avalonia",
            NameDirectiveProperty = "Name",
            Directives = directives,
            SourceLoader = new(AvaloniaLoaderMetadata.Loader, AvaloniaLoaderMetadata.Load)
            { ResourceScheme = AvaloniaResourceMetadata.Scheme, LegacyIndexMetadataName = AvaloniaLoaderMetadata.CompiledIndex },
            NameReferenceRules = XamlFrameworkProfile.Portable.NameReferenceRules.Add(new References.AvaloniaNameReferenceRule()),
            ResourceScheme = AvaloniaResourceMetadata.Scheme,
            ResourceSourceMembers = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
                .Add(AvaloniaResourceMetadata.ResourceInclude, AvaloniaResourceMetadata.Source)
                .Add(AvaloniaResourceMetadata.MergeResourceInclude, AvaloniaResourceMetadata.Source)
                .Add(AvaloniaResourceMetadata.StyleInclude, AvaloniaResourceMetadata.Source),
            ObjectExpressionRules = XamlFrameworkProfile.Portable.ObjectExpressionRules.Add(new AvaloniaResourceIncludeRule()).Add(deferredResources).Add(options).Add(bindingValues),
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
                UseTypeDescriptorStubs = true,
                SourceInfo = createSourceInfo ? new("Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo", "SetXamlSourceInfo") : null,
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
                    new XamlServiceMapping(AvaloniaMetadata.ParentProvider, XamlServiceKind.ParentStack)
                    {
                        ImplementationInterfaceMetadataName = AvaloniaMetadata.EagerParentProvider,
                        ParentProviderAdapter = new(AvaloniaMetadata.RuntimeHelpers, "AsEagerParentStackProvider")
                    },
                    new XamlServiceMapping(AvaloniaMetadata.NamespaceProvider, XamlServiceKind.XmlNamespaces, AvaloniaMetadata.NamespaceItem))
            },
            BindingRules = ImmutableArray.Create<IXamlBindingRule>(directives, bindings, new AvaloniaStyleDirectiveRule(), classes, deferredResources),
            TextConversionRules = ImmutableArray.Create<IXamlTextConversionRule>(new AvaloniaPropertyReferenceTextRule(),
                new AvaloniaFontFamilyTextRule(), new AvaloniaConstructorLiteralRule(), new AvaloniaNumericLiteralRule(), new AvaloniaAnimationLiteralRule(), new AvaloniaInputLiteralRule(), new AvaloniaStaticLiteralRule(),
                new AvaloniaTextConversionRule(), new AvaloniaListLiteralRule()),
            TypeConverterProviders = ImmutableArray.Create<IXamlTypeConverterProvider>(new AvaloniaTypeConverterProvider()),
            ObjectBindingRules = ImmutableArray.Create<IXamlObjectBindingRule>(names, new AvaloniaStyleObjectRule(), new AvaloniaTemplatePartsRule(), styleWarnings, bindings, classes, resources, deferredResources, new AvaloniaResourceSourceInfoRule(), new AvaloniaResourceCapacityRule()),
            MemberBindingRules = ImmutableArray.Create<IXamlMemberBindingRule>(new AvaloniaPropertyDescriptorRule(), names),
            PropertyBindingRules = ImmutableArray.Create<IXamlPropertyBindingRule>(new AvaloniaDesignPropertyRule(), styleWarnings, resources,
                new AvaloniaStylePropertyRule(), new AvaloniaContainerQueryRule(), new References.AvaloniaResolveByNameRule(), new AvaloniaRegisteredSetterRule(), new AvaloniaBindingRule()),
            MarkupBindingRules = ImmutableArray.Create<IXamlMarkupBindingRule>(options, new AvaloniaCompiledBindingRule(), bindingValues)
        };
    }
}
