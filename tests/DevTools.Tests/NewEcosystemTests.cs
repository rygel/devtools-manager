using DevTools.Core.Model;
using DevTools.Scan;
using DevTools.Scan.Parsers;
using Xunit;

namespace DevTools.Tests;

public class ElixirParserTests
{
    [Fact]
    public void MixExsYieldsTheElixirRequirementAndAppName()
    {
        var fs = new InMemoryProjectFileSystem().File("mix.exs", """
        defmodule MyApp.MixProject do
          use Mix.Project

          def project do
            [
              app: :my_app,
              version: "0.1.0",
              elixir: "~> 1.15",
              deps: deps()
            ]
          end
        end
        """);

        var context = ProjectContextFactory.Create(fs);
        new ElixirParser().Extract(context);

        var requirement = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Elixir);
        Assert.Equal("~> 1.15", requirement.Raw);
        Assert.Equal("my_app", context.Name);
        Assert.Contains(Ecosystem.Elixir, context.Ecosystems);
    }

    [Fact]
    public void PessimisticElixirRequirementMeansTheSameMajor()
    {
        var constraint = DevTools.Core.Constraints.ConstraintParser.Parse(ToolKind.Elixir, "~> 1.15");

        Assert.True(constraint.Satisfies(DevTools.Core.Versioning.VersionNumber.Parse("1.16.2"), ToolKind.Elixir));
        Assert.False(constraint.Satisfies(DevTools.Core.Versioning.VersionNumber.Parse("2.0.0"), ToolKind.Elixir));

        var patchLevel = DevTools.Core.Constraints.ConstraintParser.Parse(ToolKind.Elixir, "~> 1.15.0");
        Assert.False(patchLevel.Satisfies(DevTools.Core.Versioning.VersionNumber.Parse("1.16.0"), ToolKind.Elixir));
        Assert.True(patchLevel.Satisfies(DevTools.Core.Versioning.VersionNumber.Parse("1.15.5"), ToolKind.Elixir));
    }
}

public class ScalaParserTests
{
    [Fact]
    public void BuildSbtGivesScalaVersionAndName()
    {
        var fs = new InMemoryProjectFileSystem().File("build.sbt", """
        ThisBuild / scalaVersion := "3.3.1"
        name := "demo-service"
        libraryDependencies += "org.typelevel" %% "cats-core" % "2.10.0"
        """);

        var context = ProjectContextFactory.Create(fs);
        new ScalaParser().Extract(context);

        var scala = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Scala);
        Assert.Equal("3.3.1", scala.Raw);
        Assert.Equal("demo-service", context.Name);
    }

    [Fact]
    public void ScalaCliUsingDirectiveIsRead()
    {
        var fs = new InMemoryProjectFileSystem().File("Main.scala", """
        //> using scala 3.4.2
        //> using dep com.lihaoyi::os-lib:0.9.1

        @main def hello() = println("hi")
        """);

        var context = ProjectContextFactory.Create(fs);
        new ScalaParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Scala && r.Raw == "3.4.2");
    }
}

public class ZigParserTests
{
    [Fact]
    public void ZigVersionFileIsAnExactPin()
    {
        var fs = new InMemoryProjectFileSystem()
            .File(".zigversion", "0.13.0\n")
            .File("build.zig", "const std = @import(\"std\");");

        var context = ProjectContextFactory.Create(fs);
        new ZigParser().Extract(context);

        var zig = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Zig);
        Assert.Equal("0.13.0", zig.Raw);
        Assert.Equal(RequirementStrength.Pinned, zig.Strength);
    }

    [Fact]
    public void BuildZonMinimumIsAMinimumNotAPin()
    {
        var fs = new InMemoryProjectFileSystem().File("build.zig.zon", """
        .{
            .name = .demo,
            .version = "0.1.0",
            .minimum_zig_version = "0.13.0",
            .dependencies = .{},
        }
        """);

        var context = ProjectContextFactory.Create(fs);
        new ZigParser().Extract(context);

        var zig = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Zig);
        Assert.Equal(">= 0.13.0", zig.Raw);
        Assert.Equal(RequirementStrength.Declared, zig.Strength);
    }
}

public class TerraformParserTests
{
    [Fact]
    public void RequiredVersionIsReadFromAnyTfFile()
    {
        var fs = new InMemoryProjectFileSystem().File("versions.tf", """
        terraform {
          required_version = ">= 1.5"
        }
        """);

        var context = ProjectContextFactory.Create(fs);
        new TerraformParser().Extract(context);

        var terraform = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Terraform);
        Assert.Equal(">= 1.5", terraform.Raw);
        Assert.Equal("versions.tf", terraform.Provenance.FilePath);
    }

    [Fact]
    public void TfenvPinFileWins()
    {
        var fs = new InMemoryProjectFileSystem().File(".terraform-version", "1.9.5\n");

        var context = ProjectContextFactory.Create(fs);
        new TerraformParser().Extract(context);

        Assert.Contains(context.Requirements,
            r => r.Tool == ToolKind.Terraform && r.Raw == "1.9.5" && r.Strength == RequirementStrength.Pinned);
    }
}

public class JuliaParserTests
{
    [Fact]
    public void ProjectTomlCompatJuliaIsTheRequirement()
    {
        var fs = new InMemoryProjectFileSystem().File("Project.toml", """
        name = "MyPkg"
        uuid = "0000-1111"

        [compat]
        julia = "1.10"
        """);

        var context = ProjectContextFactory.Create(fs);
        new JuliaParser().Extract(context);

        var julia = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Julia);
        Assert.Equal("1.10", julia.Raw);
        Assert.Equal("MyPkg", context.Name);
    }
}

