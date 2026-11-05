# Ecosystem support

The honest, current state of what `devtools` understands — and what it only *notices*.

Read this before trusting a scan. There is a meaningful difference between "we saw a `mix.exs`"
and "we know which Elixir version that project needs", and the tool deliberately does not blur it.

Regenerate the machine-readable version any time with `devtools support --json`.

---

## The three tiers

| Tier | Meaning | What you see in the scan |
|---|---|---|
| **1. Resolved** | Marker detected → requirement extracted → matched against installed toolchains → installable | A toolchain row with a ✔ / ✘ |
| **2. Detected** | Project recognised and tagged with an ecosystem, but no version requirement exists | An ecosystem count, no toolchain row |
| **3. Unknown** | Not detected at all | Nothing |

Tier 2 exists on purpose. A marker without a matching tool and parser is a promise the tool
cannot keep — it would show "Elixir project, 0 requirements, all good", which is a lie. That is
exactly the bug that was fixed for PHP and Dart, and again for Deno.

---

## Tier 1 — fully resolved and managed

26 tools across 18 ecosystems.

| Ecosystem | Detection | Requirement syntax | Toolchains |
|---|---|---|---|
| **node** | `.nvmrc` `.node-version` `package.json` lockfiles | `18` `18.20.2` `lts/iron` `lts/*` `>=18 <21` | nvm-windows, nvm, fnm, volta, asdf, mise, system |
| **npm** | `engines.npm`, `packageManager` | range or exact | `npm install -g npm@x` |
| **pnpm / yarn** | `packageManager`, lockfiles | `name@version` | corepack |
| **bun** | `bun.lock(b)`, `packageManager` | `name@version` | PATH only (inventory, not install) |
| **deno** | `deno.json(c)` `deno.lock` `.dvmrc` | exact, asdf/mise pins | dvm, asdf, mise, PATH |
| **jdk** | `pom.xml`, Gradle, wrappers, `.sdkmanrc`, `.java-version` | `17` `1.8` `21.0.2-tem` `[17,22)` | SDKMAN, `~/.jdks`, platform roots, `JAVA_HOME` |
| **maven / gradle** | wrappers, `.sdkmanrc` | exact from `distributionUrl` | wrapper dists, SDKMAN |
| **kotlin** | `pom.xml` `<kotlin.version>`, Gradle plugin block | exact | SDKMAN, `kotlinc` on PATH |
| **scala** | `build.sbt`, `build.sc`, `//> using scala`, Gradle `scala3-library` | exact | `scala -version`, asdf, mise |
| **dotnet** | `global.json`, `*.csproj` | `8.0.100` + `rollForward` | `dotnet --list-sdks`, platform roots |
| **python** | `pyproject.toml`, `.python-version`, `Pipfile`, `uv.lock` | PEP 440 (`>=3.11,<4`, `~=3.10`) | pyenv, uv, asdf, mise, system |
| **uv** | `uv.lock`, PATH | `*` (advisory) | PATH |
| **go** | `go.mod` | `go` (floor) + `toolchain` (pin) | GOROOT, `~/sdk/go*`, PATH |
| **rust** | `rust-toolchain.toml`, `Cargo.toml` | `1.76.0` `stable` `nightly` | rustup, PATH |
| **ruby** | `.ruby-version`, `Gemfile` | exact | rbenv, rvm, asdf, mise, PATH |
| **php** | `composer.json` | `^8.2` `>=8.0 <9.0` `^7.3\|^8.0` | PATH |
| **dart** | `pubspec.yaml` | `environment.sdk` range | PATH |
| **elixir** | `mix.exs`, `mix.lock` | `~> 1.15` (pessimistic = same series) | asdf, mise, PATH |
| **zig** | `build.zig`, `build.zig.zon`, `.zigversion` | `.zigversion` exact; `.minimum_zig_version` floor | zvm, asdf, mise, PATH |
| **terraform** | `*.tf`, `.terraform-version`, `terragrunt.hcl` | `required_version` range; tfenv exact | tfenv, asdf, mise, PATH |
| **julia** | `Project.toml`, `Manifest.toml` | `[compat] julia = "1.10"` | asdf, mise, PATH |
| **cmake** | `CMakeLists.txt`, `CMakePresets.json` | `cmake_minimum_required(VERSION x)` | PATH |
| **meson** | `meson.build` | `meson_version` | PATH |
| **conan** | `conanfile.py` | `required_conan_version` | PATH |
| **xmake** | `xmake.lua` | `set_xmakever` (minimum) | not inventoried |
| **bazel** | `.bazelversion`, `MODULE.bazel`, `WORKSPACE` | `.bazelversion` | not inventoried |
| **pants** | `pants.toml` | `pants_version` | not inventoried |
| **sbt** | `build.sbt`, `project/build.properties` | `sbt.version` | not inventoried |
| **leiningen** | `project.clj`, `deps.edn` | `:min-lein-version` (minimum) | not inventoried |
| **mill** | `build.mill`, `build.mill.scala`, `build.mill.yaml`, `.mill-version` | `//\| mill-version:` header, `.mill-version`, `.config/mill-version` | not inventoried |
| **fpc**, **lazarus** | `*.lpi`, `*.lpk`, `*.lpr`, `fpmake.pp` | none (the project formats declare no compiler version) | PATH (`fpc -iV`, `lazbuild --version`) |
| **swift** | `Package.swift`, `Package.resolved`, `.swift-version` | `// swift-tools-version:` (minimum) | PATH |
| **crystal** | `shard.yml`, `shard.lock` | `crystal:` (advisory, Shards only warns) | PATH |
| **gleam** | `gleam.toml` | `gleam =` | PATH |
| **nim** | `*.nimble`, `nim.cfg`, `config.nims` | `requires "nim >= x"` | PATH |
| **r** | `renv.lock`, `*.Rproj`, `.Rprofile` | `Depends: R (>= x)` in `DESCRIPTION` | PATH |
| **ghc**, **cabal**, **stack** | `*.cabal`, `cabal.project`, `stack.yaml`, `hie.yaml` | none | PATH (`--numeric-version`) |
| **ocaml** | `dune-project`, `*.opam` | none | PATH |
| **dmd** | `dub.json`, `dub.sdl` | none | PATH |
| **perl** | `cpanfile`, `Makefile.PL`, `Build.PL`, `.perl-version` | none | PATH |
| **lua** | `*.rockspec`, `.luarc.json`, `.luacheckrc` | none | PATH |
| Erlang | `rebar.config`, `rebar.lock`, `erlang.mk` | none | not inventoried |

