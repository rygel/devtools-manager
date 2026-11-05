using DevTools.Core.Model;
using DevTools.Providers;
using Xunit;

namespace DevTools.Tests;

public sealed class PackageOwnershipTests
{
    private static PackageOwner? FromPath(string path) => PackageOwnership.FromPath(path, _ => false);

    [Theory]
    [InlineData("/opt/homebrew/Cellar/node/22.1.0/bin/node", "brew", "node")]
    [InlineData("/home/linuxbrew/.linuxbrew/Cellar/pnpm/9.1.0/bin/pnpm", "brew", "pnpm")]
    [InlineData("/usr/local/Cellar/openjdk@21/21.0.2/bin/java", "brew", "openjdk@21")]
    public void HomebrewIsReadFromTheCellarFolder(string path, string manager, string formula)
    {
        var owner = FromPath(path);
        Assert.Equal(new PackageOwner(manager, formula), owner);
    }

    [Theory]
    [InlineData(@"C:\Users\Me\scoop\apps\nodejs\current\node.exe", "scoop", "nodejs")]
    [InlineData(@"C:\Users\Me\scoop\apps\python\3.12.4\python.exe", "scoop", "python")]
    public void ScoopIsReadFromTheAppsFolder(string path, string manager, string app) =>
        Assert.Equal(new PackageOwner(manager, app), FromPath(path));

    [Fact]
    public void ChocolateyIsReadFromTheLibFolder() =>
        Assert.Equal(new PackageOwner("choco", "nodejs-lts"),
            FromPath(@"C:\ProgramData\chocolatey\lib\nodejs-lts\tools\node.exe"));

    [Fact]
    public void AScoopShimIsFollowedToItsAppFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-shim-" + Guid.NewGuid().ToString("N"));
        try
        {
            var target = Path.Combine(root, "scoop", "apps", "nodejs", "current", "node.exe");
            var shims = Path.Combine(root, "scoop", "shims");
            Directory.CreateDirectory(shims);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(Path.Combine(shims, "node.shim"), "path = \"" + target + "\"\r\n");

            var owner = PackageOwnership.FromPath(Path.Combine(shims, "node.exe"), File.Exists);
            Assert.Equal(new PackageOwner("scoop", "nodejs"), owner);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScoopShimTargetIsReadFromItsPathLine()
    {
        const string contents = "path = \"C:\\Users\\Me\\scoop\\apps\\nodejs\\current\\node.exe\"\r\nshim = \"...\"\r\n";
        Assert.Equal(@"C:\Users\Me\scoop\apps\nodejs\current\node.exe", PackageOwnership.ParseShimTarget(contents));
    }

    [Fact]
    public void AnUnmanagedPathIsNotClaimedByAnyone() =>
        Assert.Null(FromPath(@"C:\Program Files\nodejs\node.exe"));

    [Theory]
    [InlineData("nodejs:amd64: /usr/bin/node\n", "nodejs")]
    [InlineData("libc6:amd64: /lib/x86_64-linux-gnu/libc.so.6\n", "libc6")]
    public void DpkgPackageIsTheNameBeforeTheFirstColon(string output, string package) =>
        Assert.Equal(package, PackageOwnership.ParseDpkg(output));

    [Fact]
    public void DpkgWithNoOwnerGivesNothing() =>
        Assert.Null(PackageOwnership.ParseDpkg("dpkg-query: no path found matching pattern /opt/x\n"));

    [Fact]
    public void RpmPackageIsTheFirstLine() =>
        Assert.Equal("nodejs-22.1.0-1.fc41.x86_64", PackageOwnership.ParseRpm("nodejs-22.1.0-1.fc41.x86_64\n"));

    [Fact]
    public void RpmSayingNotOwnedGivesNothing() =>
        Assert.Null(PackageOwnership.ParseRpm("file /opt/x is not owned by any package\n"));

    [Fact]
    public void PacmanReadsTheOwningPackage() =>
        Assert.Equal("nodejs", PackageOwnership.ParsePacman("/usr/bin/node is owned by nodejs 22.1.0-1\n"));

    [Fact]
    public void PacmanWithNoOwnerGivesNothing() =>
        Assert.Null(PackageOwnership.ParsePacman("error: No package owns /opt/x\n"));

    [Theory]
    [InlineData(ToolKind.Jdk, "java")]
    [InlineData(ToolKind.Rustc, "rustc")]
    [InlineData(ToolKind.DotNetSdk, "dotnet")]
    [InlineData(ToolKind.Node, "node")]
    public void EachToolIsProbedUnderItsOwnExecutableName(ToolKind tool, string executable) =>
        Assert.Equal(executable, UpdatePlanner.ExecutableFor(tool));

    [Fact]
    public void AHomebrewOwnedToolUsesHomebrewsUpgrade()
    {
        var toolchain = new Toolchain { Tool = ToolKind.Node, Version = "22.1.0", Path = "/x", Manager = "system" };
        var step = UpdatePlanner.OwnedStep(toolchain, "Node.js", new PackageOwner("brew", "node"));
        Assert.Equal("brew", step.FileName);
        Assert.Equal(["upgrade", "node"], step.Arguments);
    }

    [Fact]
    public void AScoopOwnedToolUsesScoopUpdate()
    {
        var toolchain = new Toolchain { Tool = ToolKind.Node, Version = "22.1.0", Path = "/x", Manager = "system" };
        var step = UpdatePlanner.OwnedStep(toolchain, "Node.js", new PackageOwner("scoop", "nodejs"));
        Assert.Equal("scoop", step.FileName);
        Assert.Equal(["update", "nodejs"], step.Arguments);
    }

    [Theory]
    [InlineData("apt", "sudo apt-get")]
    [InlineData("dnf", "sudo dnf")]
    [InlineData("pacman", "sudo pacman")]
    [InlineData("choco", "choco upgrade")]
    public void PackagesNeedingRootOrElevationAreManualWithTheCommandShown(string manager, string expected)
    {
        var toolchain = new Toolchain { Tool = ToolKind.Node, Version = "22.1.0", Path = "/x", Manager = "system" };
        var step = UpdatePlanner.OwnedStep(toolchain, "Node.js", new PackageOwner(manager, "nodejs"));
        Assert.True(step.Manual);
        Assert.Contains(expected, step.Hint);
    }

    [Theory]
    [InlineData("Python311", "3.11")]
    [InlineData("Python313", "3.13")]
    [InlineData("Launcher", null)]
    [InlineData("3.11", null)]
    public void PythonInstallFolderNamesAreReadAsMajorDotMinor(string folder, string? version) =>
        Assert.Equal(version, PythonInstallFolders.Parse(folder));
}
