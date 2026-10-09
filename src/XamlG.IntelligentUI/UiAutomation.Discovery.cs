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
        example = UiExamples.Pricing(),
        localActions = UiInteractionExamples.Counter(),
        dashboard = UiRichExamples.Dashboard(),
        form = UiFormExamples.Configuration()
    };
}
