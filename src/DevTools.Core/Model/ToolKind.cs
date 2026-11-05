namespace DevTools.Core.Model;

/// <summary>A concrete tool whose installed versions we can enumerate and manage.</summary>
public enum ToolKind
{
    Node,
    Npm,
    Pnpm,
    Yarn,
    Bun,
    Deno,
    Jdk,
    Maven,
    Gradle,
    DotNetSdk,
    Python,
    Uv,
    Go,
    Rustc,
    Ruby,
    Php,
    Dart,
    Elixir,
    Scala,
    Kotlin,
    Zig,
    Terraform,
    Julia,
    Cmake,
    Meson,
    Conan,
    Bazel,
    Pants,
    Xmake,
    Mill,
    Sbt,
    Leiningen,
    Fpc,
    Lazarus,
    Swift,
    Crystal,
    Gleam,
    Nim,
    R,
    Ghc,
    Cabal,
    Stack,
    Ocaml,
    Dmd,
    Perl,
    Lua
}

/// <summary>A language/runtime family. Used for grouping projects and for confidence scoring.</summary>
public enum Ecosystem
{
    Node,
    Deno,
    Java,
    DotNet,
    Python,
    Go,
    Rust,
    Ruby,
    Php,
    Dart,
    Elixir,
    Scala,
    Zig,
    Terraform,
    Julia,
    Cpp,
    Nix,
    Container,
    Bazel,
    Pants,
    Mill,
    Clojure,
    Pascal,
    Swift,
    Crystal,
    Gleam,
    Nim,
    R,
    Haskell,
    OCaml,
    Erlang,
    D,
    Perl,
    Lua
}

public static class ToolKinds
{
    public static Ecosystem EcosystemOf(ToolKind tool) => tool switch
    {
        ToolKind.Node or ToolKind.Npm or ToolKind.Pnpm or ToolKind.Yarn or ToolKind.Bun => Ecosystem.Node,
        ToolKind.Deno => Ecosystem.Deno,
        ToolKind.Jdk or ToolKind.Maven or ToolKind.Gradle => Ecosystem.Java,
        ToolKind.DotNetSdk => Ecosystem.DotNet,
        ToolKind.Python or ToolKind.Uv => Ecosystem.Python,
        ToolKind.Go => Ecosystem.Go,
        ToolKind.Rustc => Ecosystem.Rust,
        ToolKind.Ruby => Ecosystem.Ruby,
        ToolKind.Php => Ecosystem.Php,
        ToolKind.Dart => Ecosystem.Dart,
        ToolKind.Elixir => Ecosystem.Elixir,
        ToolKind.Scala => Ecosystem.Scala,
        ToolKind.Kotlin => Ecosystem.Java,
        ToolKind.Zig => Ecosystem.Zig,
        ToolKind.Terraform => Ecosystem.Terraform,
        ToolKind.Julia => Ecosystem.Julia,
        ToolKind.Cmake or ToolKind.Meson or ToolKind.Conan or ToolKind.Xmake => Ecosystem.Cpp,
        ToolKind.Bazel => Ecosystem.Bazel,
        ToolKind.Pants => Ecosystem.Pants,
        ToolKind.Mill => Ecosystem.Mill,
        ToolKind.Sbt => Ecosystem.Scala,
        ToolKind.Leiningen => Ecosystem.Clojure,
        ToolKind.Fpc or ToolKind.Lazarus => Ecosystem.Pascal,
        ToolKind.Swift => Ecosystem.Swift,
        ToolKind.Crystal => Ecosystem.Crystal,
        ToolKind.Gleam => Ecosystem.Gleam,
        ToolKind.Nim => Ecosystem.Nim,
        ToolKind.R => Ecosystem.R,
        ToolKind.Ghc or ToolKind.Cabal or ToolKind.Stack => Ecosystem.Haskell,
        ToolKind.Ocaml => Ecosystem.OCaml,
        ToolKind.Dmd => Ecosystem.D,
        ToolKind.Perl => Ecosystem.Perl,
        ToolKind.Lua => Ecosystem.Lua,
        _ => Ecosystem.Node
    };

    /// <summary>The short label used in the CLI and UI ("node", "jdk", "dotnet").</summary>
    public static string Display(ToolKind tool) => tool switch
    {
        ToolKind.Node => "node",
        ToolKind.Npm => "npm",
        ToolKind.Pnpm => "pnpm",
        ToolKind.Yarn => "yarn",
        ToolKind.Bun => "bun",
        ToolKind.Deno => "deno",
        ToolKind.Jdk => "jdk",
        ToolKind.Maven => "maven",
        ToolKind.Gradle => "gradle",
        ToolKind.DotNetSdk => "dotnet",
        ToolKind.Python => "python",
        ToolKind.Uv => "uv",
        ToolKind.Go => "go",
        ToolKind.Rustc => "rust",
        ToolKind.Ruby => "ruby",
        ToolKind.Php => "php",
        ToolKind.Dart => "dart",
        ToolKind.Elixir => "elixir",
        ToolKind.Scala => "scala",
        ToolKind.Kotlin => "kotlin",
        ToolKind.Zig => "zig",
        ToolKind.Terraform => "terraform",
        ToolKind.Julia => "julia",
        ToolKind.Cmake => "cmake",
        ToolKind.Meson => "meson",
        ToolKind.Conan => "conan",
        ToolKind.Bazel => "bazel",
        ToolKind.Pants => "pants",
        ToolKind.Xmake => "xmake",
        ToolKind.Mill => "mill",
        ToolKind.Sbt => "sbt",
        ToolKind.Leiningen => "leiningen",
        ToolKind.Fpc => "fpc",
        ToolKind.Lazarus => "lazarus",
        ToolKind.Ocaml => "ocaml",
        ToolKind.Dmd => "dmd",
        _ => tool.ToString().ToLowerInvariant()
    };

    public static bool TryParse(string text, out ToolKind tool)
    {
        foreach (var candidate in Enum.GetValues<ToolKind>())
        {
            if (string.Equals(Display(candidate), text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                tool = candidate;
                return true;
            }
        }

        tool = default;
        return false;
    }

    public static string DisplayEcosystem(Ecosystem ecosystem) => ecosystem switch
    {
        Ecosystem.Node => "node",
        Ecosystem.Deno => "deno",
        Ecosystem.Java => "java",
        Ecosystem.DotNet => "dotnet",
        Ecosystem.Python => "python",
        Ecosystem.Go => "go",
        Ecosystem.Rust => "rust",
        Ecosystem.Ruby => "ruby",
        Ecosystem.Php => "php",
        Ecosystem.Dart => "dart",
        Ecosystem.Elixir => "elixir",
        Ecosystem.Scala => "scala",
        Ecosystem.Zig => "zig",
        Ecosystem.Terraform => "terraform",
        Ecosystem.Julia => "julia",
        Ecosystem.Cpp => "cpp",
        Ecosystem.Nix => "nix",
        Ecosystem.Container => "container",
        Ecosystem.Bazel => "bazel",
        Ecosystem.Pants => "pants",
        Ecosystem.Mill => "mill",
        Ecosystem.Clojure => "clojure",
        Ecosystem.Pascal => "pascal",
        Ecosystem.OCaml => "ocaml",
        _ => ecosystem.ToString().ToLowerInvariant()
    };

    /// <summary>The tools a manager can hold several versions of, in the order a project cares about them.</summary>
    public static IReadOnlyList<ToolKind> All { get; } = Enum.GetValues<ToolKind>();
}
