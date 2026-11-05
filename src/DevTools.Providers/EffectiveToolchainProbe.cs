using DevTools.Core.Model;

namespace DevTools.Providers;

/// <summary>Opt-in probe using the app's inherited PATH and a project working directory.</summary>
public static class EffectiveToolchainProbe
{
    public static (string Command, string Argument)? Command(ToolKind tool) => tool switch
    {
        ToolKind.Node => ("node", "--version"),
        ToolKind.Jdk => ("java", "-version"),
        ToolKind.Maven => ("mvn", "-version"),
        ToolKind.Gradle => ("gradle", "--version"),
        ToolKind.DotNetSdk => ("dotnet", "--version"),
        ToolKind.Python => ("python", "--version"),
        ToolKind.Go => ("go", "version"),
        ToolKind.Rustc => ("rustc", "--version"),
        _ => null
    };

    public static (string? Version, string? Error) Run(ToolKind tool, string executable, string projectPath,
        IProcessRunner? runner = null)
    {
        var command = Command(tool);
        if (command is null) return (null, "This tool does not support effective-version probing.");
        var result = VersionProbe.Run(executable, command.Value.Argument, projectPath, runner);
        if (!result.Succeeded) return (null, result.Error ?? result.Combined.Trim());
        var version = InventoryHelpers.VersionTokenIn(result.Combined);
        return version is null ? (null, "The executable did not report a recognizable version.") : (version, null);
    }
}
