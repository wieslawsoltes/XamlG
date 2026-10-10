using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Styling;

namespace XamlG.IntelligentUI.Avalonia;

/// <summary>Constructs real framework templates/themes from independently validated descriptors.
/// Each renderer entry owns its template factories, created controls and binding subscriptions.</summary>
internal sealed class UiAvaloniaControlAuthoring : IDisposable
{
    private readonly List<OwnedTemplate> _templates = [];
    private OwnedTemplate? _template;
    private ControlTheme? _theme;
    private bool _disposed;

    internal static UiAvaloniaControlAuthoring Create(Control owner, UiElement node, UiAvaloniaCatalog catalog)
    {
        var result = new UiAvaloniaControlAuthoring();
        try
        {
            OwnedTemplate Template(UiControlTemplate description)
            {
                var template = new OwnedTemplate(description, owner.GetType(), catalog);
                result._templates.Add(template); return template;
            }
            if (node.ControlTemplate != null) result._template = Template(node.ControlTemplate);
            if (node.ControlTheme is { } description)
            {
                var theme = new ControlTheme { TargetType = owner.GetType() };
                var values = UiAvaloniaStyles.Create([new UiStyleRule(node.Type, description.Properties)], catalog).Single();
                foreach (var setter in values.Setters) theme.Setters.Add(setter);
                if (description.Template != null) theme.Setters.Add(new Setter(TemplatedControl.TemplateProperty, Template(description.Template).Template));
                foreach (var style in UiAvaloniaStyles.Create(description.Styles, catalog, nesting: true)) theme.Children.Add(style);
                result._theme = theme;
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    internal void Apply(Control owner)
    {
        if (owner is TemplatedControl templated)
        {
            if (_template == null) templated.ClearValue(TemplatedControl.TemplateProperty);
            else templated.Template = _template.Template;
        }
        if (_theme == null) owner.ClearValue(StyledElement.ThemeProperty); else owner.Theme = _theme;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var template in _templates) template.Dispose();
        _templates.Clear(); _theme = null; _template = null;
    }
    private sealed class OwnedTemplate : IDisposable
    {
        private readonly UiAvaloniaCatalog _catalog;
        private readonly Plan _plan;
        private List<(Control Control, UiControlRegistration Registration)> _controls = [];
        private List<IDisposable> _bindings = [];
        private bool _disposed;
        internal FuncControlTemplate Template { get; }
        internal OwnedTemplate(UiControlTemplate descriptor, Type ownerType, UiAvaloniaCatalog catalog)
        {
            _catalog = catalog;
            _plan = Prepare(descriptor.Root, ownerType);
            Template = new FuncControlTemplate((_, scope) => Build(scope));
        }
        private Plan Prepare(UiControlTemplateNode node, Type owner)
        {
            var registration = _catalog.Registrations[node.Type]; var probe = registration.Create();
            try
            {
                foreach (var property in node.Properties) registration.Setters[property.Key](probe, property.Value);
                var bindings = node.Bindings.Select(pair =>
                {
                    var target = Property(probe.GetType(), pair.Key); var source = Property(owner, pair.Value.Property);
                    if (!target.PropertyType.IsAssignableFrom(source.PropertyType)) throw new UiException("invalid_template", "Native TemplateBinding property types disagree.");
                    return new NativeBinding(target, source, pair.Value.Mode == "TwoWay" ? BindingMode.TwoWay : BindingMode.OneWay);
                }).ToImmutableArray();
                // Conversion and selector failures are detected during detached preparation.
                var styles = UiAvaloniaStyles.Create(node.Styles, _catalog);
                return new(node, registration, bindings, styles, node.Children.Select(child => Prepare(child, owner)).ToImmutableArray());
            }
            finally { registration.Retire?.Invoke(probe); }
        }
        private static AvaloniaProperty Property(Type type, string name) =>
            AvaloniaPropertyRegistry.Instance.GetRegistered(type).FirstOrDefault(property => property.Name == name && !property.IsReadOnly)
            ?? throw new UiException("invalid_template", "TemplateBinding needs a registered native property: " + name);
        private Control Build(INameScope scope)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A native template belongs to one renderer entry. Framework reapplication retires
            // the previous generated tree, never any content owned by the response renderer.
            Release();
            Control Create(Plan plan)
            {
                var control = plan.Registration.Create(); _controls.Add((control, plan.Registration));
                foreach (var property in plan.Node.Properties.OrderBy(p => p.Key == "Minimum" ? 0 : p.Key == "Maximum" ? 1 : p.Key == "Value" ? 3 : 2))
                    plan.Registration.Setters[property.Key](control, property.Value);
                if (control.Name is { } name) scope.Register(name, control);
                foreach (var style in plan.Styles) control.Styles.Add(style);
                foreach (var binding in plan.Bindings)
                    _bindings.Add(control.Bind(binding.Target, new TemplateBinding(binding.Source) { Mode = binding.Mode }));
                var children = plan.Children.Select(Create).ToArray();
                UiAvaloniaRenderer.Children(control, children,
                    plan.Node.Properties.ContainsKey("ItemsSource") || plan.Node.Bindings.ContainsKey("ItemsSource"),
                    plan.Node.Properties.ContainsKey("Content") || plan.Node.Bindings.ContainsKey("Content"));
                return control;
            }
            try { return Create(_plan); }
            catch { Release(); throw; }
        }
        private void Release()
        {
            // Dispose parent bindings before clearing containers: ContentPresenter.Content may
            // refer to a retained response child which this template must never retire.
            foreach (var binding in _bindings) binding.Dispose(); _bindings.Clear();
            foreach (var item in _controls.AsEnumerable().Reverse())
            {
                UiAvaloniaRenderer.Children(item.Control, [], item.Control is ItemsControl items && items.ItemsSource != null, false);
                item.Control.Styles.Clear(); item.Registration.Retire?.Invoke(item.Control);
            }
            _controls.Clear();
        }
        public void Dispose() { if (_disposed) return; _disposed = true; Release(); }
        private sealed record NativeBinding(AvaloniaProperty Target, AvaloniaProperty Source, BindingMode Mode);
        private sealed record Plan(UiControlTemplateNode Node, UiControlRegistration Registration, ImmutableArray<NativeBinding> Bindings,
            ImmutableArray<Style> Styles, ImmutableArray<Plan> Children);
    }
}
