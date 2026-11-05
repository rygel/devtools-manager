using DevTools.Core.Model;
using DevTools.Scan;
using DevTools.Scan.Parsers;
using Xunit;

namespace DevTools.Tests;

public class NodeParserTests
{
    [Fact]
    public void NvmrcBecomesAPinnedRequirement()
    {
        var fs = new InMemoryProjectFileSystem().File(".nvmrc", "18.20.2\n");
        var context = ProjectContextFactory.Create(fs);

        new NodeParser().Extract(context);

        var node = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Node);
        Assert.Equal("18.20.2", node.Raw);
        Assert.Equal(RequirementStrength.Pinned, node.Strength);
        Assert.Equal(".nvmrc", node.Provenance.FilePath);
        Assert.Equal(1, node.Provenance.Line);
    }

    [Fact]
    public void PackageJsonYieldsEnginesManagerAndVolta()
    {
        var fs = new InMemoryProjectFileSystem().File("package.json", """
        {
          "name": "acme-api",
          "packageManager": "pnpm@8.15.4",
          "engines": { "node": ">=18 <21" },
          "volta": { "node": "20.11.0" }
        }
        """);

        var context = ProjectContextFactory.Create(fs);
        new NodeParser().Extract(context);

        Assert.Equal("acme-api", context.Name);

        var engines = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Node && r.Strength == RequirementStrength.Advisory);
        Assert.Equal(">=18 <21", engines.Raw);

        var manager = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Pnpm);
        Assert.Equal("8.15.4", manager.Raw);
        Assert.Equal(RequirementStrength.Declared, manager.Strength);

        var volta = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Node && r.Strength == RequirementStrength.Pinned);
        Assert.Equal("20.11.0", volta.Raw);
    }

    [Fact]
    public void LockfileMarksThePackageManagerInUse()
    {
        var fs = new InMemoryProjectFileSystem()
            .File("package.json", """{"name":"x"}""")
            .File("pnpm-lock.yaml", "lockfileVersion: 6");

        var context = ProjectContextFactory.Create(fs);
        new NodeParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Pnpm);
        Assert.Contains("pnpm-lock.yaml", context.Lockfiles);
    }

    [Fact]
    public void ToolVersionsFilePinsEveryListedTool()
    {
        var fs = new InMemoryProjectFileSystem().File(".tool-versions", """
        nodejs 20.11.0
        java temurin-21.0.2+13
        """);

        var context = ProjectContextFactory.Create(fs);
        new VersionPinParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Node && r.Raw == "20.11.0");
        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Jdk && r.Raw == "temurin-21.0.2+13");
    }
}

