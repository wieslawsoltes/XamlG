namespace XamlG.IntelligentUI;

public sealed partial class UiAutomation
{
    private static object Discovery(UiSessionStore store) => new
    {
        components = store.Compiler.Catalog.Components.Values,
        composites = UiCompositeCatalog.Components.Values.OrderBy(component => component.Name, StringComparer.Ordinal),
        namespaces = new { avalonia = UiCatalog.AvaloniaNamespace, ui = UiCatalog.UiNamespace },
        limits = store.Compiler.Limits,
        syntax = "Avalonia namespace; ui namespace urn:xamlg:intelligent-ui. ui:Key, ui:Bind, ui:Action, ui:When, ui:Each + ui:ItemKey. Expressions: {ui:Expr state.value * data.price}. No arbitrary C#/XAML execution. Text requires string expressions. Actions of kind state atomically replace declared state keys from Arguments; each {ui:Expr ...} reads pre-action state. A repeated action may use item to reference its current innermost keyed row. External actions require separate review. Composites use ui: tags, for example ui:Card, ui:Metric, ui:Table/ui:TableRow and ui:BarChart/ui:DataPoint. Card Space, Gap and Radius use a 4-pixel scale. Use stable item keys for repeated table rows and chart points.",
        forms = "Use ui:Form with ui:Field wrapping exactly one registered native input, ui:ValidationSummary and ui:SubmitButton with ui:Action. IsRequired checks the typed value; Field.IsValid and Form.IsValid accept boolean expressions. ErrorText supplies explanations; ShowErrors changes display only. Invalid active fields disable submit without disabling inputs or ordinary reset Buttons. Hidden, disabled, collapsed and inactive-tab fields do not block submission. Nested forms are rejected. Form validity and checkboxes never grant host permissions or imply approval of external effects.",
        drawing = new
        {
            matrix = "RenderTransform and LayoutTransform use matrix(m11,m12,m21,m22,offsetX,offsetY) or none. Coefficients are finite invariant numbers between -1000000 and 1000000. General CSS transform functions are not accepted.",
            origin = "RenderTransformOrigin has two absolute coordinates or two percentages, for example 0,0 or 50%,50%. Do not mix units.",
            geometry = "Path.Data and Clip accept bounded M/L/H/V/C/S/Q/T/A/Z path commands, relative forms and repeated argument groups, with optional F0/F1 fill prefix. Arc radii are nonnegative; arc flags are 0 or 1. Polygon/Polyline Points are complete x,y pairs.",
            pen = "StrokeDashArray contains at most 64 nonnegative numbers; a nonempty pattern needs a positive length. Dash lengths and StrokeDashOffset are multiples of StrokeThickness.",
            limits = new { points = UiDrawingValues.MaximumPoints, pathSegments = UiDrawingValues.MaximumPathSegments, text = UiDrawingValues.MaximumDrawingText },
            layout = "One native response root receives the available viewport. Use Grid star rows/columns with ScrollViewer for constrained scrolling. Multiple roots retain vertical flow. LayoutTransformControl participates in native measure; RenderTransform affects rendering.",
            assets = "FontFamily accepts local family names only. Model-authored asset URIs, external fonts, images and executable property values are not enabled. Registered data/control templates use the typed authoring contracts below.",
            portability = "Desktop and Avalonia/Wasm render actual Avalonia controls. Portable MCP HTML projects the registered properties to CSS/SVG; it is not a pixel-identical Avalonia layout engine."
        },
        authoring = new
        {
            resources = "Inline Resources and merged ResourceDictionary values resolve in declaration scope. StaticResource and DynamicResource resolve during revisioned publication; this is not a live arbitrary CLR resource graph.",
            styles = "Scoped Styles with typed presentation Setters, control/class/name selectors and registered pseudoclasses. Nested ^ selectors preserve scope. Local properties override styles. Child/descendant/template combinators, :is, :not and bounded positional selectors are supported. Source selector groups validate each typed target. Arbitrary style includes and behavior-changing setters are not enabled.",
            selectors = new
            {
                maximumSteps = UiStyleSelectors.MaximumSteps, maximumPredicates = UiStyleSelectors.MaximumPredicates,
                maximumGroups = UiStyleSelectors.MaximumGroups, maximumCharacters = 512,
                syntax = "Final targets require a registered type. Logical descendants use whitespace; children use >; native template parts use /template/. :is(Button) includes derived registered controls; :not(predicate) negates a compound predicate. :nth-child/:nth-last-child use bounded An+B, odd or even. Nested ^ rules retain their owning scope.",
                portability = "Portable selectors count authored siblings, not incidental DOM slots. Template selectors never target ordinary response children. Native template-part styling requires the Avalonia guest. Both evaluators enforce independent work/output budgets."
            },
            inputs = new
            {
                navigationModes = new[] { "Continue", "Cycle", "Contained", "Once", "None", "Local" }, maximumTabIndex = 32767,
                syntax = "Use IsTabStop, TabIndex and KeyboardNavigation.TabNavigation for keyboard groups. AutomationProperties.AutomationId/HelpText are explicit accessibility metadata. CheckBox/ToggleButton/ToggleSwitch preserve null and support IsThreeState; RadioButton retains native select-only activation. TextBox caret/selection declarations do not reset on unrelated state echoes. Slider SmallChange/LargeChange/IsDirectionReversed control native keyboard changes. RepeatButton Delay/Interval are bounded milliseconds.",
                actions = "IsDefault and IsCancel require declared actions; they confer no tool permission. Portable default/cancel dispatch commits the focused draft first. Portable RepeatButton awaits each local-state action result; external effects still require ordinary review and do not repeat. Focus ordering in native control templates remains authoritative in Avalonia."
            },
            controlTemplates = "ControlTemplate constructs only registered typed parts in a private NameScope. ControlTheme supports typed TargetType, BasedOn, presentation setters and template parts. TemplateBinding supports registered OneWay properties or TwoWay registered input pairs; callbacks, arbitrary CLR construction and action/state directives inside parts are not permitted. ContentPresenter/ItemsPresenter connect the native owner's content/items. Portable HTML uses ordinary control fallback, not native theme/template construction.",
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
        inputExample = UiInputExamples.Controls(),
        authoringExample = UiAuthoringExamples.Directory(),
        example = UiExamples.Pricing(),
        localActions = UiInteractionExamples.Counter(),
        dashboard = UiRichExamples.Dashboard(),
        form = UiFormExamples.Configuration(),
        drawingExample = UiDrawingExamples.LayoutAndDrawing()
    };
}
