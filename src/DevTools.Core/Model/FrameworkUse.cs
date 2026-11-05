namespace DevTools.Core.Model;

/// <summary>
/// An application framework a project declares (Spring Boot, React, Django, …). Unlike a
/// <see cref="Toolchain"/> it is not installed on the machine and nothing here manages it: it is
/// a dependency of the project, reported with the version the manifest declares.
/// </summary>
public sealed class FrameworkUse
{
    /// <summary>Stable lower-case identifier, e.g. "spring-boot".</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public Ecosystem Ecosystem { get; set; }

    /// <summary>The version or range exactly as the manifest declares it; null when it declares none.</summary>
    public string? Version { get; set; }

    public Provenance Provenance { get; set; } = new();

    public string Describe() => Version is null ? Name : Name + " " + Version;
}
