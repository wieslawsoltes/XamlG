using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Frameworks.Avalonia.Resources;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia;

public static class AvaloniaFrameworkProfile
{
    public static XamlFrameworkProfile Create(bool compileBindingsByDefault = true)
    {
        var bindings = new AvaloniaBindingScopeRule(compileBindingsByDefault);
        var classes = new AvaloniaClassBindingRule();
        var resources = new AvaloniaResourceMergeRule();
        var styleWarnings = new AvaloniaStyleWarningsRule();
        var options = new AvaloniaOptionMarkupRule();
        var bindingValues = new AvaloniaBindingMarkupRule();
        return XamlFrameworkProfile.Portable with
        {
            Name = "Avalonia",
            SourceLoader = new(AvaloniaLoaderMetadata.Loader, AvaloniaLoaderMetadata.Load)
            { ResourceScheme = AvaloniaResourceMetadata.Scheme, LegacyIndexMetadataName = AvaloniaLoaderMetadata.CompiledIndex },
            NameReferenceRules = XamlFrameworkProfile.Portable.NameReferenceRules.Add(new References.AvaloniaNameReferenceRule()),
            ResourceScheme = AvaloniaResourceMetadata.Scheme,
            ResourceSourceMembers = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
                .Add(AvaloniaResourceMetadata.ResourceInclude, AvaloniaResourceMetadata.Source)
                .Add(AvaloniaResourceMetadata.MergeResourceInclude, AvaloniaResourceMetadata.Source)
                .Add(AvaloniaResourceMetadata.StyleInclude, AvaloniaResourceMetadata.Source),
            ObjectExpressionRules = XamlFrameworkProfile.Portable.ObjectExpressionRules.Add(new AvaloniaResourceIncludeRule()).Add(options).Add(bindingValues),
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
            BindingRules = ImmutableArray.Create<IXamlBindingRule>(bindings, new AvaloniaStyleDirectiveRule(), classes),
            TextConversionRules = ImmutableArray.Create<IXamlTextConversionRule>(new AvaloniaPropertyReferenceTextRule(),
                new AvaloniaFontFamilyTextRule(), new XamlStaticMemberTextRule(new[] { AvaloniaLiteralMetadata.WindowTransparencyLevel }),
                new XamlDelimitedListTextRule(new[] { AvaloniaRegisteredSetterMetadata.List, AvaloniaRegisteredSetterMetadata.ReadOnlyList }),
                new AvaloniaTextConversionRule()),
            ObjectBindingRules = ImmutableArray.Create<IXamlObjectBindingRule>(new References.AvaloniaNameScopeRule(), new AvaloniaStyleObjectRule(), new AvaloniaTemplatePartsRule(), styleWarnings, bindings, classes, resources),
            MemberBindingRules = ImmutableArray.Create<IXamlMemberBindingRule>(new AvaloniaPropertyDescriptorRule()),
            PropertyBindingRules = ImmutableArray.Create<IXamlPropertyBindingRule>(new AvaloniaDesignPropertyRule(), styleWarnings, resources,
                new AvaloniaStylePropertyRule(), new AvaloniaContainerQueryRule(), new References.AvaloniaResolveByNameRule(), new AvaloniaRegisteredSetterRule(), new AvaloniaBindingRule()),
            MarkupBindingRules = ImmutableArray.Create<IXamlMarkupBindingRule>(options, new AvaloniaCompiledBindingRule(), bindingValues)
        };
    }
}
