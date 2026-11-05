using Avalonia.Controls;

namespace DevTools.Desktop;

/// <summary>A list whose group headers are display-only: they cannot be clicked, focused or selected.</summary>
public sealed class GroupedListBox : ListBox
{
    // Without this the subclass misses the theme's ListBox template and renders nothing.
    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        var header = item is ToolGroupHeader;
        container.IsHitTestVisible = !header;
        container.Focusable = !header;
        // Compact: the theme default padding is sized for two-line rows.
        if (container is ListBoxItem listItem)
        {
            listItem.Padding = header ? new Avalonia.Thickness(10, 2) : new Avalonia.Thickness(10, 3);
            listItem.Margin = new Avalonia.Thickness(0);
        }
    }
}
