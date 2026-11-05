using DevTools.Providers;
using Xunit;

namespace DevTools.Tests;

public sealed class ProjectLaunchTests
{
    private static string? Nothing(string _) => null;

    private static string? Everything(string name) => "/found/" + name;

    [Theory]
    [InlineData(HostOs.Windows, "explorer.exe")]
    [InlineData(HostOs.MacOs, "open")]
    [InlineData(HostOs.Linux, "xdg-open")]
    public void FolderOpensWithTheFileManagerOfEachPlatform(HostOs os, string file)
    {
        var command = ProjectLaunch.Plan(LaunchKind.Folder, "/work/demo", os, Nothing)!;
        Assert.Equal(file, command.File);
        Assert.Equal(["/work/demo"], command.Arguments);
    }

    [Fact]
    public void WindowsTerminalIsPreferredWhenInstalled()
    {
        var command = ProjectLaunch.Plan(LaunchKind.Terminal, @"C:\Work\demo", HostOs.Windows, name => name == "wt" ? @"C:\wt.exe" : null)!;
        Assert.Equal("wt.exe", command.File);
        Assert.Equal(["-d", @"C:\Work\demo"], command.Arguments);
    }

    [Fact]
    public void WindowsFallsBackToPowerShellWithTheFolderQuotedAsALiteral()
    {
        var command = ProjectLaunch.Plan(LaunchKind.Terminal, @"C:\Work\it's demo", HostOs.Windows, Nothing)!;
        Assert.Equal("powershell.exe", command.File);
        Assert.Equal(["-NoExit", "-Command", @"Set-Location -LiteralPath 'C:\Work\it''s demo'"], command.Arguments);
    }

    [Fact]
    public void MacOpensTheTerminalApplicationAtTheFolder()
    {
        var command = ProjectLaunch.Plan(LaunchKind.Terminal, "/Users/a/demo", HostOs.MacOs, Nothing)!;
        Assert.Equal("open", command.File);
        Assert.Equal(["-a", "Terminal", "/Users/a/demo"], command.Arguments);
    }

    [Fact]
    public void LinuxHasNoPortableTerminalSoNoGuessIsMade() =>
        Assert.Null(ProjectLaunch.Plan(LaunchKind.Terminal, "/home/a/demo", HostOs.Linux, Everything));

    [Fact]
    public void EditorNeedsVsCodeOnThePath()
    {
        Assert.Null(ProjectLaunch.Plan(LaunchKind.Editor, "/work/demo", HostOs.Linux, Nothing));
        var command = ProjectLaunch.Plan(LaunchKind.Editor, "/work/demo", HostOs.Linux, Everything)!;
        Assert.Equal("/found/code", command.File);
        Assert.Equal(["/work/demo"], command.Arguments);
    }

    [Fact]
    public void WindowsEditorGoesThroughTheCmdShimBecauseCodeIsABatchFile()
    {
        var command = ProjectLaunch.Plan(LaunchKind.Editor, @"C:\Work\demo", HostOs.Windows, name => name == "code" ? @"C:\vs\bin\code.cmd" : null)!;
        Assert.Equal("cmd.exe", command.File);
        Assert.Equal(["/d", "/c", @"C:\vs\bin\code.cmd", @"C:\Work\demo"], command.Arguments);
    }

    [Theory]
    [InlineData(@"C:\Work\a&b")]
    [InlineData(@"C:\Work\a|b")]
    [InlineData(@"C:\Work\a%PATH%b")]
    [InlineData(@"C:\Work\a^b")]
    [InlineData("C:\\Work\\a\"b")]
    public void BatchLaunchersRefusePathsWithShellMetacharacters(string path) =>
        Assert.Null(ProjectLaunch.Plan(LaunchKind.Editor, path, HostOs.Windows, name => @"C:\vs\bin\code.cmd"));

    [Fact]
    public void ADirectExecutableMayHaveAnyCharactersInThePath() =>
        Assert.NotNull(ProjectLaunch.Plan(LaunchKind.Folder, @"C:\Work\a&b", HostOs.Windows, Nothing));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPathPlansNothing(string path) =>
        Assert.Null(ProjectLaunch.Plan(LaunchKind.Folder, path, HostOs.Linux, Everything));

    [Fact]
    public void StartingAMissingProgramReportsAnErrorInsteadOfThrowing()
    {
        var error = ProjectLaunch.Start(new LaunchCommand("definitely-not-a-real-program-xyz", ["x"]));
        Assert.NotNull(error);
        Assert.Contains("definitely-not-a-real-program-xyz", error);
    }
}
