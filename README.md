# devtools

Find every development project on a machine, work out which toolchain versions each one
needs, compare that with what is actually installed, and close the gap.

```
devtools scan C:\Develop        discover projects under the chosen root
devtools doctor                 show missing or incompatible requirements
devtools install                review installation options (dry run by default)
devtools migrate node --to 24   rewrite every Node pin to 24 (plan first, then --apply)
devtools env                    shell environment for the project you are standing in
dotnet run --project src/DevTools.Desktop   open the desktop app
```

C# / .NET 10. Windows, macOS, Linux. The desktop app uses Avalonia 12 and does not require a browser.

**`devtools support` prints the authoritative version of everything below** — it is generated
from the same marker catalogue, parser list and manager table the scanner uses, so the
documentation cannot drift from the code.

> For the full picture — including which ecosystems are only *detected* and which are not
> supported at all — see **[ECOSYSTEMS.md](ECOSYSTEMS.md)**.

---

## Quick start

```bash
dotnet build DevToolsManager.slnx
dotnet run --project src/DevTools.Cli -- scan ~/code
dotnet run --project src/DevTools.Cli -- doctor
dotnet run --project src/DevTools.Desktop
```

Existing binaries in `dist/` predate the desktop replacement and have not been republished. Build the current projects from source as shown above.

---

# What we support

## At a glance

| Ecosystem | Projects are detected and parsed | Version requirements resolved |
|---|---|---|
| **Node / JavaScript** | ✅ 9 markers | node, npm, pnpm, yarn, bun |
| **Deno** | ✅ 4 markers | deno |
| **Java / JVM** | ✅ 12 markers | jdk, maven, gradle, kotlin, scala |
| **.NET** | ✅ 8 markers | dotnet SDK (incl. `rollForward`) |
| **Python** | ✅ 9 markers | python, uv |
| **Go** | ✅ 2 markers | go |
| **Rust** | ✅ 3 markers | rustc |
| **Ruby** | ✅ 3 markers | ruby |
| **PHP** | ✅ 1 marker | php |
| **Dart / Flutter** | ✅ 1 marker | dart |
| **Elixir** | ✅ 2 markers | elixir |
| **Scala** | ✅ 2 markers | scala |
| **Zig** | ✅ 3 markers | zig |
| **Terraform** | ✅ 5 markers | terraform |
| **Julia** | ✅ 2 markers | julia |
| **C / C++** | ✅ 6 markers | cmake, meson, conan |
| **Nix** | ✅ detected only | — |
| **Containers** | ✅ detected, base image reported | — (not managed) |

---

## 1. Project detection

A directory is a project when it holds at least one **strong marker** at its top level. Marker
matching is by name only — no file is opened during discovery.

| Ecosystem | Strong markers |
|---|---|
| **node** | `package.json` · `package-lock.json` · `pnpm-lock.yaml` · `pnpm-workspace.yaml` · `yarn.lock` · `bun.lock` · `bun.lockb` · `.nvmrc` · `.node-version` |
| **deno** | `deno.json` · `deno.jsonc` · `deno.lock` · `.dvmrc` |
| **java** | `pom.xml` · `build.gradle` · `build.gradle.kts` · `settings.gradle` · `settings.gradle.kts` · `gradle.properties` · `gradlew` · `gradlew.bat` · `mvnw` · `mvnw.cmd` · `.sdkmanrc` · `.java-version` |
| **dotnet** | `global.json` · `Directory.Build.props` · `Directory.Packages.props` · `*.csproj` · `*.fsproj` · `*.vbproj` · `*.sln` · `*.slnx` |
| **python** | `pyproject.toml` · `requirements.txt` · `Pipfile` · `poetry.lock` · `uv.lock` · `setup.py` · `setup.cfg` · `.python-version` · `manage.py` |
| **go** | `go.mod` · `go.work` |
| **rust** | `Cargo.toml` · `rust-toolchain.toml` · `rust-toolchain` |
| **ruby** | `Gemfile` · `.ruby-version` · `Rakefile` |
| **php** | `composer.json` |
| **dart** | `pubspec.yaml` |
| **elixir** | `mix.exs` · `mix.lock` |
| **scala** | `build.sbt` · `build.sc` |
| **zig** | `build.zig` · `build.zig.zon` · `.zigversion` |
| **terraform** | `*.tf` · `.terraform-version` · `.terraform.lock.hcl` · `terragrunt.hcl` |
| **julia** | `Project.toml` · `Manifest.toml` |
| **cpp** | `CMakeLists.txt` · `CMakePresets.json` · `meson.build` · `conanfile.py` · `conanfile.txt` · `vcpkg.json` · `xmake.lua` |
| **bazel** | `.bazelversion` · `MODULE.bazel` · `WORKSPACE` · `WORKSPACE.bazel` |
| **pants** | `pants.toml` |
| **mill** | `build.mill` · `build.mill.scala` · `build.mill.yaml` · `.mill-version` |
| **clojure** | `project.clj` · `deps.edn` |
| **nix** | `flake.nix` · `shell.nix` · `devenv.nix` |
| **container** | `Dockerfile` · `docker-compose.yml` · `docker-compose.yaml` · `compose.yaml` · `compose.yml` |

