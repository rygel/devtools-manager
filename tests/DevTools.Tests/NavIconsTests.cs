using Avalonia.Media;
using DevTools.Desktop;
using Xunit;

namespace DevTools.Tests;

[Collection("Avalonia")]
public sealed class NavIconsTests
{
    [Theory]
    [InlineData("Projects")]
    [InlineData("Toolchains")]
    [InlineData("Disk")]
    [InlineData("Migration")]
    [InlineData("Supported")]
    [InlineData("WSL")]
    [InlineData("Settings")]
    public void EveryNavigationPageHasAnIcon(string page)
    {
        AvaloniaTestHost.Ensure();
        Assert.NotNull(NavIcons.Create(page, Brushes.White));
    }

    [Fact]
    public void AnUnknownNameGivesNoIconInsteadOfAnInventedOne()
    {
        AvaloniaTestHost.Ensure();
        Assert.Null(NavIcons.Create("Nonexistent", Brushes.White));
    }
}