public class JavaParserTests
{
    [Fact]
    public void ResolvesAppliedKotlinPluginAliasFromGradleCatalog()
    {
        var fs = new InMemoryProjectFileSystem()
            .File("build.gradle.kts", "plugins {\n    alias(libs.plugins.kotlin.jvm)\n}")
            .File("gradle/libs.versions.toml", "[versions]\nkotlin = \"2.1.20\" # tool version\n[plugins]\nkotlin-jvm = { id = \"org.jetbrains.kotlin.jvm\", version.ref = \"kotlin\" }\n");
        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);
        var kotlin = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Kotlin);
        Assert.Equal("2.1.20", kotlin.Raw);
        Assert.Equal("gradle/libs.versions.toml", kotlin.Provenance.FilePath);
        Assert.Equal(2, kotlin.Provenance.Line);
    }

    [Fact]
    public void DoesNotResolveUnusedCatalogEntries()
    {
        var fs = new InMemoryProjectFileSystem()
            .File("build.gradle.kts", "plugins { }\n")
            .File("gradle/libs.versions.toml", "[plugins]\nkotlin-jvm = { id = \"org.jetbrains.kotlin.jvm\", version = \"2.1.20\" }\n");
        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);
        Assert.DoesNotContain(context.Requirements, r => r.Tool == ToolKind.Kotlin);
    }

    [Fact]
    public void UnresolvedKotlinCatalogReferenceMarksProjectIncomplete()
    {
        var fs = new InMemoryProjectFileSystem()
            .File("build.gradle.kts", "plugins {\n alias(libs.plugins.kotlin.jvm)\n}")
            .File("gradle/libs.versions.toml", "[plugins]\nkotlin-jvm = { id = \"org.jetbrains.kotlin.jvm\", version.ref = \"missing\" }\n");
        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);
        Assert.Contains(context.Diagnostics, d => d.Code == "catalog-version-unresolved");
        Assert.DoesNotContain(context.Requirements, r => r.Tool == ToolKind.Kotlin);
    }

    [Fact]
    public void PomReleaseBecomesAJavaRequirement()
    {
        var fs = new InMemoryProjectFileSystem().File("pom.xml", """
        <?xml version="1.0" encoding="UTF-8"?>
        <project xmlns="http://maven.apache.org/POM/4.0.0">
          <groupId>com.acme</groupId>
          <artifactId>acme-api</artifactId>
          <properties>
            <maven.compiler.release>17</maven.compiler.release>
          </properties>
        </project>
        """);

        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);

        var jdk = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Jdk);
        Assert.Equal("17", jdk.Raw);
        Assert.Equal("acme-api", context.Name);
        Assert.Equal("pom.xml", jdk.Provenance.FilePath);
    }

    [Fact]
    public void LegacyJavaVersionIsNormalisedToEight()
    {
        var fs = new InMemoryProjectFileSystem().File("pom.xml", """
        <project>
          <artifactId>legacy</artifactId>
          <properties><java.version>1.8</java.version></properties>
        </project>
        """);

        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);

        var jdk = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Jdk);
        Assert.Equal("8", jdk.Raw);
    }

    [Fact]
    public void GradleWrapperPinsTheGradleVersion()
    {
        var fs = new InMemoryProjectFileSystem().File("gradle/wrapper/gradle-wrapper.properties", """
        distributionBase=GRADLE_USER_HOME
        distributionUrl=https\://services.gradle.org/distributions/gradle-8.5-bin.zip
        """);

        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);

        var gradle = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Gradle);
        Assert.Equal("8.5", gradle.Raw);
        Assert.Equal(RequirementStrength.Pinned, gradle.Strength);
    }

    [Fact]
    public void GradleToolchainBlockBecomesAJavaRequirement()
    {
        var fs = new InMemoryProjectFileSystem().File("build.gradle.kts", """
        java {
            toolchain {
                languageVersion = JavaLanguageVersion.of(21)
            }
        }
        """);

        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Jdk && r.Raw == "21");
    }

    [Fact]
    public void TemplateArtifactIdIsNotUsedAsAName()
    {
        var fs = new InMemoryProjectFileSystem().File("pom.xml", """
        <project>
          <artifactId>${artifactId}</artifactId>
          <properties><maven.compiler.release>17</maven.compiler.release></properties>
        </project>
        """);

        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);

        Assert.Null(context.Name);
        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Jdk);
    }

    [Fact]
    public void SdkmanrcCarriesSeveralTools()
    {
        var fs = new InMemoryProjectFileSystem().File(".sdkmanrc", """
        java=21.0.2-tem
        gradle=8.5
        """);

        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Jdk && r.Raw == "21.0.2-tem");
        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Gradle && r.Raw == "8.5");
    }
}