`devtools migrate` rewrites existing declarations for **node, deno, python, java, ruby, dotnet**
(see README §7). It never creates a file that does not exist.

### Elixir's `~>` is not npm's `~`

`~> 1.15` means `>= 1.15, < 2.0`; `~> 1.15.0` means `>= 1.15.0, < 1.16.0`. That is the same rule
as PEP 440's `~=`, so both are normalised to the "compatible release" range. Getting this wrong
would have flagged every Elixir 1.16 project as broken.

### What we read from a Gradle file, and what we don't

A Gradle build is a program. Evaluating it is out of scope — but version strings inside it are
literals, and parsing those is exactly what Dependabot and Renovate do.

Read (partial, regex-level, with provenance pointing at the line):

| Declaration | Extracted |
|---|---|
| `kotlin("jvm") version "2.0.20"` | kotlin 2.0.20 |
| `id("org.jetbrains.kotlin.jvm") version "2.0.20"` (or `id '…'`) | kotlin 2.0.20 |
| `kotlin_version = '1.9.24'` / `kotlinVersion = "…"` | kotlin |
| `implementation("org.scala-lang:scala3-library_3:3.3.1")` | scala 3.3.1 |
| `languageVersion = JavaLanguageVersion.of(21)` | jdk 21 |
| `jvmToolchain(17)` | jdk 17 |
| `sourceCompatibility = "1.8"` | jdk 8 |
| `distributionUrl=…gradle-8.5-bin.zip` | gradle 8.5 |
| Applied Kotlin plugin alias in `gradle/libs.versions.toml` | literal version or a `[versions]` reference |

**Not** read: arbitrary catalog syntax or libraries, custom catalog names, variables that resolve at configuration time, `buildSrc` conventions, and anything requiring the daemon. Only applied Kotlin plugin aliases with single-line inline entries and literal or simple `version.ref` values are read. If the version is computed, we do not guess.

### C and C++

The compiler is not pinnable in any portable way — a `CMakeLists.txt` says nothing about whether
you have GCC, Clang or MSVC. What *is* pinnable is the build tooling, so that is what gets a
toolchain row: **CMake**, **Meson** and **Conan**. `CMAKE_CXX_STANDARD`/`CXX_STANDARD` is
reported as an informational diagnostic ("C++20 language standard declared"), never as a
toolchain requirement.

### CI drift (GitHub Actions)

