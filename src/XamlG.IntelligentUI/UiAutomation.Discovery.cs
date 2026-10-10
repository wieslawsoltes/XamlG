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
            assets = "FontFamily accepts local family names only. Model-authored asset URIs, external fonts, images, arbitrary templates and executable property values are not enabled.",
            portability = "Desktop and Avalonia/Wasm render actual Avalonia controls. Portable MCP HTML projects the registered properties to CSS/SVG; it is not a pixel-identical Avalonia layout engine."
        },
        example = UiExamples.Pricing(),
        localActions = UiInteractionExamples.Counter(),
        dashboard = UiRichExamples.Dashboard(),
        form = UiFormExamples.Configuration(),
        drawingExample = UiDrawingExamples.LayoutAndDrawing()
    };
}
