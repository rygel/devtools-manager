using DevTools.Core.Model;

namespace DevTools.Scan;

/// <summary>
/// The marker catalogue: which file/directory names mean "this is a project" and which
/// ecosystem it belongs to. Pure name matching — no file is opened during discovery.
/// </summary>
public static class MarkerCatalog
{
    private static readonly Dictionary<string, Ecosystem> StrongNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Node
        ["package.json"] = Ecosystem.Node,
        ["pnpm-workspace.yaml"] = Ecosystem.Node,
        ["pnpm-lock.yaml"] = Ecosystem.Node,
        ["yarn.lock"] = Ecosystem.Node,
        ["package-lock.json"] = Ecosystem.Node,
        ["bun.lockb"] = Ecosystem.Node,
        ["bun.lock"] = Ecosystem.Node,
        [".nvmrc"] = Ecosystem.Node,
        [".node-version"] = Ecosystem.Node,
        ["deno.json"] = Ecosystem.Deno,
        ["deno.jsonc"] = Ecosystem.Deno,
        ["deno.lock"] = Ecosystem.Deno,
        [".dvmrc"] = Ecosystem.Deno,

        // Java
        ["pom.xml"] = Ecosystem.Java,
        ["mvnw"] = Ecosystem.Java,
        ["mvnw.cmd"] = Ecosystem.Java,
        ["build.gradle"] = Ecosystem.Java,
        ["build.gradle.kts"] = Ecosystem.Java,
        ["settings.gradle"] = Ecosystem.Java,
        ["settings.gradle.kts"] = Ecosystem.Java,
        ["gradlew"] = Ecosystem.Java,
        ["gradlew.bat"] = Ecosystem.Java,
        ["gradle.properties"] = Ecosystem.Java,
        [".sdkmanrc"] = Ecosystem.Java,
        [".java-version"] = Ecosystem.Java,

        // .NET
        ["global.json"] = Ecosystem.DotNet,
        ["Directory.Build.props"] = Ecosystem.DotNet,
        ["Directory.Packages.props"] = Ecosystem.DotNet,

        // Python
        ["pyproject.toml"] = Ecosystem.Python,
        ["requirements.txt"] = Ecosystem.Python,
        ["Pipfile"] = Ecosystem.Python,
        ["poetry.lock"] = Ecosystem.Python,
        ["uv.lock"] = Ecosystem.Python,
        ["setup.py"] = Ecosystem.Python,
        ["setup.cfg"] = Ecosystem.Python,
        [".python-version"] = Ecosystem.Python,
        ["manage.py"] = Ecosystem.Python,

        // Go
        ["go.mod"] = Ecosystem.Go,
        ["go.work"] = Ecosystem.Go,

        // Rust
        ["Cargo.toml"] = Ecosystem.Rust,
        ["rust-toolchain.toml"] = Ecosystem.Rust,
        ["rust-toolchain"] = Ecosystem.Rust,

        // Ruby
        ["Gemfile"] = Ecosystem.Ruby,
        [".ruby-version"] = Ecosystem.Ruby,
        ["Rakefile"] = Ecosystem.Ruby,

        // Elixir
        ["mix.lock"] = Ecosystem.Elixir,

        // Scala
        ["build.sbt"] = Ecosystem.Scala,
        ["build.sc"] = Ecosystem.Scala,

        // Zig
        ["build.zig"] = Ecosystem.Zig,
        ["build.zig.zon"] = Ecosystem.Zig,
        [".zigversion"] = Ecosystem.Zig,

        // Julia
        ["Project.toml"] = Ecosystem.Julia,
        ["Manifest.toml"] = Ecosystem.Julia,

        // Terraform
        [".terraform-version"] = Ecosystem.Terraform,
        [".terraform.lock.hcl"] = Ecosystem.Terraform,
        ["terragrunt.hcl"] = Ecosystem.Terraform,

        // PHP
        ["composer.json"] = Ecosystem.Php,

        // Dart / Elixir
        ["pubspec.yaml"] = Ecosystem.Dart,
        ["mix.exs"] = Ecosystem.Elixir,

        // C / C++
        ["CMakeLists.txt"] = Ecosystem.Cpp,
        ["CMakePresets.json"] = Ecosystem.Cpp,
        ["meson.build"] = Ecosystem.Cpp,
        ["conanfile.py"] = Ecosystem.Cpp,
        ["conanfile.txt"] = Ecosystem.Cpp,
        ["vcpkg.json"] = Ecosystem.Cpp,
        ["xmake.lua"] = Ecosystem.Cpp,

        // Polyglot build systems
        [".bazelversion"] = Ecosystem.Bazel,
        ["MODULE.bazel"] = Ecosystem.Bazel,
        ["WORKSPACE"] = Ecosystem.Bazel,
        ["WORKSPACE.bazel"] = Ecosystem.Bazel,
        ["pants.toml"] = Ecosystem.Pants,
        ["build.mill"] = Ecosystem.Mill,
        ["build.mill.scala"] = Ecosystem.Mill,
        ["build.mill.yaml"] = Ecosystem.Mill,
        [".mill-version"] = Ecosystem.Mill,

        // Clojure (JVM)
        ["project.clj"] = Ecosystem.Clojure,
        ["deps.edn"] = Ecosystem.Clojure,

        // Free Pascal / Lazarus (project and package files are suffix markers below)
        ["fpmake.pp"] = Ecosystem.Pascal,

        // Swift
        ["Package.swift"] = Ecosystem.Swift,
        ["Package.resolved"] = Ecosystem.Swift,
        [".swift-version"] = Ecosystem.Swift,

