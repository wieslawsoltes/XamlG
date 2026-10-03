using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Diagnostics;
using XamlG.Runtime.Reload;

namespace XamlG.AvaloniaRuntime;

/// <summary>Transfers interaction state only when its declaration did not change. Does not copy arbitrary application fields.</summary>
public sealed class AvaloniaInteractiveStateAdapter : IXamlStateTransferAdapter
{
    public XamlStateTransferOperation? Prepare(XamlStateTransferContext context)
    {
        if (context.Previous.Instance is not Control before || context.Candidate.Instance is not Control after) return null;
        var immediate = new List<Action>();
        var attached = new List<Action>();
        if (context.HasNoDeclaration(nameof(StyledElement.DataContext)))
        {
            var diagnostic = before.GetDiagnostic(StyledElement.DataContextProperty);
            var value = diagnostic.Value;
            if (diagnostic.Priority == BindingPriority.LocalValue && !ReferenceEquals(value, before))
                immediate.Add(() => after.SetCurrentValue(StyledElement.DataContextProperty, value));
        }
        if (before is TextBox oldText && after is TextBox newText)
        {
            var text = oldText.Text;
            var caret = oldText.CaretIndex; var start = oldText.SelectionStart; var end = oldText.SelectionEnd;
            if (context.IsDeclarationUnchanged(nameof(TextBox.Text))) immediate.Add(() => newText.SetCurrentValue(TextBox.TextProperty, text));
            attached.Add(() =>
            {
                var length = newText.Text?.Length ?? 0;
                newText.CaretIndex = Math.Min(caret, length);
                newText.SelectionStart = Math.Min(start, length);
                newText.SelectionEnd = Math.Min(end, length);
            });
        }
        if (before is ToggleButton oldToggle && after is ToggleButton newToggle && context.IsDeclarationUnchanged(nameof(ToggleButton.IsChecked)))
        {
            var value = oldToggle.IsChecked;
            immediate.Add(() => newToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, value));
        }
        if (before is RangeBase oldRange && after is RangeBase newRange && context.IsDeclarationUnchanged(nameof(RangeBase.Value)))
        {
            var value = oldRange.Value;
            immediate.Add(() => newRange.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(value, newRange.Minimum, newRange.Maximum)));
        }
        if (before is Expander oldExpander && after is Expander newExpander && context.IsDeclarationUnchanged(nameof(Expander.IsExpanded)))
        {
            var value = oldExpander.IsExpanded;
            immediate.Add(() => newExpander.SetCurrentValue(Expander.IsExpandedProperty, value));
        }
        if (before is SelectingItemsControl oldSelection && after is SelectingItemsControl newSelection &&
            context.IsDeclarationUnchanged(nameof(SelectingItemsControl.SelectedIndex)) && context.IsDeclarationUnchanged(nameof(SelectingItemsControl.SelectedItem)))
        {
            var index = oldSelection.SelectedIndex;
            attached.Add(() => { if (index >= -1 && index < newSelection.Items.Count) newSelection.SetCurrentValue(SelectingItemsControl.SelectedIndexProperty, index); });
        }
        if (before is ScrollViewer oldScroll && after is ScrollViewer newScroll && context.IsDeclarationUnchanged(nameof(ScrollViewer.Offset)))
        {
            var offset = oldScroll.Offset;
            attached.Add(() => { newScroll.UpdateLayout(); newScroll.SetCurrentValue(ScrollViewer.OffsetProperty, offset); });
        }
        var focused = before.IsFocused;
        if (focused) attached.Add(() => after.Focus());
        return new(() => { foreach (var operation in immediate) operation(); }, () =>
        {
            after.UpdateLayout();
            foreach (var operation in attached) operation();
        });
    }
}
