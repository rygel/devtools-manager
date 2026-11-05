using DevTools.Core.Model;
using DevTools.Scan;
using Xunit;

namespace DevTools.Tests;

public sealed class FrameworkDetectorTests
{
    private static IReadOnlyList<FrameworkUse> Detect(InMemoryProjectFileSystem fs) =>
        FrameworkDetector.Detect(new ProjectContext { Directory = "/project", Fs = fs });

    private static FrameworkUse Single(IReadOnlyList<FrameworkUse> found, string id) =>
        Assert.Single(found, f => f.Id == id);

    [Fact]
    public void PackageJsonDependenciesAndDevDependenciesYieldFrameworksWithDeclaredVersions()
    {
        var found = Detect(new InMemoryProjectFileSystem().File("package.json", """
            {
              "name": "shop",
              "description": "a django-like react-ish thing",
              "dependencies": { "react": "^18.2.0", "@angular/core": "17.0.1", "left-pad": "1.0.0" },
              "devDependencies": { "next": "14.1.0" }
            }
            """));
        var react = Single(found, "react");
        Assert.Equal("React", react.Name);
        Assert.Equal(Ecosystem.Node, react.Ecosystem);
        Assert.Equal("^18.2.0", react.Version);
        Assert.Equal("package.json", react.Provenance.FilePath);
        Assert.Equal(4, react.Provenance.Line);
        Assert.Equal("17.0.1", Single(found, "angular").Version);
        Assert.Equal("Next.js", Single(found, "next").Name);
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void WordsInsideDescriptionsAreNotFrameworks()
    {
        var found = Detect(new InMemoryProjectFileSystem()
            .File("package.json", "{ \"name\": \"x\", \"description\": \"uses react and express\", \"keywords\": [\"react\"] }")
            .File("pyproject.toml", "[project]\nname = \"x\"\ndescription = \"A django and flask helper\"\n"));
        Assert.Empty(found);
    }

    [Fact]
    public void MavenParentGivesTheSpringBootVersionAndStartersDoNotDuplicateIt()
    {
        var found = Detect(new InMemoryProjectFileSystem().File("pom.xml", """
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <parent>
                <groupId>org.springframework.boot</groupId>
                <artifactId>spring-boot-starter-parent</artifactId>
                <version>3.2.1</version>
              </parent>
              <dependencies>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-web</artifactId></dependency>
                <dependency><groupId>com.google.guava</groupId><artifactId>guava</artifactId><version>33.0.0-jre</version></dependency>
              </dependencies>
            </project>
            """));
        var boot = Assert.Single(found);
        Assert.Equal("spring-boot", boot.Id);
        Assert.Equal("3.2.1", boot.Version);
        Assert.Equal("pom.xml", boot.Provenance.FilePath);
    }

    [Fact]
    public void MavenPropertyVersionsAreResolvedAndUnresolvableOnesStayUnknown()
    {
        var found = Detect(new InMemoryProjectFileSystem().File("pom.xml", """
            <project>
              <properties><quarkus.platform.version>3.8.2</quarkus.platform.version></properties>
              <dependencyManagement><dependencies>
                <dependency><groupId>io.quarkus.platform</groupId><artifactId>quarkus-bom</artifactId><version>${quarkus.platform.version}</version></dependency>
              </dependencies></dependencyManagement>
              <dependencies>
                <dependency><groupId>io.javalin</groupId><artifactId>javalin</artifactId><version>${missing.version}</version></dependency>
              </dependencies>
            </project>
            """));
        Assert.Equal("3.8.2", Single(found, "quarkus").Version);
        Assert.Null(Single(found, "javalin").Version);
    }

    [Fact]
    public void GradleBuildFilesYieldPluginAndCoordinateFrameworks()
    {
        var found = Detect(new InMemoryProjectFileSystem().File("build.gradle.kts", """
            plugins {
                kotlin("jvm") version "2.0.0"
                id("org.springframework.boot") version "3.3.0"
            }
            dependencies {
                implementation("io.javalin:javalin:6.1.0")
                testImplementation("org.junit.jupiter:junit-jupiter:5.10.0")
            }
            """));
        var boot = Single(found, "spring-boot");
        Assert.Equal("3.3.0", boot.Version);
        Assert.Equal(3, boot.Provenance.Line);
        Assert.Equal("6.1.0", Single(found, "javalin").Version);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void DotNetProjectsDistinguishWebSdkAndPackageFrameworks()
    {
        var found = Detect(new InMemoryProjectFileSystem()
            .File("Shop.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="Avalonia" Version="11.0.5" /></ItemGroup>
                </Project>
                """));
        Assert.Equal("8.0", Single(found, "aspnet-core").Version);
        Assert.Equal("11.0.5", Single(found, "avalonia").Version);
    }

    [Fact]
    public void PythonRequirementsPyprojectAndPipfile()
    {
        var found = Detect(new InMemoryProjectFileSystem()
            .File("requirements.txt", "# web\nDjango==4.2.7\nrequests>=2\n")
            .File("pyproject.toml", "[project]\nname = \"x\"\ndependencies = [\"fastapi>=0.110\", \"httpx\"]\n[tool.poetry.dependencies]\nflask = \"^3.0\"\n"));
        Assert.Equal("4.2.7", Single(found, "django").Version);
        Assert.Equal(">=0.110", Single(found, "fastapi").Version);
        Assert.Equal("^3.0", Single(found, "flask").Version);
    }

    [Fact]
    public void RubyPhpGoRustElixirAndDartManifests()
    {
        var found = Detect(new InMemoryProjectFileSystem()
            .File("Gemfile", "source 'https://rubygems.org'\ngem 'rails', '~> 7.1'\ngem 'puma'\n")
            .File("composer.json", "{ \"require\": { \"php\": \"^8.2\", \"laravel/framework\": \"^10.0\" } }")
            .File("go.mod", "module x\n\ngo 1.22\n\nrequire (\n\tgithub.com/gin-gonic/gin v1.9.1\n\tgithub.com/stretchr/testify v1.8.4\n)\n")
            .File("Cargo.toml", "[package]\nname = \"x\"\n\n[dependencies]\naxum = \"0.7\"\ntokio = { version = \"1\", features = [\"full\"] }\nrocket = { version = \"0.5\" }\n")
            .File("mix.exs", "defp deps do\n  [{:phoenix, \"~> 1.7.10\"}, {:jason, \"~> 1.4\"}]\nend\n")
            .File("pubspec.yaml", "name: app\ndependencies:\n  flutter:\n    sdk: flutter\n  http: ^1.0.0\n"));
        Assert.Equal("~> 7.1", Single(found, "rails").Version);
        Assert.Equal("^10.0", Single(found, "laravel").Version);
        Assert.Equal("v1.9.1", Single(found, "gin").Version);
        Assert.Equal("0.7", Single(found, "axum").Version);
        Assert.Equal("0.5", Single(found, "rocket").Version);
        Assert.Equal("~> 1.7.10", Single(found, "phoenix").Version);
        Assert.Null(Single(found, "flutter").Version);
        Assert.Equal(7, found.Count);
    }

    [Fact]
    public void ProjectsWithoutManifestsHaveNoFrameworksAndBrokenManifestsDoNotThrow()
    {
        Assert.Empty(Detect(new InMemoryProjectFileSystem()));
        Assert.Empty(Detect(new InMemoryProjectFileSystem().File("package.json", "{ not json").File("pom.xml", "<project><unclosed>")));
    }

    [Fact]
    public void FrameworksAreNotToolchains()
    {
        foreach (var tool in ToolKinds.All)
            Assert.DoesNotContain(FrameworkCatalog.All, framework => framework.Id == ToolKinds.Display(tool));
    }
}
