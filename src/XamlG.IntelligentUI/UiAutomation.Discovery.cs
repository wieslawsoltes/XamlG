namespace XamlG.IntelligentUI;

public sealed partial class UiAutomation
{
    private static object Discovery(UiSessionStore store) => new
    {
        components = store.Compiler.Catalog.Components.Values,
        composites = UiCompositeCatalog.Components.Values.OrderBy(component => component.Name, StringComparer.Ordinal),
        namespaces = new { avalonia = UiCatalog.AvaloniaNamespace, ui = UiCatalog.UiNamespace },
        limits = store.Compiler.Limits,
        drawing = new
        {
            matrix = "RenderTransform and LayoutTransform: none or matrix(m11,m12,m21,m22,m31,m32); six finite coefficients between -1000000 and 1000000.",
            origin = "RenderTransformOrigin: x,y in pixels or x%,y% with matching coordinate units. Native default is the control's Avalonia default.",
            geometry = "Path.Data and Clip use bounded Avalonia path text: M L H V C S Q T A Z (absolute/relative), optional leading F0/F1 fill rule; at most 512 segments and 16384 characters. Arc flags are 0 or 1 and radii are nonnegative.",
            points = "Polygon/Polyline Points are comma/space-separated coordinate pairs, at most 512 points. StrokeDashArray has at most 64 nonnegative values and cannot be all zero. Dashes and offsets are multiples of pen thickness.",
            fonts = "FontFamily accepts local family names, not asset URIs. Native typography and layout use Avalonia; portable CSS/SVG is a functional projection, not pixel-identical rendering.",
            portability = "Native desktop and Wasm use the same registered controls. LayoutTransformControl is projected through transformed natural dimensions in the portable resource; Avalonia remains authoritative for constrained layout and template behavior."
        },
        drawingExample = UiDrawingExamples.LayoutAndDrawing(),
        syntax = "Avalonia namespace; ui namespace urn:xamlg:intelligent-ui. ui:Key, ui:Bind, ui:Action, ui:When, ui:Each + ui:ItemKey. Expressions: {ui:Expr state.value * data.price}. No arbitrary C#/XAML execution. Text requires string expressions. Actions of kind state atomically replace declared state keys from Arguments; each {ui:Expr ...} reads pre-action state. A repeated action may use item to reference its current innermost keyed row. External actions require separate review. Composites use ui: tags, for example ui:Card, ui:Metric, ui:Table/ui:TableRow and ui:BarChart/ui:DataPoint. Card Space, Gap and Radius use a 4-pixel scale. Use stable item keys for repeated table rows and chart points.",
        forms = "Use ui:Form with ui:Field wrapping exactly one registered native input, ui:ValidationSummary and ui:SubmitButton with ui:Action. IsRequired checks the typed value; Field.IsValid and Form.IsValid accept boolean expressions. ErrorText supplies explanations; ShowErrors changes display only. Invalid active fields disable submit without disabling inputs or ordinary reset Buttons. Hidden, disabled, collapsed and inactive-tab fields do not block submission. Nested forms are rejected. Form validity and checkboxes never grant host permissions or imply approval of external effects.",
        authoring = new
        {
            resources = "Inline Resources and merged ResourceDictionary values resolve in declaration scope. StaticResource and DynamicResource resolve during revisioned publication; this is not a live arbitrary CLR resource graph.",
            styles = "Scoped Styles with typed presentation Setters, control/class/name selectors and registered pseudoclasses. Nested ^ selectors preserve scope. Local properties override styles. Control templates, selector combinators and arbitrary style includes are not enabled.",
            bindings = "Binding and CompiledBinding walk JSON paths, indices and quoted object keys. Default scope is data; DataContext inherits a JSON scope; templates bind their item. Explicit state/data/item roots remain available. Modes Default and OneWay are supported; a registered input may bind TwoWay to exactly one declared state slot. FallbackValue, TargetNullValue and bounded invariant StringFormat are supported. CompiledBinding here validates JSON syntax, not CLR x:DataType bindings. No arbitrary converters, source objects, reflection or effects.",
            templates = "Inline or StaticResource DataTemplate for ItemsControl/ListBox/ComboBox ItemTemplate and content-control ContentTemplate. Provide stable ui:ItemKey for object rows. Content may be structured JSON. Keys are scoped per template instance and actions capture owned current item data. Templates expand within tree budgets and are not virtualized native data templates.",
            brushes = new
            {
                kinds = new[] { "solid", "linear", "radial" }, maximumStops = UiBrushValues.MaximumStops,
                maximumCharacters = UiBrushValues.MaximumCharacters, maximumSurfaceGradients = 512,
                syntax = "Brush properties accept color strings, null, or objects with kind, opacity, transform and transformOrigin. Gradients have ordered stops [{color,offset}], spreadMethod Pad/Reflect/Repeat, and linear startPoint/endPoint or radial center/gradientOrigin/radiusX/radiusY. XAML SolidColorBrush/LinearGradientBrush/RadialGradientBrush and GradientStop property elements lower to the same data. Bind the whole brush for reactive replacement.",
                portability = "Native Avalonia and static XAML preserve brush semantics. Portable SVG paints and CSS backgrounds are projections; CSS text/borders fall back to the first gradient stop. Mixed units, brush transforms and nonsquare background geometry need native rendering for exact fidelity. No image assets or remote brush loading."
            }
        },
        authoringExample = UiAuthoringExamples.Directory(),
        example = UiExamples.Pricing(),
        localActions = UiInteractionExamples.Counter(),
        dashboard = UiRichExamples.Dashboard(),
        form = UiFormExamples.Configuration()
    };
}