**Weak markers** (raise confidence, never sufficient alone): `README.md`, `LICENSE`,
`.editorconfig`, `.gitignore`, `Makefile`, `Justfile`, `Taskfile.yml`, `.pre-commit-config.yaml`,
`.tool-versions`, `mise.toml`, `.mise.toml`, plus the directories `.git`, `.github`, `.mvn`,
`.devcontainer`, `.vscode`, `.idea`.

**Confidence scoring** (threshold 0.5): base 0.60, +0.15 lockfile, +0.15 `.git`, +0.05 `.github`,
−0.40 inside `fixtures`/`testdata`/`__fixtures__`/`mocks`, −0.25 inside
`examples`/`samples`/`demo`/`playground`, −0.15 when a `Makefile` is the only signal. Below the
threshold the directory is stored as **suppressed** with the reason, never silently dropped.

---

## 2. What is extracted from each project

| File | Tool | Value |
|---|---|---|
| `.nvmrc` | node | exact, major, `node`, `lts/*`, `lts/<codename>` |
| `.node-version` | node | exact |
| `.dvmrc` | deno | exact (dvm's pin file) |
| `package.json` → `engines.node` | node | range (advisory) |
| `package.json` → `engines.npm` | npm | range (advisory) |
| `package.json` → `packageManager` | pnpm / yarn / npm / bun | `name@version` (corepack) |
| `package.json` → `volta.node` | node | exact pin |
| `package.json` → `name`, `workspaces` | — | project name, workspace flag |
| `pnpm-lock.yaml` / `yarn.lock` / `bun.lock(b)` | pnpm / yarn / bun | "this project uses it" |
| `pom.xml` → `maven.compiler.release` → `source` → `java.version` → `target` | jdk | `${…}` resolved from local `<properties>` |
| `pom.xml` → `artifactId`, `<modules>` | — | name, workspace |
| `build.gradle(.kts)` → `languageVersion`, `jvmToolchain(n)`, `sourceCompatibility` | jdk | exact |
| `gradle/wrapper/gradle-wrapper.properties` → `distributionUrl` | gradle | exact version |
| `.mvn/wrapper/maven-wrapper.properties` → `distributionUrl` | maven | exact version |
| `.sdkmanrc` | jdk / maven / gradle | `java=`, `maven=`, `gradle=` candidates |
| `.java-version` | jdk | exact |
| `global.json` → `sdk.version` + `rollForward` | dotnet | pin + policy |
| `*.csproj` → `TargetFramework(s)` | dotnet | `net8.0` → 8.0 (advisory) |
| `pyproject.toml` → `requires-python`, `tool.poetry.dependencies.python` | python | PEP 440 specifier |
| `.python-version` | python | first line |
| `Pipfile` → `[requires] python_version` | python | exact |
| `uv.lock` | uv | "this project uses it" |
| `go.mod` → `go` | go | **floor** (`>=`), not a pin |
| `go.mod` → `toolchain` | go | exact toolchain |
| `rust-toolchain.toml` → `channel` | rust | exact / stable / beta / nightly |
| `Cargo.toml` → `rust-version` | rust | advisory |
| `.ruby-version` | ruby | exact (`ruby-` prefix stripped) |
| `Gemfile` → `ruby "x"` | ruby | exact |
| `composer.json` → `require.php` | php | range or alternatives |
| `pubspec.yaml` → `environment.sdk` | dart | range |
| `mix.exs` → `elixir:` | elixir | `~> 1.15` (pessimistic, same series) |
| `build.sbt` → `scalaVersion :=` | scala | exact |
| `//> using scala` in `*.scala` | scala | exact (scala-cli) |
| `build.gradle(.kts)` → `kotlin("jvm") version` / `id("…kotlin.jvm") version` / `kotlin_version` | kotlin | exact (partial DSL read) |
| applied Kotlin plugin alias in `gradle/libs.versions.toml` | kotlin | literal or `[versions]` reference; partial catalog support |
| `pom.xml` → `<kotlin.version>` | kotlin | exact |
| `build.gradle(.kts)` → `org.scala-lang:scala3-library` | scala | exact (partial DSL read) |
| `.zigversion` | zig | exact |
| `build.zig.zon` → `.minimum_zig_version` | zig | **minimum** |
| `*.tf` → `required_version` | terraform | range |
| `.terraform-version` | terraform | exact (tfenv) |
| `Project.toml` → `[compat] julia` | julia | range |
| `CMakeLists.txt` → `cmake_minimum_required(VERSION …)` | cmake | **minimum** |
| `CMakePresets.json` → `cmakeMinimumRequired` | cmake | **minimum** |
| `meson.build` → `meson_version` | meson | range |
| `conanfile.py` → `required_conan_version` | conan | range |
| `xmake.lua` → `set_xmakever` | xmake | minimum, advisory |
| `.bazelversion` | bazel | exact, advisory; `latest`, `last_green`, `rolling`, `4.x` and `fork/version` are reported, not guessed |
| `pants.toml` → `pants_version` | pants | exact, advisory |
| `.github/workflows/*.yml` → `setup-node` / `setup-java` / `setup-python` / `setup-go` / `setup-dotnet` versions | node, jdk, python, go, dotnet | advisory; conflicts with the project's own declaration raise `ci-drift` |
| `project/build.properties` → `sbt.version` | sbt | exact, advisory |
| `project.clj` → `:min-lein-version` | leiningen | minimum, advisory |
| `//| mill-version:` header · `.mill-version` · `.config/mill-version` | mill | exact, advisory; every declaration found is kept, none preferred |
| `Package.swift` → `// swift-tools-version:` | swift | minimum |
| `shard.yml` → `crystal:` | crystal | advisory |
| `gleam.toml` → `gleam` | gleam | as written |
| `*.nimble` → `requires "nim …"` | nim | as written |
| `DESCRIPTION` → `Depends: R (…)` | r | as written |
| `.tool-versions` / `mise.toml` / `.mise.toml` | any managed tool | asdf / mise pins |
| `Dockerfile` → `FROM …jdk…` | — | informational diagnostic only |

**Precedence**, highest first: user override (reserved) → version-manager pin file → build
descriptor → inherited from the parent project → machine default. A child directory with no
declaration of its own inherits its nearest ancestor's **pins** (marked `inferred` so it does not
pollute conflict detection).

