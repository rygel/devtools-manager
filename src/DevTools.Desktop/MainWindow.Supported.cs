using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DevTools.Core.Model;

namespace DevTools.Desktop;

public sealed partial class MainWindow
{
    private readonly TextBox _supportFilter = new()
    {
        Name = "SupportFilter",
        PlaceholderText = "Filter tools, frameworks and ecosystems",
        MaxWidth = 420,
        HorizontalAlignment = HorizontalAlignment.Left
    };

    private readonly StackPanel _supportBody = new() { Spacing = 0 };

    private readonly TextBlock _supportCount = Ui.Dim(null, 12);

    /// <summary>The search stays pinned above the list so it is reachable however far the list is scrolled.</summary>
    private Grid BuildSupportedPage()
    {
        _supportFilter.PlaceholderText = "Search tools, frameworks, ecosystems or marker files (for example pom.xml)";
        _supportFilter.MaxWidth = 560;
        AutomationProperties.SetName(_supportFilter, "Search supported tools and frameworks");
        _supportFilter.TextChanged += (_, _) => PopulateSupported();
        _supportFilter.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape || string.IsNullOrEmpty(_supportFilter.Text)) return;
            _supportFilter.Text = "";
            e.Handled = true;
        };
        PopulateSupported();

        var searchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Margin = new Thickness(0, 0, 0, 12),
            Children = { _supportFilter, _supportCount }
        };
        _supportCount.VerticalAlignment = VerticalAlignment.Center;

        var scroller = new ScrollViewer
        {
            Content = new StackPanel { Margin = new Thickness(0, 0, 20, 24), Children = { _supportBody } }
        };
        return new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { searchRow, WithRow(scroller, 1) }
        };
    }

    private void PopulateSupported()
    {
        _supportBody.Children.Clear();
        var groups = SupportCatalog.Build(_supportFilter.Text);
        var tools = groups.Sum(group => group.Tools.Count);
        var frameworks = groups.Sum(group => group.Frameworks.Count);
        _supportCount.Text = string.IsNullOrWhiteSpace(_supportFilter.Text)
            ? ""
            : $"{tools} toolchains · {frameworks} frameworks · {groups.Count} ecosystems";
        if (groups.Count == 0)
        {
            _supportBody.Children.Add(Ui.Empty("Nothing matches", "Try a tool name such as Maven, a framework such as React, or an ecosystem such as Python."));
            return;
        }

        for (var index = 0; index < groups.Count; index++)
        {
            if (index > 0) _supportBody.Children.Add(new Border { Height = 1, Background = Palette.Brush(Palette.Line), Margin = new Thickness(0, 18) });
            _supportBody.Children.Add(SupportGroupView(groups[index]));
        }
    }

    private static StackPanel SupportGroupView(SupportGroup group)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Ui.Text(group.Title, 16, bold: true, wrap: false));
        if (group.Markers.Count > 0)
            stack.Children.Add(Ui.Dim("Detected by " + string.Join("  ·  ", group.Markers), 11, mono: true));

        if (group.Tools.Count > 0)
        {
            stack.Children.Add(Ui.Label("Toolchains"));
            var tiles = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var tool in group.Tools) tiles.Children.Add(SupportToolTile(tool));
            stack.Children.Add(tiles);
        }

        if (group.Frameworks.Count > 0)
        {
            stack.Children.Add(Ui.Label("Frameworks"));
            var chips = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var framework in group.Frameworks)
            {
                var chip = Ui.Chip(framework.Name);
                chip.Margin = new Thickness(0, 0, 6, 6);
                chips.Children.Add(chip);
            }
            stack.Children.Add(chips);
        }

        return stack;
    }

    private static Border SupportToolTile(SupportedTool support)
    {
        var parts = new List<string>();
        if (support.Discovered) parts.Add("found on this machine");
        if (support.Installable) parts.Add("installable");
        var caption = parts.Count == 0 ? "version read from project files" : string.Join("  ·  ", parts);

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            Ui.Text(ToolIcons.Title(support.Tool), 13, bold: true, wrap: false),
            Ui.Dim(caption, 11, wrap: false)
        }};
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { ToolIcons.Tile(support.Tool, 32), text } };
        var tile = new Border
        {
            Width = 252,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(8),
            Background = Palette.Brush(Palette.Raised),
            Child = row
        };
        AutomationProperties.SetName(tile, ToolIcons.Title(support.Tool) + ", " + caption);
        return tile;
    }
}