public class DenoParserTests
{
    [Fact]
    public void DvmrcPinsTheDenoRuntime()
    {
        var fs = new InMemoryProjectFileSystem()
            .File(".dvmrc", "2.6.9\n")
            .File("deno.json", """{ "name": "@acme/api" }""");

        var context = ProjectContextFactory.Create(fs);
        new DenoParser().Extract(context);

        var deno = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Deno);
        Assert.Equal("2.6.9", deno.Raw);
        Assert.Equal(RequirementStrength.Pinned, deno.Strength);
        Assert.Equal("@acme/api", context.Name);
        Assert.Contains(Ecosystem.Deno, context.Ecosystems);
    }

    [Fact]
    public void DenoWithoutAPinStillCountsAsADenoProject()
    {
        var fs = new InMemoryProjectFileSystem().File("deno.json", "{}");

        var context = ProjectContextFactory.Create(fs);
        new DenoParser().Extract(context);

        Assert.Empty(context.Requirements);
        Assert.Contains(Ecosystem.Deno, context.Ecosystems);
    }

    [Fact]
    public void ToolVersionsCanPinDeno()
    {
        var fs = new InMemoryProjectFileSystem().File(".tool-versions", "deno 2.6.9\n");

        var context = ProjectContextFactory.Create(fs);
        new VersionPinParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Deno && r.Raw == "2.6.9");
    }

    [Fact]
    public void DenoMarkersAreDetectedAsTheirOwnEcosystem()
    {
        Assert.True(MarkerCatalog.TryClassify("deno.json", out var ecosystem, out var strong));
        Assert.True(strong);
        Assert.Equal(Ecosystem.Deno, ecosystem);

        Assert.True(MarkerCatalog.TryClassify("deno.lock", out _, out _));
        Assert.True(MarkerCatalog.TryClassify(".dvmrc", out _, out _));
    }
}

public class RuntimeParserTests
{    [Fact]
    public void GlobalJsonPinsTheSdkAndKeepsRollForward()
    {
        var fs = new InMemoryProjectFileSystem().File("global.json", """
        { "sdk": { "version": "8.0.100", "rollForward": "latestFeature" } }
        """);

        var context = ProjectContextFactory.Create(fs);
        new DotNetParser().Extract(context);

        var sdk = Assert.Single(context.Requirements, r => r.Tool == ToolKind.DotNetSdk);
        Assert.Equal("8.0.100", sdk.Raw);
        Assert.Equal(RequirementStrength.Pinned, sdk.Strength);
        Assert.Contains("latestFeature", sdk.Provenance.Note);
    }

    [Fact]
    public void TargetFrameworkIsAdvisoryNotAPin()
    {
        var fs = new InMemoryProjectFileSystem().File("App.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net8.0-windows</TargetFramework></PropertyGroup>
        </Project>
        """);

        var context = ProjectContextFactory.Create(fs);
        new DotNetParser().Extract(context);

        var sdk = Assert.Single(context.Requirements, r => r.Tool == ToolKind.DotNetSdk);
        Assert.Equal("8.0", sdk.Raw);
        Assert.Equal(RequirementStrength.Advisory, sdk.Strength);
    }

    [Fact]
    public void PyprojectRequiresPython()
    {
        var fs = new InMemoryProjectFileSystem().File("pyproject.toml", """
        [project]
        name = "thing"
        requires-python = ">=3.11,<4"
        """);

        var context = ProjectContextFactory.Create(fs);
        new PythonParser().Extract(context);

        var python = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Python);
        Assert.Equal(">=3.11,<4", python.Raw);
        Assert.Equal("thing", context.Name);
    }

    [Fact]
    public void GoModExposesBothDirectives()
    {
        var fs = new InMemoryProjectFileSystem().File("go.mod", """
        module github.com/acme/thing

        go 1.22.1

        toolchain go1.22.3
        """);

        var context = ProjectContextFactory.Create(fs);
        new GoParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Go && r.Raw == ">= 1.22.1");
        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Go && r.Raw == "1.22.3" && r.Strength == RequirementStrength.Pinned);
        Assert.Equal("thing", context.Name);
    }

    [Fact]
    public void RustToolchainTomlPinsTheChannel()
    {
        var fs = new InMemoryProjectFileSystem().File("rust-toolchain.toml", """
        [toolchain]
        channel = "1.76.0"
        components = ["clippy"]
        """);

        var context = ProjectContextFactory.Create(fs);
        new RustParser().Extract(context);

        var rust = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Rustc);
        Assert.Equal("1.76.0", rust.Raw);
        Assert.Equal(RequirementStrength.Pinned, rust.Strength);
    }

    [Fact]
    public void ComposerRequiresPhp()
    {
        var fs = new InMemoryProjectFileSystem().File("composer.json", """
        { "name": "acme/site", "require": { "php": "^8.2" } }
        """);

        var context = ProjectContextFactory.Create(fs);
        new PhpParser().Extract(context);

        var php = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Php);
        Assert.Equal("^8.2", php.Raw);
    }
}
