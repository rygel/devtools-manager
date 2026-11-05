# Updating toolchains

DevTools Manager updates a toolchain by running the update command its own manager documents. It never downloads or unpacks a toolchain itself, and it never guesses a command. When a manager has no documented non-interactive update command, or the manager is a shell function that cannot run as a plain subprocess, the plan shows the documented route as a manual step.

The same plan is available in three places:

| Where | How |
| --- | --- |
| Desktop | Toolchains page, **Updates** section: *Prepare update options*, select one, *Update selected* |
| CLI | `devtools update` (dry run), `devtools update --apply --yes --plan TOKEN` |
| JSON | `devtools update --json` prints the plan |
| Outdated check | `devtools outdated`, `devtools update --outdated-only`, and the *Only outdated* box in the desktop Updates section |

## Is it outdated?

`devtools outdated` compares each installed version with the newest release of its release line, using the release data from [endoflife.date](https://endoflife.date) that the app already caches for end-of-life warnings (`GET https://endoflife.date/api/<product>.json`, field `latest` on each release line).

- Reading uses only the local cache. `--refresh` is the one thing that contacts the network, and it fetches one small file per product. In the desktop app the data is the same cache the end-of-life setting uses, so *Only outdated* needs that setting on.
- Each version is matched to its release line by prefix (`20.11.0` belongs to line `20`, `3.12.4` to `3.12`). A version that matches no line is reported as unknown, never guessed.
- The result names the newest patch of the installed line, and the newest line when it is newer: `20.11.0 → 20.19.4 · newest line 26.13.0`. Moving to a newer line (a new Node major, say) is a decision, so it is shown but never implied by the patch update.
- A command that covers several installs, such as `rustup update`, counts as outdated when any install it covers is.
- JDK and .NET SDK installs are not compared. The JDK entry on endoflife.date covers Eclipse Temurin only while installs come from many vendors with different build numbers, and the .NET entry lists runtime versions while the inventory holds SDK versions. For the SDK, `dotnet sdk check` lists the latest SDK per feature band.
- `devtools update --outdated-only` keeps only steps whose toolchain is known to be outdated. Tools with no release data are left out, so the plan can be shorter than the full one. Give the same flags to the dry run and to `--apply`, or the plan token will not match.
- The release data does not enter the plan token. The same installs produce the same token with or without it.

## Safety model

The update flow follows the install flow.

1. The plan is built from the index of installed toolchains and printed as a dry run. Nothing runs.
2. Applying needs the plan token printed by the dry run, and `--yes` on the CLI. The token is a fingerprint of the exact commands. If the index changed since the dry run, the token no longer matches and nothing runs.
3. Before each command and after it, a row is written to the audit log (the `audit` table in `devtools.db`, action `update`). If the log cannot be written, no further command runs.
4. In the desktop app, *Update selected* runs only the selected step. The step's own token is checked against the live plan first.
5. A failing command is reported with its output. The remaining commands still run and the run is reported as failed.
6. Rescan afterwards (`devtools scan`, or *Scan roots*) to refresh the index.

Only healthy, non-stale installations are planned. Several installations under one manager that need the same command produce one step. Windows `.cmd` shims such as `npm` and `corepack` are started through `cmd.exe /c`.

WSL distributions are not updated from the Windows app. Run `devtools update` inside the distribution.

## What each manager runs

"Latest" always means what that manager's own resolver picks. The plan does not look up version numbers itself.

### JavaScript runtimes and package managers

| Tool via | Command | Notes |
| --- | --- | --- |
| Node via **fnm** | `fnm install <major>` | Installs the latest release of the installed major. Side by side: the old version stays and fnm's default alias does not move. `fnm default <version>` changes it. [commands](https://raw.githubusercontent.com/Schniz/fnm/master/docs/commands.md) |
| Node via **Volta** | `volta install node@<major>` | A partial version resolves to a matching release. `volta install` also sets the Volta default. Volta's README says the project is unmaintained and recommends mise. [docs](https://docs.volta.sh/guide/understanding) |
| Node via **nvm** | manual | `nvm` is a sourced shell function, not an executable. In a shell with nvm loaded: `nvm ls-remote`, then `nvm install <version>` or `nvm install --lts`. [README](https://github.com/nvm-sh/nvm/blob/master/README.md) |
| Node via **nvm-windows** | manual | In an elevated shell: `nvm list available`, `nvm install <version>` (or `lts`), `nvm use <version>`. Creating the symlink needs administrator rights. [README](https://github.com/coreybutler/nvm-windows/blob/master/README.md) |
| pnpm / Yarn via **Corepack** | `corepack install -g <name>` | Sets the global default for the package manager. `COREPACK_ENABLE_DOWNLOAD_PROMPT=0` suppresses the download prompt. Corepack ships with Node 14.19.0 up to, not including, 25.0.0. [README](https://github.com/nodejs/corepack/blob/main/README.md) |
| **pnpm** (on PATH) | `pnpm self-update` | Outside a project it installs globally. If Homebrew or Corepack manages pnpm, the global install is untouched and the command fails outside a project; use `brew upgrade pnpm`. [docs](https://pnpm.io/cli/self-update) |
| **Yarn** (on PATH) | manual | Yarn 2+ is updated per project with `yarn set version stable`, which edits the project's `package.json`. Yarn 1.x: `npm install --global yarn@1`. [Yarn 4](https://yarnpkg.com/cli/set/version), [Yarn 1](https://classic.yarnpkg.com/en/docs/install) |
| **npm** (on PATH) | `npm install -g npm@latest` | On Windows the npm bundled under Program Files comes before `%APPDATA%\npm` on PATH, so the new global npm can be shadowed. Check `npm config get prefix -g`. Linux may need `sudo`. [docs](https://docs.npmjs.com/try-the-latest-stable-version-of-npm) |
| **Bun** (on PATH) | `bun upgrade` | Replaces the binary in place. If Bun came from a package manager, use `brew upgrade bun` or `scoop update bun`. [docs](https://bun.sh/docs/installation) |
| **Deno** (on PATH) | `deno upgrade` | Replaces the executable in place; latest stable. `deno upgrade lts`, `rc`, `canary` and `--version` select other targets. [docs](https://docs.deno.com/runtime/reference/cli/upgrade/) |
| Deno via **dvm** | `dvm install` | With no version it installs the latest. [README](https://raw.githubusercontent.com/justjavac/dvm/main/README.md) |

### Multi-language managers

| Manager | Commands | Notes |
| --- | --- | --- |
| **mise** | `mise upgrade <tool>` | Stays inside the version range in the config (`node = "20"` gives the newest 20.x). `mise upgrade --bump` goes past the range and rewrites the config; it is not run. mise prunes the old version after a grace period unless something still uses it. The JDK is called `java` in mise. [docs](https://mise.jdx.dev/cli/upgrade.html) |
| **asdf** (0.16+) | `asdf plugin update <plugin>`, then `asdf install <plugin> latest:<prefix>` | The prefix is the major version for Node, Deno and the JDK, and `major.minor` for the others. Old versions stay. Selecting the new version is a separate step (`asdf set`), so the plan does not change what is active. The Node plugin is named `nodejs`. asdf 0.16 removed `asdf update`; upgrade the asdf binary through the OS package manager. [versions](https://asdf-vm.com/manage/versions.html), [plugins](https://asdf-vm.com/manage/plugins.html), [0.16 guide](https://asdf-vm.com/guide/upgrading-to-v0-16.html) |
| **SDKMAN** | manual: `sdk upgrade <candidate>` | `sdk` is a bash function sourced from `~/.sdkman/bin/sdkman-init.sh`. On Windows SDKMAN needs WSL or Git Bash. Setting `sdkman_auto_answer=true` makes it non-interactive. [usage](https://sdkman.io/usage), [install](https://sdkman.io/install) |

### Languages

| Tool via | Command | Notes |
| --- | --- | --- |
| Python via **uv** | `uv python upgrade <major.minor>` | Installs the new patch next to the old one. Virtual environments on that minor version move to the new patch. Only uv-managed CPython can be upgraded. [docs](https://docs.astral.sh/uv/concepts/python-versions/) |
| **uv** (on PATH) | `uv self update` | Works only for the standalone installer. For a pip or package-manager install it is disabled; use that manager. [docs](https://docs.astral.sh/uv/getting-started/installation/) |
| Python via **pyenv** | manual | `pyenv update` (the pyenv-update plugin; on pyenv-win it refreshes the version list), `pyenv install -l`, then `pyenv install <version>`. [pyenv](https://github.com/pyenv/pyenv/blob/master/README.md), [pyenv-win](https://github.com/pyenv-win/pyenv-win/blob/master/README.md) |
| Rust via **rustup** | `rustup update` | Updates the installed toolchains (stable by default) and checks for a rustup self-update. `rustup self update` updates only rustup. [docs](https://rust-lang.github.io/rustup/basics.html) |
| Ruby via **rbenv** | manual | Update ruby-build (`brew upgrade ruby-build`, or `git -C "$(rbenv root)"/plugins/ruby-build pull`), then `rbenv install --list`, `rbenv install <version>`, `rbenv global <version>`. [ruby-build](https://github.com/rbenv/ruby-build/blob/master/README.md), [rbenv](https://github.com/rbenv/rbenv/blob/master/README.md) |
| Terraform via **tfenv** | `tfenv install latest` | A separate install; `tfenv use <version>` changes the active one. [README](https://github.com/tfutils/tfenv/blob/master/README.md) |
| Julia | `juliaup update` | Only when `juliaup` is on PATH. Updates every installed channel to its newest release. [README](https://github.com/JuliaLang/juliaup/blob/main/README.md) |
| Zig via **zvm** | `zvm i stable` | Installing also activates the version unless `--skip-use` is given. [README](https://github.com/tristanisham/zvm/blob/master/README.md) |
| **.NET SDK** | manual | `winget upgrade --id Microsoft.DotNet.SDK.<major>` from an elevated shell, or `dotnet-install -Channel <major>.0` for a per-user install. SDKs are side by side; the CLI uses the newest unless `global.json` pins one. [install](https://learn.microsoft.com/en-us/dotnet/core/install/windows), [script](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script) |
| **Go** | manual | Download from go.dev/dl. On Linux remove the old `/usr/local/go` before extracting; never extract over it. [docs](https://go.dev/doc/manage-install) |
| **Swift** with swiftly on PATH | `swiftly update --assume-yes` | `--assume-yes` disables the confirmation prompts. [reference](https://raw.githubusercontent.com/swiftlang/swiftly/main/Documentation/SwiftlyDocs.docc/swiftly-cli-reference.md) |
| **R** with rig on PATH | `rig add release` | Installs the latest release beside the current ones. The default R changes only with `rig default <version>`. [README](https://github.com/r-lib/rig) |
| **Nim** with choosenim on PATH | `choosenim update stable -y` | `-y` agrees to every question. The maintained repository is `nim-lang/choosenim`. [README](https://github.com/nim-lang/choosenim) |
| **OCaml** with opam on PATH | `opam update`, then `opam upgrade --yes` | Upgrades the packages in the current switch. A newer compiler is a new switch (`opam switch create <switch> <compiler>`), shown as a manual step. [usage](https://opam.ocaml.org/doc/Usage.html) |
| **GHC** with ghcup on PATH | manual | `ghcup list`, `ghcup install ghc`, then `ghcup set ghc <version>`; `ghcup upgrade` updates ghcup itself. The non-interactive flags of these commands are not documented in the guide. [guide](https://www.haskell.org/ghcup/guide/) |

## Not updated automatically

These tools are discovered and shown, but the plan has no step for them. Their documentation either gives no update command or gives one that needs interaction or a project context.

| Tool | Why | What the documentation offers |
| --- | --- | --- |
| Maven, Gradle | Updated per project, by rewriting the wrapper files | `mvn wrapper:wrapper -Dmaven=<version>`; `./gradlew wrapper --gradle-version latest`, run twice so the jar and scripts update as well ([Maven](https://maven.apache.org/tools/wrapper/), [Gradle](https://docs.gradle.org/current/userguide/gradle_wrapper.html)) |
| Kotlin | The command-line page documents install only | Install paths: SDKMAN, Homebrew, Snap, or the GitHub release zip ([page](https://kotlinlang.org/docs/command-line.html)) |
| Scala, scala-cli | The install pages document install only | coursier: `cs update` updates installed apps ([page](https://get-coursier.io/docs/cli-install)) |
| Dart | OS package manager | `winget upgrade --id=Google.DartSDK -e`, `choco upgrade dart-sdk`, `brew upgrade dart`, `apt-get install dart` ([page](https://dart.dev/get-dart)) |
| PHP | No official version manager; distro packages | `apt install php` on Debian and Ubuntu ([page](https://www.php.net/downloads.php)) |
| Swift, R, Nim, OCaml, GHC | Updated only when swiftly, rig, choosenim, opam or ghcup is on PATH (see above) | Without the manager there is no step; each tool's install page lists how to get one |
| Cabal, Stack | ghcup's per-tool install commands are not documented in the guide | `ghcup list` shows what ghcup can install ([guide](https://www.haskell.org/ghcup/guide/)) |
| D | The installer script updates itself, not compilers | `install.sh install dmd` installs a newer compiler beside the old one ([installer](https://raw.githubusercontent.com/dlang/installer/master/script/install.sh)) |
| Crystal | The install pages describe no upgrade path | Windows previews do not upgrade themselves; the GUI installer removes the previous installation ([page](https://crystal-lang.org/install/on_windows/)) |
| Gleam | The install pages describe no upgrade command | Install through Homebrew, winget, mise, asdf, Scoop or a GitHub binary ([page](https://gleam.run/install)) |
| Free Pascal, Lazarus | fpcupdeluxe is a GUI tool and its command line is not documented | Official installers on [lazarus-ide.org](https://www.lazarus-ide.org/index.php?page=downloads) |
| CMake, Meson, Conan | Installed through pip, pipx, or an OS installer | Conan documents `pip install conan --upgrade` ([page](https://docs.conan.io/2/installation.html)) |
| Perl, Lua | perlbrew, plenv, luaenv and hererocks have no single documented in-place update | plenv and luaenv update with `git pull` inside their directory |

## OS package managers

A tool found on PATH is checked for an owning package manager before the tool's own command is used. The owner is read from the executable's real path (symlinks are followed) and, on Linux, from the package database:

| Owner | How it is recognised | Step |
| --- | --- | --- |
| Homebrew | the path contains `/Cellar/<formula>/` | `brew upgrade <formula>` (runs) |
| Scoop | the path is `…/scoop/apps/<app>/…`, or a Scoop shim whose `.shim` file points there | `scoop update <app>` (runs) |
| Chocolatey | the path is `…/chocolatey/lib/<package>/…` | manual: `choco upgrade <package> -y` from an elevated shell |
| apt | `dpkg -S <path>` | manual: `sudo apt-get update && sudo apt-get install --only-upgrade -y <package>` |
| dnf | `rpm -qf <path>` | manual: `sudo dnf upgrade -y <package>` |
| pacman | `pacman -Qo <path>` | manual: `sudo pacman -S --noconfirm <package>` |

Homebrew and Scoop run without elevation, so they are steps. The others need root or an elevated shell, so the plan shows the command and does not run it. When a manager owns the tool, its command replaces the tool's own updater: a Homebrew pnpm is updated with `brew upgrade pnpm` and not with `pnpm self-update`, which fails for managed installs.

Each command below is documented, non-interactive, and upgrades one package.

| Manager | Check | Upgrade one package |
| --- | --- | --- |
| winget | `winget upgrade` lists upgrades; `winget list --upgrade-available` | `winget upgrade --id <Id> -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity`. Pinned packages are skipped without `--include-pinned`; packages with an unknown version are skipped without `--include-unknown`. Exit code `0x8A15002B` means no applicable update. [docs](https://learn.microsoft.com/en-us/windows/package-manager/winget/upgrade) |
| Scoop | `scoop status` | `scoop update <app>`. Held apps and apps with running processes are skipped. [source](https://raw.githubusercontent.com/ScoopInstaller/Scoop/master/libexec/scoop-update.ps1) |
| Chocolatey | `choco outdated` | `choco upgrade <package> -y`. With `useEnhancedExitCodes`, `choco outdated` exits 2 when something is outdated. The default install directory is administrator-only. [docs](https://docs.chocolatey.org/en-us/choco/commands/upgrade/) |
| Homebrew | `brew outdated` | `brew upgrade <formula>`. `brew update` fetches the newest formulae first. [manual](https://docs.brew.sh/Manpage) |
| apt | `apt list --upgradable` (the man page recommends `apt-get` and `apt-cache` in scripts) | `apt-get install --only-upgrade -y <package>` after `apt-get update` [man page](https://manpages.debian.org/trixie/apt/apt-get.8.en.html) |
| dnf | `dnf check-update` exits 100 when updates exist | `dnf upgrade -y <package>` [reference](https://dnf.readthedocs.io/en/latest/command_ref.html) |

Which manager owns an executable can be read from the system: `dpkg -S <file>` on Debian, `rpm -qf <file>` on RPM systems, `pacman -Qo <file>` on Arch (`pacman -Qu` lists its out-of-date packages; [man page](https://man.archlinux.org/man/pacman.8)), `winget list --id <Id>` on Windows, and `scoop which <command>` for Scoop. Scoop installs under `%USERPROFILE%\scoop` unless `SCOOP` overrides it, and Chocolatey under `C:\ProgramData\chocolatey` unless `ChocolateyInstall` overrides it.

## Adding a rule

Rules live in `src/DevTools.Providers/Updates.cs`, in `UpdatePlanner.StepsFor`. A rule needs a command the manager's documentation gives, a form that needs no prompt, and a source to record in the table above. A manager that is a shell function, or whose documentation shows no non-interactive form, becomes a manual step. Add a test in `tests/DevTools.Tests/UpdatePlannerTests.cs` for the command it produces.