Contradictions are reported, not resolved: `.nvmrc: 18` against `engines: >=20` becomes a
`conflicting-declaration` diagnostic with both file references.

---

## 3. Installed-toolchain discovery

| Tool | Where we look | Version comes from |
|---|---|---|
| **node** | nvm-windows (`%APPDATA%\nvm`, `NVM_HOME`), nvm (`~/.nvm/versions/node`), fnm (`%APPDATA%\fnm\node-versions`, `~/.local/share/fnm`, `~/.fnm`), volta (`~/.volta/tools/image/node`), asdf (`~/.asdf/installs/nodejs`), mise (`~/.local/share/mise/installs/node`), PATH, `C:\Program Files\nodejs` | directory name + `node --version` |
| **npm / pnpm / yarn / bun** | PATH (corepack shims detected) | `<tool> --version` |
| **deno** | dvm (`~/.dvm/versions`), asdf (`~/.asdf/installs/deno`), mise (`~/.local/share/mise/installs/deno`), PATH | directory name, `deno --version` |
| **jdk** | SDKMAN (`~/.sdkman/candidates/java`), `~/.jdks`, `%ProgramFiles%\{Java, Eclipse Adoptium, Microsoft, Zulu, Amazon Corretto, BellSoft, Semeru}`, `/Library/Java/JavaVirtualMachines`, `/usr/lib/jvm`, `/opt/java`, `/usr/java`, `JAVA_HOME` | directory name (vendor suffixes stripped) |
| **maven** | `~/.m2/wrapper/dists/**`, SDKMAN, PATH | wrapper directory name, `mvn -version` |
| **gradle** | `~/.gradle/wrapper/dists/**`, SDKMAN, PATH | wrapper directory name, `gradle -version` |
| **dotnet** | `dotnet --list-sdks` (authoritative) + `--version`, `%ProgramFiles%\dotnet\sdk`, `/usr/local/share/dotnet/sdk`, `/usr/share/dotnet/sdk` | command output |
| **python** | pyenv, asdf, mise, uv (`~/.local/share/uv/python`), `%LOCALAPPDATA%\Programs\Python`, PATH | directory name, `python --version`; Windows Store aliases flagged as stubs |
| **uv** | PATH | `uv --version` |
| **go** | `go version` + `go env GOROOT`, `~/sdk/go*` | command output |
| **rust** | rustup (`~/.rustup/toolchains`), PATH | directory name, or `rustc --version` for channel-only names |
| **ruby** | rbenv, rvm, asdf, mise, PATH | directory name, `ruby --version` |
| **php / dart** | PATH | `--version` |
| **elixir** | asdf, mise, PATH | `Elixir x.y.z` from `elixir --version` |
| **scala** | asdf, mise, PATH | `scala -version` |
| **kotlin** | SDKMAN, PATH | `kotlinc -version` |
| **zig** | zvm (`~/.zvm`), asdf, mise, PATH | directory name, `zig version` |
| **terraform** | tfenv (`~/.tfenv/versions`), asdf, mise, PATH | `terraform version` |
| **julia** | asdf, mise, PATH | `julia --version` |
| **cmake / meson / conan** | PATH | `--version` |