public class CppBuildParserTests
{
    [Fact]
    public void CmakeMinimumBecomesACmakeRequirement()
    {
        var fs = new InMemoryProjectFileSystem().File("CMakeLists.txt", """
        cmake_minimum_required(VERSION 3.20)
        project(demo CXX)
        set(CMAKE_CXX_STANDARD 20)
        """);

        var context = ProjectContextFactory.Create(fs);
        new CppBuildParser().Extract(context);

        var cmake = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Cmake);
        Assert.Equal(">= 3.20", cmake.Raw);
        Assert.Contains(context.Diagnostics, d => d.Code == "cpp-standard" && d.Message.Contains("C++20"));
    }

    [Fact]
    public void CmakePresetsSupplyTheMinimumWhenTheScriptDoesNot()
    {
        var fs = new InMemoryProjectFileSystem().File("CMakePresets.json", """
        {
          "version": 6,
          "cmakeMinimumRequired": { "major": 3, "minor": 25, "patch": 0 }
        }
        """);

        var context = ProjectContextFactory.Create(fs);
        new CppBuildParser().Extract(context);

        Assert.Contains(context.Requirements,
            r => r.Tool == ToolKind.Cmake && r.Raw == ">= 3.25.0");
    }

    [Fact]
    public void MesonVersionIsRead()
    {
        var fs = new InMemoryProjectFileSystem().File("meson.build", """
        project('demo', 'cpp', version : '1.0', meson_version : '>= 1.2')
        """);

        var context = ProjectContextFactory.Create(fs);
        new CppBuildParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Meson && r.Raw == ">= 1.2");
    }

    [Fact]
    public void ConanRequiredVersionIsRead()
    {
        var fs = new InMemoryProjectFileSystem().File("conanfile.py", """
        from conan import ConanFile

        required_conan_version = ">=2.0"
        """);

        var context = ProjectContextFactory.Create(fs);
        new CppBuildParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Conan && r.Raw == ">=2.0");
    }
}

public class GradleDslTests
{
    [Fact]
    public void KotlinPluginVersionIsExtractedFromKotlinDsl()
    {
        var fs = new InMemoryProjectFileSystem().File("build.gradle.kts", """
        plugins {
            kotlin("jvm") version "2.0.20"
            id("org.jetbrains.kotlin.plugin.serialization") version "2.0.20"
        }
        """);

        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);

        var kotlin = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Kotlin);
        Assert.Equal("2.0.20", kotlin.Raw);
    }

    [Fact]
    public void KotlinPluginIdFormIsAlsoRead()
    {
        var fs = new InMemoryProjectFileSystem().File("build.gradle", """
        plugins {
            id 'org.jetbrains.kotlin.jvm' version '1.9.24'
        }
        """);

        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Kotlin && r.Raw == "1.9.24");
    }

    [Fact]
    public void KotlinVersionPropertyInMavenIsRead()
    {
        var fs = new InMemoryProjectFileSystem().File("pom.xml", """
        <project>
          <artifactId>kotlin-service</artifactId>
          <properties>
            <java.version>21</java.version>
            <kotlin.version>1.9.24</kotlin.version>
          </properties>
        </project>
        """);

        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Kotlin && r.Raw == "1.9.24");
        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Jdk && r.Raw == "21");
    }

    [Fact]
    public void ScalaDependencyInGradleIsRead()
    {
        var fs = new InMemoryProjectFileSystem().File("build.gradle.kts", """
        dependencies {
            implementation("org.scala-lang:scala3-library_3:3.3.1")
        }
        """);

        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Scala && r.Raw == "3.3.1");
    }

    [Fact]
    public void AGradleFileWithNoJavaToolchainStillYieldsKotlin()
    {
        var fs = new InMemoryProjectFileSystem().File("build.gradle.kts", """
        plugins { kotlin("jvm") version "2.0.0" }
        """);

        var context = ProjectContextFactory.Create(fs);
        new JavaParser().Extract(context);

        Assert.Contains(context.Requirements, r => r.Tool == ToolKind.Kotlin);
        Assert.DoesNotContain(context.Requirements, r => r.Tool == ToolKind.Jdk);
    }
}

public class MarkerCoverageTests
{
    [Theory]
    [InlineData("mix.lock", Ecosystem.Elixir)]
    [InlineData("build.sbt", Ecosystem.Scala)]
    [InlineData("build.sc", Ecosystem.Scala)]
    [InlineData("build.zig", Ecosystem.Zig)]
    [InlineData("build.zig.zon", Ecosystem.Zig)]
    [InlineData(".zigversion", Ecosystem.Zig)]
    [InlineData("Project.toml", Ecosystem.Julia)]
    [InlineData("Manifest.toml", Ecosystem.Julia)]
    [InlineData(".terraform-version", Ecosystem.Terraform)]
    [InlineData("main.tf", Ecosystem.Terraform)]
    [InlineData("CMakePresets.json", Ecosystem.Cpp)]
    [InlineData("conanfile.py", Ecosystem.Cpp)]
    public void NewMarkersClassifyToTheirEcosystem(string name, Ecosystem expected)
    {
        Assert.True(MarkerCatalog.TryClassify(name, out var ecosystem, out var strong));
        Assert.True(strong);
        Assert.Equal(expected, ecosystem);
    }
}