`.github/workflows/*.yml` and `*.yaml` are read for `actions/setup-node`, `setup-java`, `setup-python`,
`setup-go` and `setup-dotnet` steps with a literal `node-version`, `java-version`, `python-version`,
`go-version` or `dotnet-version`. Each one is recorded as an advisory requirement with its file and line,
and, when it conflicts with what the project declares (for example `.nvmrc` says 18 and CI installs 20),
the project gets a `ci-drift` warning and shows as needing attention. Two constraints conflict only
when neither accepts the other's pin or lower bound, so `>=18` against CI 20 is not drift. Expressions
(`${{ matrix.node }}`), lists, block scalars and `*-version-file` inputs are skipped rather than guessed,
and tools outside the project's own ecosystems are ignored. Dockerfile and devcontainer versions are not
compared yet.

### Bazel, Pants, xmake, Mill, sbt and Leiningen

These build systems fetch or gate their own version: Bazelisk downloads the Bazel named in
`.bazelversion`, the Pants launcher installs `pants_version`, and `set_xmakever` is a minimum.
DevTools therefore reads the declaration and shows it on the project, but records it as
**advisory**: it does not count as a missing toolchain, and the tools are not inventoried or
installed (running a launcher to ask its version could trigger a download). Mill reads its version from the `//| mill-version:` build header, `.mill-version` or
`.config/mill-version`; its docs do not say which wins, so every declaration found is kept. A legacy
`build.sc` is read but, because Scala scripts share that name, it does not by itself mark a Mill project.
sbt's pin was previously only an info diagnostic; it is now an advisory sbt requirement. For Clojure,
`deps.edn` declares no tool version and the Clojure library version in `:dependencies` is a dependency, so
only `:min-lein-version` is read. Floating Bazel values
(`latest`, `last_green`, `rolling`, `4.x`, `fork/version`) name no exact version, so they produce an
informational diagnostic instead of a requirement.

---

## Tier 2 — detected, not versioned

| Ecosystem | Detected by | Why there is no requirement |
|---|---|---|
| **nix** | `flake.nix`, `shell.nix`, `devenv.nix` | a flake pins everything in `flake.lock`; there is no single "version" to resolve |
| **container** | `Dockerfile`, compose files | `FROM node:20` is reported as an informational diagnostic; images are never pulled |

---

## Frameworks — declared, never managed

A **toolchain** is something installed on the machine that a project needs a version of (a JDK, Node,
Maven, the .NET SDK). It is resolved against the project's requirement, reported ✔ / ✘, and can be
installed. A **framework** is something the project is *built with*: Spring Boot, React, Django. It
is a declared dependency, so it has a version the manifest states, but nothing to install and
nothing to resolve. The two are kept apart everywhere: a separate list in the model
(`ProjectRecord.Frameworks`, never `Requirements`), a separate section in `devtools show` and in the
desktop detail pane, a separate command (`devtools frameworks`), and no status or exit-code effect.

Frameworks are read from the dependency sections of manifests only. A word in a description or a
keyword list never counts, and versions are reported exactly as written (`^18.2.0`, `3.2.1`,
`v1.9.1`); an unresolvable `${property}` version is shown as undeclared rather than guessed.

| Ecosystem | Manifest | Frameworks |
|---|---|---|
| **node** | `package.json` dependencies, devDependencies | React, Next.js, Vue, Nuxt, Angular, Svelte, SvelteKit, SolidJS, Astro, Remix, Express, Fastify, NestJS, Electron, React Native, Expo |
| **java** | `pom.xml` (parent, dependencies, plugins; `${property}` versions resolved), `build.gradle(.kts)` (plugin ids and `group:artifact:version` strings) | Spring Boot, Spring Framework (only without Boot), Quarkus, Micronaut, Javalin, Vaadin, Dropwizard, Helidon |
| **dotnet** | `*.csproj` | ASP.NET Core (`Microsoft.NET.Sdk.Web`), Blazor WebAssembly, .NET MAUI, Avalonia |
| **python** | `requirements.txt`, `pyproject.toml` dependency lists, `Pipfile` | Django, Flask, FastAPI |
| **ruby** | `Gemfile` | Ruby on Rails, Sinatra |
| **php** | `composer.json` require, require-dev | Laravel, Symfony, Slim |
| **dart** | `pubspec.yaml` (`flutter: sdk: flutter`) | Flutter |
| **go** | `go.mod` | Gin, Echo, Fiber |
| **rust** | `Cargo.toml` | Axum, Actix Web, Rocket, Tauri |
| **elixir** | `mix.exs` | Phoenix |