Guarantees: every probe has a 5 s timeout, stdin closed, runs from a neutral temp directory (so
`mvn`/`dotnet` cannot pick up project-local config), and is deduplicated by
`(tool, version, canonical path)` — one JDK reachable through `JAVA_HOME`, SDKMAN and PATH is one
toolchain.

---

## 4. Version managers we drive

| Tool | Managers, in preference order | `devtools install` runs |
|---|---|---|
| node | fnm · nvm-windows · nvm · volta · mise · asdf | `fnm install <v>` / `volta fetch node@<v>` / `mise install node@<v>` / `asdf install nodejs <v>`; nvm is presented as a manual shell command |
| jdk | sdkman | hint with the exact `sdk install java <id>` command *(a bare major cannot be installed by name)* |
| maven | sdkman | manual `sdk install maven <v>` hint |
| gradle | sdkman | manual `sdk install gradle <v>` hint |
| python | pyenv · uv · mise · asdf | `pyenv install <v>` / `uv python install <v>` |
| rust | rustup | `rustup toolchain install <v>` |
| ruby | rbenv · mise · asdf | `rbenv install <v>` |
| pnpm / yarn | corepack | `corepack prepare <tool>@<v>` |
| npm | npm | manual `npm install -g npm@<v>` hint; changes the global version |
| deno | dvm · mise · asdf | `dvm install <v>` / `mise install deno@<v>` / `asdf install deno <v>` |
| elixir | asdf · mise | `asdf install elixir <v>` / `mise install elixir@<v>` |
| scala | — | manual hint (scala-cli / coursier) |
| kotlin | sdkman | manual `sdk install kotlin <v>` hint |
| zig | zvm · asdf · mise | `zvm i <v>` / `asdf install zig <v>` / `mise install zig@<v>` |
| terraform | tfenv · asdf · mise | `tfenv install <v>` / `asdf install terraform <v>` |
| julia | juliaup · asdf · mise | `juliaup add <v>` / `asdf install julia <v>` |
| cmake / meson / conan | — | manual hint (package manager / pipx) |
| bun | — | manual hint (`npm install -g bun`) |
| dotnet | — | manual hint (`dotnet-install --version <v>`) |
| go | — | manual hint (`go install golang.org/dl/go<v>@latest`) |
| php / dart | — | manual hint |

`✓` in `devtools support` marks the managers found on the current machine. If no manager is
present, the plan says so instead of emitting a command that would fail.

**Activation** happens through `devtools env` / `devtools shell`, which emit an environment patch
rather than sourcing vendor scripts:

| Tool | Environment effect |
|---|---|
| node, pnpm, yarn, bun, python, ruby, php, dart | `PATH` prepend |
| jdk | `JAVA_HOME` + `PATH` |
| maven / gradle | `MAVEN_HOME` / `GRADLE_HOME` + `PATH` |
| dotnet | `DOTNET_ROOT` (resolved to the install root, not the SDK directory) + `PATH` |
| go | `GOROOT` + `PATH` |
| rust | `PATH` |

Shells: `pwsh`, `powershell`, `bash`, `zsh`, `fish`.

---

Updating installed toolchains uses a separate table, with the sources for each command: **[docs/UPDATING.md](docs/UPDATING.md)**.

## 5. Version syntax

Every ecosystem's notation is normalised into one comparable constraint.

| Form | Example | Ecosystems |
|---|---|---|
| exact | `18.20.2` · `8.0.100` | all |
| series | `20` · `20.x` · `1.8.x` | node, java, php |
| comparator | `>=18 <21` · `>=3.11,<4` | all |
| caret | `^20.0.0` → `>=20 <21` · `^0.2.3` → `<0.3.0` | node, php, ruby |
| tilde | `~1.2.3` → `>=1.2.3 <1.3.0` | node, php |
| PEP 440 | `~=3.10` → `>=3.10 <4` · `~=3.10.1` → `>=3.10.1 <3.11` | python |
| Maven interval | `[17,22)` · `(,1.0]` | java |
| alternatives | `^7.3\|^8.0` · `^8.0 \|\| ^7.4` | php, npm |
| LTS alias | `lts/*` · `lts/iron` | node |
| named channel | `stable` · `beta` · `nightly` | rust |
| distribution id | `21.0.2-tem` | jdk (SDKMAN) |
| wildcard / any | `*` · `latest` · `x` | all |

Ecosystem quirks handled deliberately:

- **`1.8` is Java 8**, `JavaVersion.VERSION_1_8` is Java 8, `21.0.2+13` is Java 21.0.2.
- **Maven ordering is numeric**: `3.9.10 > 3.9.9`, not locale collation.
- **A partial version is a series**: `.nvmrc: 18` matches 18.20.2; `global.json: 10.0.300` needs
  an exact match under `rollForward: disable`.
