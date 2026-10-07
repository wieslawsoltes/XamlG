namespace XamlG.Frameworks.Avalonia;

/// <summary>Versioned Avalonia metadata identities; the portable compiler never references these names.</summary>
public static class AvaloniaMetadata
{
    public const string Object = "Avalonia.AvaloniaObject";
    public const string Property = "Avalonia.AvaloniaProperty";
    public const string PropertySuffix = "Property";
    public const string XmlnsDefinition = "Avalonia.Metadata.XmlnsDefinitionAttribute";
    public const string Content = "Avalonia.Metadata.ContentAttribute";
    public const string DeferredContent = "Avalonia.Metadata.TemplateContentAttribute";
    public const string WhitespaceSignificant = "Avalonia.Metadata.WhitespaceSignificantCollectionAttribute";
    public const string TrimSurroundingWhitespace = "Avalonia.Metadata.TrimSurroundingWhitespaceAttribute";
    public const string UsableDuringInitialization = "Avalonia.Metadata.UsableDuringInitializationAttribute";
    public const string AssignBinding = "Avalonia.Data.AssignBindingAttribute";
    public const string AddChild = "Avalonia.Metadata.IAddChild";
    public const string AddChildGeneric = "Avalonia.Metadata.IAddChild`1";
    public const string Loader = "Avalonia.Markup.Xaml.AvaloniaXamlLoader";
    public const string Load = "Load";
    public const string DesignNamespace = "http://schemas.microsoft.com/expression/blend/2008";
    public const string RootProvider = "Avalonia.Markup.Xaml.IRootObjectProvider";
    public const string ValueTarget = "Avalonia.Markup.Xaml.IProvideValueTarget";
    public const string UriContext = "Avalonia.Markup.Xaml.IUriContext";
    public const string RuntimeNamespace = "Avalonia.Markup.Xaml.XamlIl.Runtime.";
    public const string RuntimeHelpers = RuntimeNamespace + "XamlIlRuntimeHelpers";
    public const string ParentProvider = RuntimeNamespace + "IAvaloniaXamlIlParentStackProvider";
    public const string NamespaceProvider = RuntimeNamespace + "IAvaloniaXamlIlXmlNamespaceInfoProvider";
    public const string NamespaceItem = RuntimeNamespace + "AvaloniaXamlIlXmlNamespaceInfo";
    public const string NameScope = "Avalonia.Controls.NameScope";
    public const string NameScopeContract = "Avalonia.Controls.INameScope";
    public const string Control = "Avalonia.Controls.Control";
    public const string BindingBase = "Avalonia.Data.BindingBase";
    public const string BindingAdapter = "XamlG.AvaloniaRuntime.AvaloniaBindingAdapter";
    public const string ApplyBinding = "Apply";
    public const string BrushContract = "Avalonia.Media.IBrush";
    public const string Brush = "Avalonia.Media.Brush";
    public const string TransformContract = "Avalonia.Media.ITransform";
    public const string Transform = "Avalonia.Media.Transform";
    public const string RowDefinitions = "Avalonia.Controls.RowDefinitions";
    public const string ColumnDefinitions = "Avalonia.Controls.ColumnDefinitions";
}