Not detected: Gradle version catalogs (`libs.versions.toml`), frameworks pulled in only
transitively, and anything not in the table. Adding one means a row in `FrameworkCatalog` and a
mapping in `FrameworkDetector`; the tests in `FrameworkDetectorTests` show the manifest shapes.

---

## Tier 3 — not supported, with the research

| Ecosystem | Marker | How it pins | Effort |
|---|---|---|---|
| **Swift** | `Package.swift`, `.swift-version` | `// swift-tools-version:5.9` on line 1 | **S** |
| **Crystal** | `shard.yml`, `.crystal-version` | `crystal: '>= 1.0'`; crenv's file | **S** |
| **Nim** | `*.nimble` | `requires "nim >= 1.6.0"` | **S** |
| **R** | `renv.lock`, `DESCRIPTION` | `"R": { "Version": "4.4.1" }` | **S** |
| **Perl** | `cpanfile`, `Makefile.PL` | `requires 'perl', '5.36'`; `.perl-version` | **S** |
| **Lua** | `*.rockspec` | `dependencies = { "lua >= 5.1" }` | **S** |
| **Bazel** | `MODULE.bazel`, `.bazelversion` | single line (bazelisk) | **S** |
| **Elm** | `elm.json` | `"elm-version": "0.19.1"` | **S** |
| **Solidity** | `foundry.toml`, `hardhat.config.*` | `solc = "0.8.24"` | **S** |
| **Cairo** | `Scarb.toml` | `cairo-version` | **S** |
| **Haskell** | `stack.yaml`, `cabal.project` | `resolver: lts-21.25` (a snapshot, not GHC); `with-compiler:` | **M** |
| **OCaml** | `dune-project`, `*.opam` | `(lang dune 3.11)` + `ocaml >= 4.14` | **M** |
| **Clojure** | `deps.edn`, `project.clj` | no self-version; the artifact version | **M** |
| **Gleam** | `gleam.toml`, `manifest.toml` | no compiler pin; asdf/mise only | **M** |
| **Move** | `Move.toml` | git revs in `[dependencies]` | **M** |
| **Pulumi** | `Pulumi.yaml` | runtime block + `package.json` | **M** |
| **Ansible** | `ansible.cfg` | collections versions, no runtime pin | **M** |
| **Flutter** | `.fvmrc` | FVM pins the SDK; `environment.sdk` only pins Dart | **S** |
| **Android SDK** | `build.gradle` | `compileSdk` + AGP version | **M** |
| **Unity** | `ProjectSettings/ProjectVersion.txt` | `m_EditorVersion: 2022.3.21f1` | **S** |
| **Unreal** | `*.uproject` | `"EngineAssociation": "5.3"` | **S** |
| **Godot** | `project.godot` | `config_version` is a *format* version, not the engine | **M** |
| **sbt (the tool)** | `project/build.properties` | `sbt.version=1.9.8` — currently an info diagnostic only | **M** |

---

## What I would add next

Ranked by (size of audience × cleanliness of the declaration), not novelty:

1. **Swift** — `swift-tools-version` is a single line and Swift on Linux/Windows is growing.
2. **Crystal / Nim** — both declare the compiler requirement in a manifest that is already
   parseable; one day each.
3. **Bazel** — `.bazelversion` is literally one line, and bazelisk already respects it.
4. **R** — `renv.lock` is JSON with an `R.Version` key; data-science repos are a large untapped
   population.
5. **Gradle version catalogs** — extend the limited Kotlin plugin support to library aliases,
   `libs.versions.*` references, richer versions, and custom catalog names.

What I would still not add: a C/C++ *compiler* requirement (no portable convention), and
anything whose version only exists after running a build script.

---

## Adding one yourself

An ecosystem is four small pieces, and the tests tell you when one is missing:

1. `ToolKinds` — add the `ToolKind` and its `Ecosystem` mapping.
2. `MarkerCatalog` — add the file names (or extension, via `SuffixMarkers`) that trigger detection.
3. `IProjectParser` — read the declaration, emit a `Requirement` with provenance.
4. `IToolchainProvider` — find installed versions (`PathProbeProvider(tool, "exe")` covers
   anything on PATH; `VersionDirectoryProvider` covers `<manager>/<version>/` layouts) and
   register it in `ToolchainInventory.CreateDefault()`.

Optional but expected: add the file to `MigrationPlanner.FilesFor` so `devtools migrate` works,
and a line in `InstallPlanner.ManagerPreference` so `devtools install` can act.