        // Crystal
        ["shard.yml"] = Ecosystem.Crystal,
        ["shard.lock"] = Ecosystem.Crystal,

        // Gleam
        ["gleam.toml"] = Ecosystem.Gleam,

        // Nim
        ["nim.cfg"] = Ecosystem.Nim,
        ["config.nims"] = Ecosystem.Nim,
        ["nimble.lock"] = Ecosystem.Nim,

        // R
        ["renv.lock"] = Ecosystem.R,
        [".Rprofile"] = Ecosystem.R,

        // Haskell
        ["stack.yaml"] = Ecosystem.Haskell,
        ["cabal.project"] = Ecosystem.Haskell,
        ["hie.yaml"] = Ecosystem.Haskell,

        // OCaml
        ["dune-project"] = Ecosystem.OCaml,

        // Erlang
        ["rebar.config"] = Ecosystem.Erlang,
        ["rebar.lock"] = Ecosystem.Erlang,
        ["erlang.mk"] = Ecosystem.Erlang,

        // D
        ["dub.json"] = Ecosystem.D,
        ["dub.sdl"] = Ecosystem.D,

        // Perl
        ["cpanfile"] = Ecosystem.Perl,
        ["Makefile.PL"] = Ecosystem.Perl,
        ["Build.PL"] = Ecosystem.Perl,
        [".perl-version"] = Ecosystem.Perl,

        // Lua
        [".luarc.json"] = Ecosystem.Lua,
        [".luacheckrc"] = Ecosystem.Lua,

        // Nix
        ["flake.nix"] = Ecosystem.Nix,
        ["shell.nix"] = Ecosystem.Nix,
        ["devenv.nix"] = Ecosystem.Nix,

        // Containers (informational requirements only)
        ["Dockerfile"] = Ecosystem.Container,
        ["docker-compose.yml"] = Ecosystem.Container,
        ["docker-compose.yaml"] = Ecosystem.Container,
        ["compose.yaml"] = Ecosystem.Container,
        ["compose.yml"] = Ecosystem.Container
    };

    private static readonly Dictionary<string, Ecosystem> WeakNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["README.md"] = Ecosystem.Node,
        ["readme.md"] = Ecosystem.Node,
        ["LICENSE"] = Ecosystem.Node,
        ["LICENSE.md"] = Ecosystem.Node,
        [".editorconfig"] = Ecosystem.Node,
        [".gitignore"] = Ecosystem.Node,
        ["Makefile"] = Ecosystem.Cpp,
        ["Justfile"] = Ecosystem.Node,
        ["Taskfile.yml"] = Ecosystem.Node,
        [".pre-commit-config.yaml"] = Ecosystem.Node,
        [".tool-versions"] = Ecosystem.Node,
        ["mise.toml"] = Ecosystem.Node,
        [".mise.toml"] = Ecosystem.Node
    };

    /// <summary>Directories that mark a project (and survive the hidden-directory prune).</summary>
    public static readonly HashSet<string> MarkerDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".mvn", ".devcontainer", ".github", ".vscode", ".idea"
    };

    private static readonly string[] ProjectFileSuffixesInternal =
    [
        ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx"
    ];

    /// <summary>Suffix-based markers (a project file is a marker whatever its name).</summary>
    public static IReadOnlyList<string> ProjectFileSuffixes => ProjectFileSuffixesInternal;

    /// <summary>Extension-based markers beyond .NET project files, e.g. Terraform's <c>.tf</c>.</summary>
    public static IReadOnlyDictionary<string, Ecosystem> SuffixMarkers { get; } =
        new Dictionary<string, Ecosystem>(StringComparer.OrdinalIgnoreCase)
        {
            [".tf"] = Ecosystem.Terraform,
            [".lpi"] = Ecosystem.Pascal,
            [".lpk"] = Ecosystem.Pascal,
            [".lpr"] = Ecosystem.Pascal,
            [".nimble"] = Ecosystem.Nim,
            [".Rproj"] = Ecosystem.R,
            [".cabal"] = Ecosystem.Haskell,
            [".opam"] = Ecosystem.OCaml,
            [".rockspec"] = Ecosystem.Lua
        };

    /// <summary>Marker names grouped by ecosystem, for documentation and `devtools support`.</summary>
    public static IReadOnlyDictionary<Ecosystem, IReadOnlyList<string>> MarkersByEcosystem { get; } =
        StrongNames
            .GroupBy(pair => pair.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(pair => pair.Key)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList());

    public static IReadOnlyDictionary<string, Ecosystem> Strong => StrongNames;

    public static IReadOnlyDictionary<string, Ecosystem> Weak => WeakNames;

    /// <summary>Classifies a directory entry by name only.</summary>
    public static bool TryClassify(string name, out Ecosystem ecosystem, out bool strong)
    {
        if (StrongNames.TryGetValue(name, out ecosystem))
        {
            strong = true;
            return true;
        }

        foreach (var suffix in ProjectFileSuffixesInternal)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                ecosystem = Ecosystem.DotNet;
                strong = true;
                return true;
            }
        }

        foreach (var (extension, mapped) in SuffixMarkers)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                ecosystem = mapped;
                strong = true;
                return true;
            }
        }

        if (WeakNames.TryGetValue(name, out ecosystem))
        {
            strong = false;
            return true;
        }

        ecosystem = default;
        strong = false;
        return false;
    }

    /// <summary>True when the name could plausibly be a marker; used to size the walker's HashSet.</summary>
    public static IReadOnlyCollection<string> AllStrongNames => StrongNames.Keys;
}
