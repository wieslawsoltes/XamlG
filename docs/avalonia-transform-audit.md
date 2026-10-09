# Pinned Avalonia transform inventory

The comparison uses Avalonia `8eeda4f6f546165b3f72e63c9f42247abb306905` and runtime/compiler package `12.1.3`, with portable XamlX `7ef6aef496ab6e8dcf3df04bef697be49db37c04`. The registry is `AvaloniaXamlIlCompiler.cs` under the pinned loader's `CompilerExtensions` directory. It registers 38 document transforms and two group transforms. The table lists every registered transform; the `AvaloniaXamlIl` prefix is omitted where present.

The registry and `AvaloniaXamlIlLanguageParseIntrinsics.cs` are byte-identical in
the newer ControlCatalog pin `a9429a328057befa287ffb5e981f58b86a86eda0`.
The [parser coverage inventory](avalonia-parser-coverage.md) additionally covers
public parser APIs added between these versions, including FlexBasis and
FontVariationSettings.

XamlG keeps framework policy in `XamlG.Frameworks`, symbol-based binding in the portable compiler and typed lowering in `XamlG.CSharp`. Runtime factories, property access and service contracts use public Avalonia APIs. No production package references XamlX or the runtime XAML compiler.

| Registered transform | Native implementation | Executable coverage |
| --- | --- | --- |
| XNameTransformer | Name directive property mapping and ordered registration | LiteralNameContractTests, ProvidedNameTests |
| IgnoredDirectivesTransformer | AvaloniaDirectivePolicy and syntax validation | DirectiveContractTests, DirectiveIntegrationTests |
| DesignPropertiesTransformer | AvaloniaDesignPropertyRule | design-property and designer tests |
| AvaloniaBindingExtensionTransformer | AvaloniaBindingScopeRule, compiled/reflection value rules | CompiledBindingTests, BindingCompletionTests |
| ResolveClassesPropertiesTransformer | AvaloniaClassBindingRule | ConditionalClassTests |
| TransformInstanceAttachedProperties | AvaloniaRegisteredSetterRule and typed property descriptors | RegisteredSpecialValueTests, RegisteredPropertySelectionTests |
| TransformSyntheticCompiledBindingMembers | CompiledBindingInputReader and relative-source binder | BindingCompletionTests, RemainingBindingTests |
| AvaloniaPropertyResolver | registered property resolver and adapters | RegisteredPropertySelectionTests, BindingPathMetadataTests |
| ReorderClassesPropertiesTransformer | class binding assignment ordering | ConditionalClassTests |
| ClassesTransformer | class string/list conversion and class adapter | ConditionalClassTests |
| ControlThemeTransformer | AvaloniaStyleObjectRule | StyleScopeTests, both original theme corpora |
| SelectorTransformer | selector parser, binder and property conversion | SelectorContractTests |
| QueryTransformer | container query parser and binder | ContainerQueryTests |
| DuplicateSettersChecker | AvaloniaStyleWarningsRule | StyleWarningTests |
| ControlTemplateTargetTypeMetadataTransformer | style/template annotations and target resolver | StyleScopeTests, NestedTemplateOwnerTests |
| BindingPathParser | BindingPathParser, CompiledBindingInputReader | BindingPropertyElementTests, RemainingBindingTests |
| SetterTargetTypeMetadataTransformer | explicit setter scope annotations | SetterContractTests, ConditionalClassTests |
| SetterTransformer | style object/property rules and detached template metadata | SetterContractTests, RemainingScopeTests |
| StyleValidatorTransformer | style scope validation | StyleScopeTests, StyleWarningTests |
| ConstructorServiceProviderTransformer | ConstructorBinder service constructor selection | ElementBindingServiceTests, converter/service tests |
| TransitionsTypeMetadataTransformer | transition property scopes | ThemeLiteralCompatibilityTests, RemainingTransformTests |
| ResolveByNameMarkupExtensionReplacer | AvaloniaResolveByNameRule | ResolveByNameRegressionTests, literal name cases |
| ThemeVariantProviderTransformer | resource merge/value initializers | ThemeResourceInitializationTests, ResourceLinkingTests |
| DataTemplateWarningsTransformer | AvaloniaStyleWarningsRule | StyleWarningTests |
| OptionMarkupExtensionTransformer | configurable XamlOptionMarkupBinder | OptionMarkupTests, RemainingTransformTests |
| XDataTypeTransformer | AvaloniaDataTypeMetadata and binding scope rules | DataTypeMetadataTests |
| AddNameScopeRegistration | ordered names, child scopes and name catalog | ProvidedNameTests, LiteralNameContractTests, NamedBindingSourceTests |
| ControlTemplatePartsChecker | AvaloniaTemplatePartsRule | TemplatePartTests |
| DataContextTypeTransformer | context and item inference with cached metadata | DataContextInferenceTests, ItemTypeInferenceTests |
| BindingPathTransformer | typed paths, property accessors, method delegates and commands | BindingPathMetadataTests, RemainingBindingTests, RemainingScopeTests |
| CompiledBindingsMetadataRemover | metadata stays in binder annotations rather than runtime assignments | emitted binding tests and theme builds |
| AvaloniaXamlResourceTransformer | deferred resources, shared directives and keyed source information | DeferredResourceTests, RuntimeSourceInfoTests |
| TransformRoutedEvent | typed routed-event binding | routed-event cases in framework tests |
| ControlTemplatePriorityTransformer | AvaloniaTemplatePriority and registered setters | TemplatePriorityTests, RemainingScopeTests |
| MetadataRemover | binder-only style annotations | source emission and original theme gates |
| EnsureResourceDictionaryCapacityTransformer | AvaloniaResourceCapacityRule | RemainingResourceTests |
| RootObjectScope | runtime root/child namescope creation, attachment and completion | name, deferred-content and service-provider tests |
| AddSourceInfoTransformer | typed source information emission | RuntimeSourceInfoTests, LiteralConstructionTests, StructuredLiteralTests |
| XamlMergeResourceGroupTransformer | project resource graph and merge lowering | resource include/merge and referenced-assembly tests |
| AvaloniaXamlIncludeTransformer | project/catalog factory resolution and resource exports | resource project tests, both original theme corpora |

The build-task directives (`x:Precompile`, `x:ClassModifier`) and named-field modifier rules are covered separately by the directive policy and source-generator integration tests. Literal conversions are covered by the portable shared cases and the Avalonia constructor, numeric, list, enum and synthetic-converter differential suites. The broader [parser coverage inventory](avalonia-parser-coverage.md) tracks every public string parser in the packaged baseline and the newer source pin, with executable discovery and lowering checks.

The completion boundary is the pinned compiler's supported source surface, with XamlG's existing documented extensions retained. Those include logical relative-source trees, typed/property CanExecute predicates, native field/indexer conveniences, intrinsic references/arrays, struct objects and property-element namespace declarations. Upstream IL implementation details and unsupported upstream programs are not new implementation requirements. Explicitly tested upstream phase behavior includes detached compiled TemplatedParent rejection, non-shared handling of either x:Shared Boolean, and runtime failures for invalid decimal literals; valid decimal literals are now folded without changing their bits.

Full checkpoint counts and clean-worktree theme provenance are recorded in [upstream validation](upstream-validation.md).

The subsequent [compatibility and performance review](compatibility-review.md) checks implicit conventions and runtime integration beneath these transforms and records newly discovered differences. The inventory is a source map, not a substitute for those behavioral comparisons.