- **`.NET `rollForward`** — `disable`, `patch`, `feature`, `latestFeature`, `minor`,
  `latestMinor`, `major`, `latestMajor`. `10.0.300` + `latestFeature` is satisfied by `10.0.403`.
- **`go 1.27.0` is a floor**, satisfied by Go 1.27.3.
- **`rust-version` and `engines` are advisory** — they warn, they do not fail.

---

## 6. Resolution outcomes

| Status | Meaning |
|---|---|
| `satisfied` | an installed toolchain matches; the matching version is shown |
| `missing` | that exact version is not installed (the nearest installed version is offered) |
| `unsatisfied-range` | the tool is installed, but nothing falls inside the required range |
| `unknown` | the declaration could not be interpreted; always carries a reason |
| `not-managed` | a container or remote toolchain, deliberately not managed |
| `ignored` | reserved for user override |
| `conflict` | another project demands an incompatible exact version |

Cross-project conflicts are aggregated **per version pair**, ignore inherited pins, ignore
Gradle/Maven wrappers (each project fetches its own), and reconcile .NET pins through
`rollForward`.

---

## 7. Mass migration

`devtools migrate` rewrites version declarations across every project at once — the thing you
actually want when moving 300 Node projects off an end-of-life release.

```bash
devtools migrate node --to 24                    # plan only, writes nothing
devtools migrate node --to 24 --diff             # show a representative changed line per file
devtools migrate node --to 24 --from 18          # only projects currently on 18.x
devtools migrate node --to 24 --apply --yes --plan <token>  # apply the reviewed plan, with backups
devtools migrate node --to 24 --include-ranges   # also rewrite engines/requires ranges
```

| Tool | Files rewritten |
|---|---|
| **node** | `.nvmrc` · `.node-version` · `.tool-versions` · `mise.toml` · `.mise.toml` · `package.json` (`volta.node`; `engines.node` only with `--include-ranges`) |
| **deno** | `.dvmrc` · `.tool-versions` · `mise.toml` · `.mise.toml` |
| **python** | `.python-version` · `.tool-versions` · `mise.toml` · `.mise.toml` · `pyproject.toml` (`requires-python`, ranges only) |
| **jdk** | `.java-version` · `.sdkmanrc` (needs a vendor id such as `25.0.2-tem`) · `.tool-versions` · `mise.toml` · `.mise.toml` |
| **gradle** | `gradle/wrapper/gradle-wrapper.properties`: an official distribution URL without an existing checksum; wrapper scripts and JAR remain unchanged |
| **ruby** | `.ruby-version` · `.tool-versions` · `mise.toml` · `.mise.toml` |
| **dotnet** | `global.json` (`sdk.version`; `rollForward` untouched) |

Safety properties, all enforced in code rather than convention:

- **Plan first.** Without `--apply`, nothing is written.
- **Review before applying.** `--apply` requires `--yes` and the token from the plan with the same filters.
- **Byte-exact backup** of every file before it changes, under `<store>/backups/<plan-id>/`.
- **Optimistic concurrency** — a changed plan is refused and must be reviewed again; a subsequent file change stops the migration.
- **Path containment** — an edit resolving outside its project is refused.
- **Bounded review** — non-UTF-8 files and files over 1 MiB are skipped; a plan retains at most 8 Mi characters of before-and-after content and reports further files as skipped. Symbolic-link paths are skipped.
- **Gradle wrapper limits** — checksum-protected and custom-hosted distributions require manual handling. Gradle 9 and later require a full `major.minor.patch` target. After changing the URL, use Gradle's wrapper task if the scripts and wrapper JAR also need updating. See the [Gradle wrapper guide](https://docs.gradle.org/current/userguide/gradle_wrapper.html).
- **JSON is spliced, not re-serialised.** `package.json` keeps its formatting and key order, and
  a file that does not parse in full is refused rather than half-edited.
- **Already-on-target is a no-op.** Migrating "to 24" leaves a project pinned to `24.4.1` alone.
- **Nothing is created.** A project with no `.nvmrc` does not get one.
- Every write is recorded in the `audit` table of `devtools.db`.

## 8. Commands

| Command | Purpose |
|---|---|
| `devtools scan [roots…]` | build/refresh the index |
| `devtools list` | projects with toolchain status |
| `devtools show <path\|name>` | requirement-by-requirement detail |
| `devtools doctor [path]` | machine (or one project) health |
| `devtools toolchains` | every installed toolchain |
| `devtools export --format mise --project PATH` | that project's own declarations as a `mise.toml` (plain versions only; ranges, java and tools without a mise registry entry are listed as comments, advisory CI/engines values are ignored) |
| `devtools audit PATH` | known vulnerabilities (osv.dev) in the project's locked dependencies from `package-lock.json` (lockfile v2/v3) and `Cargo.lock` registry crates. Running it sends each package's ecosystem, name and version to api.osv.dev and nothing else. Only lockfiles are read, because a manifest range does not say what is installed. A failed lookup is reported as incomplete, never as clean; `--strict` exits 1 on findings or an incomplete result, `--json` for scripts |
| `devtools eol` | end-of-life status of installed toolchains from the local cache; `--refresh` fetches it from endoflife.date (the only command that contacts the network for this), `--json` for scripts |
| `devtools ignore <path>` / `unignore <path>` | hide an indexed project from lists and plans, or restore it; the choice survives rescans and is written to the audit log |
| `devtools verify-toolchain <path>` | preview a version probe for an unverified installation; use `--yes` to run it |
| `devtools probe-effective <project-path> <tool>` | preview a project-directory version probe; `--yes` runs it using the current process environment |
| `devtools environments` | list the local environment and available WSL distributions |
| `devtools env [path]` | shell environment for a project |
| `devtools shell [path]` | open a shell with that environment |
| `devtools install` | plan missing toolchains (dry run) |
| `devtools outdated` | installed toolchains with a newer release, from the local endoflife.date cache; `--refresh` fetches it, `--json` for scripts. JDK and .NET SDK installs are not compared |
| `devtools update [--tool NAME]` | plan toolchain updates using each manager's own documented update command (dry run); `--outdated-only` keeps steps known to be outdated, `--refresh` refreshes the release data, `--apply --yes --plan TOKEN` runs them and writes each to the audit log. The desktop app has the same flow in the Toolchains page. Every command and its source is in [docs/UPDATING.md](docs/UPDATING.md) |
| `devtools prune` | plan artifact cleanup (dry run) |
| `devtools migrate <tool> --to <v>` | rewrite version pins across every project |
| `devtools disk` | disk usage by project |
| `devtools export` | emit `mise` / `sdkmanrc` / `lock` |
| `devtools roots add\|remove\|list` | manage scan roots |
| `devtools support` | this catalogue, from code |
| `devtools version` / `help` | |

Flags:

| Command | Flags |
|---|---|
| `scan` | `--depth N` · `--measure` · `--no-inventory` · `--no-probe` · `--follow-symlinks` · `--json` |
| `list` | `--tool` · `--ecosystem` · `--framework` · `--status missing\|ok` · `--stale DAYS` · `--limit N` · `--all` · `--json` |
| `show` | `--explain` · `--json` |
| `doctor` | `--json` |
| `toolchains` | `--tool` · `--unused` · `--json` |
| `frameworks` | frameworks your projects declare, with project counts and the declared versions · `--json` |
| `env` | `--shell pwsh\|bash\|zsh\|fish` · `--json` |
| `shell` | `--shell` |
| `install` | `--tool` · `--version` · `--plan` · `--apply` · `--yes` · `--json` |
| `prune` | `--project PATH` · `--artifact NAME` · `--older-than DAYS` · `--measure` · `--apply` · `--yes` · `--plan TOKEN` · `--json` |
| `migrate` | `--to VERSION` · `--from VERSION` · `--diff` · `--include-ranges` · `--plan TOKEN` · `--apply` · `--yes` · `--limit N` · `--json` |
| `disk` | `--json` |
| `export` | `--format mise\|sdkmanrc\|lock` |
| `support` | `--json` |

Exit codes: `0` fine · `1` problems found · `2` error. `doctor` and `show` returning `1` makes
them usable in a pre-commit hook.

---

## 9. Desktop app

Run `dotnet run --project src/DevTools.Desktop` to open the Avalonia desktop app. It uses the same local index and scanner as the CLI. A sidebar switches between Projects, Toolchains, Disk, Migration and Supported, plus WSL on Windows. **Supported** lists every toolchain and framework this build understands by ecosystem, with the marker files that identify each ecosystem and whether a tool is found on the machine, installable, or only read from project files; it has a filter box and is generated from the same catalogues the scanner uses. **Settings** at the bottom of the sidebar opens a dialog for scan roots, excluded directory names, maximum depth, symbolic links and the disk-measurement default; these are the same values the CLI reads. Press **Scan roots** to scan. Projects, toolchains and disk usage are lists with a detail pane for the selected item. Projects and Toolchains have a search box (toolchains match tool name, version, manager or path), and Supported has a filter box. **Settings → Look up end-of-life dates** (off by default) makes the desktop app fetch each installed tool's release lines from endoflife.date in the background, cache them for 7 days in `eol-cache.json`, and show an "end of life" or "ends <date>" pill on toolchains. Only product names such as `nodejs` or `python` are requested. End-of-life data is informational and never changes a project's status. Selecting a local project shows **Open folder**, **Terminal**, **VS Code** and **Copy mise.toml** buttons; the first three are disabled when the platform has no command for them (Linux has no portable terminal launcher; VS Code needs `code` on PATH) and for projects inside WSL. The Toolchains list is grouped by tool, with a divider, icon and installed count per group (all Python together, uv together, and so on) and the newest version first within each group. Browse and filter projects with their requirements and sources visible, verify selected unverified toolchains, prepare and review installation options and migration edits, and inspect individual local or loaded WSL cleanup targets before moving one to trash. WSL actions require a current CLI inside that distribution.

For the CLI, run `devtools install` to review the plan. Apply the displayed plan with `devtools install --apply --yes --plan <token>`. For one option, include its displayed `--tool` and `--version` and use that option's token. The plan is rechecked before a command runs; version ranges requiring a choice and shell-function managers are manual. Rescan after installation.

To load a WSL index in the Windows desktop app, build the CLI inside that distribution using a Linux-native checkout and its .NET 10 SDK. Publish it to a dedicated directory and link it without overwriting an existing command:

```sh
dotnet publish src/DevTools.Cli -o "$HOME/.local/share/devtools-manager"
mkdir -p "$HOME/.local/bin"
ln -s "$HOME/.local/share/devtools-manager/devtools" "$HOME/.local/bin/devtools"
"$HOME/.local/bin/devtools" scan "$HOME/code"
```

Use your actual scan root in the last command. In the Windows desktop app, open **WSL**, select **Find** under WSL distributions, choose a distribution from the list, and select **Load index**. The configured roots are shown in the list; **Rescan** explicitly rescans those roots in that distribution. The app labels each environment and keeps its index separate. `devtools environments` lists available distributions; `devtools environments --load Debian --json` reads one index, and adding `--scan --yes` explicitly rescans its configured roots. A distribution without the CLI or an index reports an error instead of appearing empty.

For CLI cleanup of one WSL project, `devtools environments --load Debian --cleanup-plan /path/to/project` lists targets and individual plan tokens. After reviewing, use `devtools environments --load Debian --cleanup /path/to/project --artifact node_modules --plan TOKEN --yes`; substitute the reviewed artifact and token.

---

## 10. Exports

| Format | Content |
|---|---|
| `mise` | `[tools]` block with the dominant pin per tool across the machine |
| `sdkmanrc` | `java=`, `maven=`, `gradle=` |
| `lock` | JSON manifest: toolchains + per-project requirements |

---

## 11. What gets pruned

| Class | Directories |
|---|---|
| always | `node_modules`, `bower_components`, `vendor`, `target`, `.git`, `.svn`, `.hg`, `.jj`, `.gradle`, `.venv`, `venv`, `env`, `__pycache__`, `.tox`, `.mypy_cache`, `.pytest_cache`, `.next`, `.nuxt`, `.svelte-kit`, `.parcel-cache`, `.turbo`, `.nx`, `.expo`, `coverage`, `.nyc_output`, `Pods`, `.terraform`, `.serverless`, `dist`, `out`, `.idea`, `.vs`, `.fleet`, and every known toolchain/cache root (`.cargo`, `.rustup`, `.nuget`, `.m2`, `.npm`, `.pnpm-store`, `.yarn`, `.bun`, `.nvm`, `.fnm`, `.volta`, `.asdf`, `.sdkman`, `.pyenv`, `.jdks`, `.dotnet`, `.local`, `.cache`, `.config`, `AppData`, `Library`) |
| inside a project | `bin`, `obj`, `build`, `Debug`, `Release`, `artifacts`, `packages` |
| volume root only | `Windows`, `Program Files`, `ProgramData`, `$Recycle.Bin`, `System Volume Information`, `proc`, `sys`, `dev`, `usr`, `var`, `opt`, `System`, `private`, … |
| hidden | any dot-directory except `.git`, `.mvn`, `.devcontainer`, `.github`, `.vscode`, `.idea` |

Never followed: junctions, symlinks and cloud placeholders (OneDrive/Dropbox files are skipped
by attribute, so a scan never triggers a download). Every prune is counted and reported by
reason.

---

## 12. Platforms

| | Windows | macOS | Linux |
|---|---|---|---|
| Index & config | `%APPDATA%\devtools` | `~/Library/Application Support/devtools` | `${XDG_DATA_HOME:-~/.local/share}/devtools` |
| Publish RIDs | `win-x64`, `win-arm64` | `osx-x64`, `osx-arm64` | `linux-x64`, `linux-arm64` |
| Paths | `\\?\`-safe .NET APIs, long paths | ✓ | XDG ✓ |
| Reparse points / symlinks | detected, never followed | ✓ | ✓ |

Set `DEVTOOLS_HOME` to relocate the store — useful for CI, a portable install, or trying a
`migrate --apply` against a copy without touching the real index.

---

# Not supported (yet)

Being explicit, because "it didn't do X" should be a documented answer rather than a surprise.

- **`migrate` only rewrites declarations that already exist.** It never creates a `.nvmrc` or
  `global.json`, and it does not touch `pom.xml`, `go.mod`, `Cargo.toml`, `Dockerfile`, or CI
  workflow YAML.
- **No file watchers.** Rescans are explicit (`devtools scan`); there is no background daemon.
- **Incomplete scans retain stale records.** Projects under unavailable roots or unreadable directories remain in the index and are marked stale until a successful rescan. Stale projects are excluded from install and prune plans.
- **Manually unpacked installations:** scanned roots are checked for conventionally named `jdk-*`, `node-v*`, `apache-maven-*`, and `gradle-*` directories and for Java, Maven, Gradle, or Node executables in other `bin` directories. Arbitrarily named candidates have an unknown version. These entries remain unverified and cannot satisfy requirements until an explicit `verify-toolchain PATH --yes` probe. Windows batch launcher paths containing shell metacharacters are refused.
- **No MFT/USN fast path.** The portable walk is the only mode (9 s for 141k directories, warm).
- **No provider plugins.** Providers are compiled in; there is no TOML/WASM extension point.
- **No general dependency scanning.** Libraries are intentionally out of scope — this is not an SCA tool. The one exception is a short, curated list of application frameworks (see ECOSYSTEMS.md, "Frameworks"), read from the dependency sections of manifests.
- **Frameworks are never installed or managed.** A framework is a declared dependency of a project, reported with the version the manifest states; it has no installed/missing status and does not affect exit codes. Toolchains (runtimes, compilers, package managers, build tools) are the things that are installed, resolved against requirements and managed.
- **`deno.json` is detected and versioned through `.dvmrc`**, but Deno is not installed for you
  unless dvm, mise or asdf is present. `bun` is detected and versioned, but not
  installable through a manager we support.
- **Containers are informational.** `FROM` lines are reported; images are never pulled or managed.
- **WSL indexes are separate.** On Windows, the desktop app can list distributions, load an existing WSL index, and explicitly rescan its configured roots. The Linux `devtools` executable must already be installed and scanned in the distribution. Other remote roots are not supported.
- **Environment inventories remain separate.** A scan records Windows, Linux, macOS, or the WSL distribution name; a known index from another environment is refused. Matching means a toolchain is available in that inventory, not that a normal project shell has been verified to select it.
- **`prune --apply` uses the Windows Recycle Bin or Linux `gio trash`**, behind `--yes` and the token printed by the current dry run, and refuses unsafe paths. Repeat the same filters with `--apply --yes --plan TOKEN`; changed target lists require review. Apply is unavailable when no supported trash service is available. `--measure` reports logical file bytes for each target; unreadable targets report an unavailable size.
- **Prune JSON format:** `prune --json` returns an object containing the overall `plan` and a `targets` array with per-target plan tokens and availability. Adding `--apply --yes --plan TOKEN` runs the reviewed operation and returns its results as JSON.
- **No telemetry, no cloud, no account.** Network is touched only by `install --apply`.
- **No `devtools sync`** from a committed lockfile — `export` writes one, nothing consumes it yet.

---

## How it works

Three phases, described in full in [DESIGN.md](DESIGN.md):

1. **Discovery** — breadth-first, level-parallel walk that reads *directory entries only*.
   One `readdir` per directory, zero file opens, aggressive pruning, every prune counted.
2. **Extraction** — for each candidate, parse ~50 marker files with a size cap; a malformed
   manifest degrades to `unknown` with a reason instead of aborting the project.
3. **Resolution** — match requirements against the inventory using the normalised constraint
   algebra, keeping provenance so every verdict can explain itself (`show --explain`).

```
src/DevTools.Core        versions, constraints, resolution      (no I/O at all)
src/DevTools.Scan        walker, pruning, markers, parsers, git
src/DevTools.Providers   toolchain inventory, install plans, env patches
src/DevTools.Store       index, config, audit log
src/DevTools.Cli         the devtools binary
src/DevTools.Desktop     Avalonia desktop application
tests/DevTools.Tests     automated test suite
ECOSYSTEMS.md            the tier list: resolved / detected / not supported
docs/UPDATING.md         how each toolchain is updated, with sources
```

## Testing

```bash
dotnet test tests/DevTools.Tests/DevTools.Tests.csproj
```

Tests cover the constraint algebra (Java `1.8`, Maven ordering, PEP 440, unions, caret and
tilde), .NET `rollForward` policies, every parser against in-memory fixture projects, the
pruning policy, confidence scoring, and an end-to-end walk over a real temporary directory tree.

## Limits worth knowing

- Requirements are resolved **offline**: a Maven parent POM that is not in the local repository
  becomes `unknown`, never a network fetch.
- The index is JSON (~1.6 MB at 3,000 projects), behind `JsonStore` if it ever needs to become
  SQLite.
- Version detection trusts a directory name when it is unambiguous and probes the binary when it
  is not; a pathologically renamed install directory can still be misread.

## Third-party notices

The tool icons in the desktop app are paths from [Simple Icons](https://simpleicons.org), released under CC0 1.0. Each logo and brand name remains a trademark of its owner and is used only to identify that tool. GHC, Cabal and Stack share the Haskell mark. Meson, Pants, xmake, Mill, sbt, Leiningen and Free Pascal have no Simple Icons entry, so they show a letter tile.
