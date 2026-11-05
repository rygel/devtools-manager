# DevTools Manager — Design Document

**Status:** Draft v0.1
**Date:** 2026-11-02
**Working name:** `devtools` (binary), `devtoolsd` (daemon)

> **Reading this document:** Sections 0–18 describe the original proposal, not a guarantee of current behavior. The current executable is the C# CLI and Avalonia desktop app; there is no daemon or active web server. See [README.md](README.md) for supported commands and limitations and §19 for implementation departures. Targets and benchmarks below remain unverified unless a reproducible result is explicitly recorded.

> **Implementation note (2026-11-02).** This document originally specified a Rust core with
> Tauri. The implemented build targets **.NET 10** instead, at the request of the project owner,
> and uses an Avalonia desktop UI rather than a Tauri shell. The parts that mattered survived the
> change intact: the three-phase directed scan, the marker/confidence model, the single
> constraint algebra, provenance on every requirement, and the safety rules around mutation.
> The differences worth knowing about are recorded in §19.

---

## 0. TL;DR

`devtools` scans a machine's disks, finds every development project, works out **which toolchain
versions each one needs**, inventories **which toolchain versions are actually installed** (Node,
Java, Maven, Gradle, .NET, Python, Go, Rust, Ruby…), and then closes the gap: install what is
missing, pin what is ambiguous, show what is wasting disk, and hand you a shell with the right
versions active.

The three things that make it hard, and therefore the three things this document spends most of
its words on:

1. **Scanning a whole drive without being a tax on the machine.** A naive recursive walk of a
   2M-directory NTFS volume is minutes of I/O and will trip antivirus. The design uses a
   marker-file BFS with aggressive pruning, an MFT/USN fast path on Windows, and a
   content-addressed cache so rescans are incremental.
2. **Resolving a version *requirement*, not just spotting a file.** `.nvmrc` says `lts/*`,
   `pom.xml` says `1.8` (meaning Java 8), `global.json` says `8.0.100` with
   `rollForward: latestFeature`, `pyproject.toml` says `>=3.11,<4`. All of these must normalise
   into one comparable constraint model, with provenance, so the UI can say *why*.
3. **Touching the filesystem safely.** The app reads broadly and writes narrowly. Mutations are
   dry-run first, delegated to the existing version manager where one exists, logged, and
   reversible.

**Stack decision up front:** Rust core + SQLite + Tauri v2 shell (React/TS UI) + a first-class
CLI. Rationale and rejected alternatives in §5.

**Scope decision up front:** read-only discovery and *diagnosis* are v1. Installing, pinning and
pruning are v1.1 behind an explicit "allow mutations" switch. Deleting anything that isn't a
cache directory is out of scope for v1 entirely.

---

## 1. Problem

### 1.1 The situation

A developer's disk accumulates projects from years of work. Each carries an implicit, partially
declared toolchain requirement:

- `~/work/api` needs Node 18 (`.nvmrc`), pnpm 8 (`packageManager`), and Postgres 14 in Docker.
- `~/clients/acme` is Java. `pom.xml` pins Maven via wrapper; `maven.compiler.release` is 17.
- `~/oss/thing` is Gradle. `gradle-wrapper.properties` pins Gradle 8.5; the build declares a
  Java toolchain of 21.
- `~/archive/2019-app` needs Node 12 and will not build with anything newer — but does the
  machine still have Node 12? Only `nvm` can answer that, and only if you ask it.
- `C:\Develop\Claude\apache-maven-3.9.12` — a Maven install unpacked into a random folder,
  unrelated to any project, invisible to `mvn` on PATH, wasting 10 MB and confusing everyone.

The consequences are mundane and constant:

- **"Works on my machine" in reverse** — a project fails to build and the cause is a silently
  wrong runtime, found 20 minutes later.
- **Invisible disk usage** — 40 `node_modules` trees, 6 JDK distributions, 4 Gradle
  distributions, and every npm/pnpm/Maven/Gradle cache. Nobody knows the total.
- **Untracked global installs** — toolchains installed by Chocolatey, winget, Homebrew, SDKMAN,
  asdf, mise, volta, fnm, nvm-for-Windows, GraalVM's own installer, or a manual unzip, with no
  single view of what exists.
- **Onboarding cost** — a new machine requires archaeology: open each repo, read each marker
  file, install each version by hand.

### 1.2 What people do today

| Approach | Fails at |
|---|---|
| `nvm ls` / `sdk list java` / `dotnet --list-sdks` | One ecosystem at a time, no project context, no disk numbers |
| `Everything` / `fd` / Spotlight | Finds files, not meaning; no versions resolved, no management |
| `direnv` + `.envrc` | Only works in projects that already adopted it |
| `mise` / `asdf` | Excellent managers, but *only* for projects that opted in and only for tools they manage; nothing about legacy folders, nothing about disk |
| "Just reinstall everything" | Hours, and destroys the ability to build the 2019 project |

There is a gap between **file search** and **version managers**: nothing builds a single model of
*project → requirement → installed toolchain → action*.

### 1.3 Users

**Primary — the polyglot professional.** 5+ years, 30–300 repos, 3–6 ecosystems, 2–3 machines.
Adopts tooling easily but has lost the thread of what's installed where. This is the person who
has both `nvm` and `fnm` and doesn't remember why.

**Secondary — the returning developer.** Opening a project untouched for 18 months. Needs the
app to say "this needs Node 16.20.2, you have 20 and 22; here's the one command."

**Secondary — the new-machine provisioner.** Has a reference machine and a blank laptop. Wants
"give me everything this disk has, as a manifest."

**Tertiary — the team lead.** Wants a `devtools.lock` committed next to `README.md` so
`devtools sync` sets up any machine.

### 1.4 Non-goals (explicit)

- Not a package manager. It does not replace npm/pnpm/Maven/Gradle; it *drives* them.
- Not a dependency scanner or vulnerability scanner (no SCA).
- Not a build system, CI runner, or task orchestrator.
- Not a code indexer or LSP.
- No cloud service, no account, no telemetry in v1.
- Does not manage container images or WSL distros as *toolchains* in v1 (detects them as
  *requirements*, informational only — see §7.3).

---

## 2. Core concepts

Precise vocabulary, because the rest of the document uses these words in exactly one way.

| Term | Definition |
|---|---|
| **Root** | A directory the user has authorised scanning of (`C:\Develop`, `D:\`, `~/code`). Scans only ever begin at roots. |
| **Project** | A directory that is a unit of development work, identified by ≥1 strong marker. Has an identity that survives moves (see §6.6). |
| **Marker** | A file or directory whose presence at depth 0 of a candidate directory indicates a project and/or an ecosystem (`pom.xml`, `package.json`, `.git`). |
| **Ecosystem** | A language/runtime family: `node`, `java`, `dotnet`, `python`, `go`, `rust`, `ruby`, `php`, `elixir`, `cpp`, `flutter`. |
| **Tool** | A specific executable/installation family within an ecosystem: `node`, `pnpm`, `yarn`, `bun`, `jdk`, `maven`, `gradle`, `dotnet-sdk`, `python`, `uv`, `go`, `rustc`, `ruby`. |
| **Toolchain** | One concrete installed instance of a Tool: `node 20.11.0 via fnm at ~/.local/share/fnm/...`. |
| **Requirement** | A Project's declared or inferred need: "tool `node`, constraint `^20.11`, from `.nvmrc` line 1". |
| **Constraint** | Normalised, comparable form of a requirement's version spec (`Exact`, `Range`, `Any`, `Lts`, `Latest`, `Unknown`). |
| **Resolution** | The outcome of matching a Requirement against inventory: `Satisfied(toolchain)`, `Missing`, `UnsatisfiedRange`, `NotManaged`, `Unknown`, `Ignored`. |
| **Provider** | A plugin that knows one Tool: how to find installs, list versions, install, pin, and activate. |
| **Action** | Any mutation: install, pin, unpin, prune, cache-clean, write-lockfile. Always audited. |

The critical distinction: a **Requirement** is a property of a project on disk; a **Toolchain** is
a property of the machine. The app's whole value is the join between them.

---

## 3. Goals and success criteria

### 3.1 Product goals

- **G1 — Completeness.** On a corpus of 500 real-world repos, detect ≥98% of projects, with
  ≤2% false positives, for the ten shipped ecosystems.
- **G2 — Explanation.** For every project, every requirement is traceable to a file and line (or
  an explicit inference rule). "Why does it say 17?" always has an answer.
- **G3 — Actionability.** Every non-satisfied requirement offers a concrete, one-click/one-command
  fix that the app can execute or explain why it cannot.
- **G4 — Safety.** No unintended writes. Every mutation is dry-run-able, logged, and reversible
  where the underlying tool allows.
- **G5 — Cheapness.** A cold scan of a 1M-file volume completes in one coffee sip; a warm rescan
  is invisible.

### 3.2 Measurable acceptance

| Metric | Target | How verified |
|---|---|---|
| Cold full scan, 1M files / 150k dirs, NVMe | ≤ 90 s (walk) / ≤ 25 s (USN fast path) | Benchmark corpus, §14.4 |
| Warm incremental after touch of one marker | ≤ 2 s to index updated | Bench harness |
| Idle CPU after scan | ≤ 0.3% (0 when watching disabled) | 10-min profile |
| Peak RSS during scan | ≤ 300 MB | Bench harness |
| Project detection recall / precision | ≥98% / ≥97% | Hand-labelled corpus of 500 repos |
| Requirement extraction correctness | ≥99% per documented parser case | Golden-file tests (§14.2) |
| Time to first useful screen (fresh install, default roots) | ≤ 60 s | Manual script |
| UI responsiveness with 10k projects | 60 fps list scroll, <100 ms filter | Profiling |

---

## 4. User stories

Written as acceptance-shaped statements; these drive the CLI and UI surfaces.

**Discovery**
1. As a user, I point the app at `C:\Develop` and see every project under it, grouped by
   ecosystem, within a minute.
2. As a user, I see which project is which even when the folder name is `repo`, by reading the
   `origin` remote and the manifest `name`.
3. As a user, I can exclude a folder (a vendored dump, a VM image store) and it is never scanned
   again.

**Diagnosis**
4. As a user, for any project I see "Node 20.11.0 required by `.nvmrc` → **not installed**".
5. As a user, I see a single dashboard: N projects need something you don't have; here they are,
   sorted by how recently you touched them.
6. As a user, I see two projects demanding mutually exclusive Node versions, and I am told which
   managers can hold both (nvm/fnm can; a single system install cannot).
7. As a user, I can see what a project *would* use right now if I opened a shell there, versus
   what it declares — a "declared vs effective" diff.

**Management**
8. As a user, I select 6 missing versions across 4 tools and install them in one action, seeing a
   dry-run list first.
9. As a user, I get a shell in which the right toolchain is active, without permanently changing
   global state: `devtools shell ~/work/api`.
10. As a user, I reclaim disk: "delete `node_modules` in 31 projects untouched for 90 days"
    with a byte total and a Recycle-Bin-backed delete.

**Portability**
11. As a user, I export the whole machine's toolchain requirements as one manifest and reproduce
    it elsewhere.
12. As a team lead, I commit `devtools.lock`; a colleague runs `devtools sync` and gets an
    environment that matches.

---

## 5. Architecture

### 5.1 Shape

A single binary that can run headless, plus an optional GUI. The expensive, filesystem-heavy work
lives in one place and is consumed by both surfaces.

```
┌───────────────────────────────────────────────────────────────────────────┐
│                              UI LAYER                                     │
│  ┌──────────────────────┐  ┌──────────────────┐  ┌────────────────────┐   │
│  │ Tauri v2 desktop app │  │  CLI (`devtools`)│  │ Shell integration  │   │
│  │ React + TS + Tailwind│  │  clap, JSON out  │  │ `devtools env`     │   │
│  └──────────┬───────────┘  └────────┬─────────┘  └─────────┬──────────┘   │
└─────────────┼───────────────────────┼──────────────────────┼──────────────┘
              │ IPC (typed)           │ named pipe / unix socket
              ▼                       ▼                      ▼
┌───────────────────────────────────────────────────────────────────────────┐
│                    CORE  (`devtoolsd` / linked lib)                       │
│                                                                           │
│  ┌────────────┐  ┌──────────────┐  ┌────────────┐  ┌──────────────────┐   │
│  │  Scanner   │  │  Inventory   │  │ Resolution │  │  Action Engine   │   │
│  │  (§6)      │  │  (§8)        │  │  Engine    │  │  (§10)           │   │
│  │            │  │              │  │  (§9)      │  │                  │   │
│  │ walker     │  │ providers    │  │ precedence │  │ plan → dry-run   │   │
│  │ heuristics │  │ probing      │  │ matching   │  │ → apply → audit  │   │
│  │ watcher    │  │ dedupe       │  │ statuses   │  │ → rollback       │   │
│  └─────┬──────┘  └──────┬───────┘  └─────┬──────┘  └────────┬─────────┘   │
│        │                │                │                  │             │
│        └────────────────┴────────────────┴──────────────────┘             │
│                                   │                                       │
│                        ┌──────────▼──────────┐                            │
│                        │  Store (SQLite WAL) │  ← index, cache, audit     │
│                        └─────────────────────┘                            │
└───────────────────────────────────────────────────────────────────────────┘
                                   │
                     ┌─────────────▼─────────────┐
                     │  OS primitives             │
                     │  filesystem, MFT/USN, FSEvents, inotify, │
                     │  process spawn, reparse points │
                     └────────────────────────────┘
```

### 5.2 Why this shape

**Daemon + thin clients.** Scanning a drive takes tens of seconds and should not be tied to a
window being open. The index must survive app restarts so the UI opens instantly. A long-lived
`devtoolsd` (started on demand, exits after idle timeout) also makes the filesystem watcher
useful for shell integration.

**Rust for the core.** The scanner is `stat`-bound and needs real threads, no GC pauses, and
precise control over which syscalls run (reparse points, recall flags, MFT ioctls). Rust also
gives a single static binary per platform with no runtime dependency, which matters for a
"install this on your new laptop" tool.

**SQLite for the store.** The queries are relational (join projects × requirements × toolchains),
the dataset is small (10k projects ≈ 10 MB), and SQLite gives us transactions, FTS5 for search,
and portability with zero operational cost. WAL mode so the UI can read while the scanner writes.

**Tauri for the GUI.** Reuses the Rust core in-process (no IPC serialisation of the whole index,
no second implementation), ships ~10 MB instead of ~150 MB, and renders with the OS webview.

### 5.3 Alternatives considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Electron + Node** | Fastest to prototype; huge ecosystem; `node` detection is natural | 150 MB+ download; scanning in JS/`fs` is ~3–5× slower and allocates heavily; two runtimes; `stat` storms are exactly where Node is weakest | Rejected |
| **.NET 8 + Avalonia** | Excellent Windows filesystem APIs; good perf; single file publish | Weakest macOS/Linux story for *this* audience; smaller UI component ecosystem | Rejected (strong runner-up) |
| **Go + Wails** | Simple concurrency model, easy cross-compile, small binaries | Weaker type-level modelling for the constraint system; cgo friction for MFT ioctls; slower `stat` loops than Rust in practice | Rejected |
| **Rust core + Tauri** ✅ | One language for core+backend; Tauri reuses it; strong perf; excellent crates (`jwalk`, `notify`, `ignore`, `windows`) | Rust learning curve; Tauri's webview variance across platforms | **Chosen** |
| **Pure CLI, no GUI** | Cheapest | The product's core value ("what's on my disk") is inherently visual; treemaps and matrices need pixels | Rejected as sole surface — CLI remains first-class |

### 5.4 Process model

```
devtools <cmd>                 → connects to daemon if running, else runs in-process (fast paths)
devtools scan                  → always daemon (long-running)
devtools shell/env             → in-process (must be <150 ms; it runs on every `cd`)
devtools gui                   → spawns daemon if needed, opens Tauri window
```

Idle daemon exit after 30 min, or immediately when no watchers are configured and no window is
open. On Windows the daemon is a user-level process (no service, no UAC) reached via
`\\.\pipe\devtools-<sid>`; on Unix a `0700` socket under `$XDG_RUNTIME_DIR`.

### 5.5 Crate/package layout

```
crates/
  devtools-core/        # domain types, constraint algebra, resolution engine (pure, no I/O)
  devtools-scan/        # walker, heuristics, pruning, watcher, platform fast paths
  devtools-providers/   # provider trait + built-in implementations
  devtools-store/       # SQLite schema, migrations, queries, FTS
  devtools-actions/     # planner, executor, audit log, trash integration
  devtools-ipc/         # wire types shared by daemon/CLI/GUI
apps/
  devtoolsd/            # daemon binary
  devtools-cli/         # `devtools` binary
  devtools-gui/         # Tauri app (Rust shell + React frontend)
plugins/                # TOML provider manifests (user-extensible)
```

`devtools-core` has **no** filesystem or process dependencies. That is a deliberate boundary: the
entire constraint algebra and resolution engine is testable without touching a disk, which is where
most of the subtle bugs will live.

---

## 6. Scanning subsystem

This is the deepest part of the system. Everything else is bookkeeping on top of it.

### 6.1 The shape of the problem

A whole-drive scan is not "walk every file". On a developer workstation with 2M files, a full
recursive enumeration takes minutes, generates megawatts of antivirus work, and hydrates OneDrive
placeholders. But we do not need every file. We need:

- the set of **directories** that look like project roots, and
- a small number of **specific files** inside those directories.

Everything else is noise. So the scan is a *directed search*, not an enumeration.

### 6.2 Three-phase scan

```
Phase 0  ROOT PREP        resolve roots, load exclusions, open store, snapshot config
Phase 1  DISCOVERY WALK   BFS over directories; at each dir, look for markers (name match only)
Phase 2  EXTRACTION       for each discovered project, parse only its marker/manifest files
Phase 3  RECONCILE        dedupe, merge workspaces, compute identity, diff against previous run
```

**Phase 1 detail.** Breadth-first or work-stealing DFS over a directory queue, N worker threads
(`min(8, cores)` by default). For each directory:

1. `readdir` once, collecting `(name, file_type)`.
2. Classify entries by name only — no stat per child, no `read()`.
3. Record **marker hits** into a per-directory structure.
4. Enqueue child directories that pass the pruning predicate (§6.3).
5. Only if ≥1 strong marker was found, record the directory as a project candidate.

The invariant that makes it fast: **one `readdir` per directory, one `stat` per marker hit, zero
file reads during phase 1.** File *contents* are only read in phase 2, and only for the ~50 files
per project that matter.

**Phase 2 detail.** Per project, a fixed manifest list is read with a **size cap** (default 2 MB;
`pom.xml` and `package-lock.json` can be enormous — a 40 MB `package-lock.json` is read only for
its first 64 KB header, see §9.5). Parsers are streaming and total-ordered: a malformed file
yields `Unknown(raw_excerpt)` with a diagnostic, never an error that aborts the project.

**Phase 3 detail.** Nested projects, workspace members, monorepo packages, duplicate clones, and
worktrees are resolved here — see §6.6.

### 6.3 Pruning rules

The scan never enters a directory matching a *deny* rule. Deny rules come in four classes:

**A. Structural (always, not user-configurable)**
```
node_modules/            target/                 .git/          .svn/  .hg/
.gradle/                 build/                  obj/  bin/      (dotnet)
.venv/  venv/  __pycache__/  .tox/  .mypy_cache/  .pytest_cache/
Pods/  .build/           vendor/                 (only if > N children heuristics fail — see below)
.nuget/  packages/       .terraform/             jmeter.log  …
dist/  out/  coverage/   .next/  .nuxt/  .svelte-kit/  .cache/
.cargo/registry/  .m2/repository/  .npm/_cacache/  .pnpm-store/
```
Note `vendor/` and `bin/` are ecosystem-overloaded (`bin/` is a *build output* for .NET/C++ but a
*source* dir for shell scripts). Rule: prune `bin/` and `obj/` **only when a sibling `*.csproj`,
`*.sln`, or `CMakeCache.txt` exists or when the directory contains no marker files and >200 entries.**

**B. System and OS**
```
C:\Windows  C:\Program Files  C:\Program Files (x86)  C:\ProgramData
$Recycle.Bin  System Volume Information  C:\$WinREAgent
*/AppData/Local/Temp  */AppData/Local/Packages  */AppData/Local/Microsoft/Windows
/System  /private/var  /usr  /proc  /sys  /dev  /snap  /var/lib/docker
```
Version-manager and package-cache roots are *excluded from the project walk* but *included in the
inventory scan* (§8) — different purpose, different rules.

**C. High-cost, low-yield**
- Directories with >50k immediate children (photo dumps, mail stores) → skip and record a
  `skipped_large` diagnostic.
- Reparse points / junctions / symlinks → **never followed** by default (`--follow-symlinks` opt-in,
  with a visited-(volume,file-id) set to break cycles).
- Cloud placeholder directories (OneDrive/Dropbox/iCloud roots) → skipped by default with a clear
  notice; opt-in scanning uses `FILE_FLAG_OPEN_NO_RECALL` so nothing downloads.
- Network drives and removable media → excluded by default (see §6.8 on why this is a safety
  issue, not just a speed one).

**D. User**
- Per-root glob exclusions, `.devtoolsignore` file (gitignore syntax) at root or project level.
- "Ignore this project" per-project (persisted, survives rescans; identity-based, §6.6).

**Pruning is observable.** The scan report always states: directories visited, pruned by rule
class (with counts), skipped for permission errors, and bytes of marker files read. Users must be
able to see *what was not looked at*, otherwise "it didn't find my project" is unfalsifiable.

### 6.4 Depth policy

Default `max_depth = 12`. Rationale: real projects sit at `~/code/<org>/<repo>` (depth 2–4);
12 tolerates `D:\clients\2021\acme\web\packages\ui` while bounding pathological trees.

Depth is not a hard cliff: when a directory at `max_depth` contains a **weak** marker (`.git`,
`.editorconfig`, `README.md`, `LICENSE`) but no strong one, the walk gets a bounded extension of
+4 levels in that subtree (`max_extensions = 1`). This catches deep monorepos without paying for
deep trees everywhere.

### 6.5 Windows fast path (USN / MFT)

The walk above is the portable baseline. On NTFS volumes, when the user grants
"fast scan (needs elevation)", an optional path exists:

1. Open the volume (`\\.\C:`) with `GENERIC_READ`.
2. `FSCTL_ENUM_USN_DATA` to enumerate the MFT: every file/dir with name, parent ref, attributes,
   timestamps — a single sequential pass over metadata, typically **10–30× faster** than `readdir`
   recursion and immune to directory-batch latency.
3. Reconstruct the directory tree in memory from `(parent_ref, name)` pairs, then run the *same*
   marker and pruning logic against it — the heuristics are shared, only the enumeration differs.

Constraints and honesty about them:
- Requires Administrator (or `SeManageVolumePrivilege`). Never required — it is an accelerator.
- Name-only data: file *contents* still need normal reads in phase 2 (small, unavoidable).
- MFT can be stale relative to the last write; resolve with `FSCTL_QUERY_USN_JOURNAL` and read
  deltas from the journal since the last scan — which is also the **incremental** mechanism.
- Hard links and reparse points appear as ordinary entries; de-duplicate by `(volume, file_id)`.
- On non-NTFS (ReFS, exFAT, network) fall back to the walk, silently.

macOS incremental uses `FSEvents` (per-volume journal, cheap, coarse-grained); Linux uses
`inotify` where watch counts allow, else periodic rescan. `notify` crate covers all three for the
watcher case; the MFT path is `windows`-crate-specific.

### 6.6 Project identity, nesting, and changes over time

**Nesting rules.** A directory is a *project root* if it has ≥1 strong marker AND is not the
child of a more-authoritative root, with these exceptions:

- If a parent project declares workspace membership (see below), children that carry their own
  `package.json`/`pom.xml` are **members**, not independent projects, but are still individually
  addressable in the UI.
- If a child has its own **lockfile or build descriptor** (`package-lock.json`, `pnpm-lock.yaml`,
  `gradlew`, `mvnw`), it is treated as a project even inside another project — this is how you
  represent `legacy/` checkouts vendored inside a repo.

Workspace detection (declared, not guessed):
| Ecosystem | Workspace root signal |
|---|---|
| npm/yarn/pnpm | `workspaces` in `package.json`, `pnpm-workspace.yaml` |
| Maven | `<modules>` in `pom.xml` |
| Gradle | `settings.gradle(.kts)` `include(...)` |
| .NET | `.sln` project references |
| Cargo | `[workspace] members` |
| Go | `go.work` |
| Python | `[tool.uv.workspace] members` |

**Identity survives movement.** A project is keyed by a `ProjectKey` = the first available of:
1. `git remote origin` normalised URL + relative subpath (stable across clones and moves),
2. manifest `name`+`group`/`groupId:artifactId` + relative subpath,
3. content hash of marker file names + normalised absolute path (fallback).

This matters because: user overrides (ignore, tags, notes, merged/split decisions) must survive a
folder rename; and the diff view ("2 new projects, 1 disappeared") must not cry wolf on a move.

**Change reconciliation.** Each scan produces a diff against the stored snapshot:
`added`, `removed`, `moved` (identity match, path changed), `changed` (markers or requirements
changed), `unchanged`. The UI shows this as a notification ("3 new projects found, 1 moved").

### 6.7 Incremental strategy

Three layers, cheapest first:

1. **Marker mtime cache.** `markers(path, size, mtime_ns, ino/file_id) → parsed_requirements`.
   If a marker's `(size, mtime, file_id)` is unchanged, reuse the parse without opening it.
   Phase 2 becomes ~free on rescans. This is the single biggest win and requires no OS support.
2. **Directory watch.** `notify` on the *project roots only* (not whole roots, where possible):
   create/rename/delete events on marker filenames trigger a targeted re-parse (debounced 500 ms).
3. **Journal replay** (Windows USN only): apply journal deltas since `last_usn` to the in-memory
   tree, avoiding any walk at all for a "did anything change" check.

A full walk still runs on demand (`devtools scan --full`) and weekly in the background (configurable),
because watchers miss events across sleep, network hiccups, and daemon downtime.

### 6.8 Failure handling

Scanning a whole disk *will* hit errors. None of them abort the scan.

| Condition | Behaviour |
|---|---|
| `EACCES` / `EPERM` on readdir | Record in `scan_diagnostics`, count it, continue. Report at end. |
| Path >260 chars on Windows | Use `\\?\`-prefixed APIs / `std::fs` with long-path enabled in the manifest; never truncate silently. |
| Directory deleted mid-walk | Ignore (`ENOENT` is normal); do not retry. |
| Reparse-point loop | Visited set by `(volume_serial, file_id)`; skip on revisit. |
| Drive dismounted / USB removed mid-scan | Abort that root only; mark root `unavailable`; keep partial results flagged `partial`. |
| Unreadable file (locked by a build) | Record diagnostic; retry once at end of phase 2. |
| Malformed JSON/XML/TOML manifest | Keep project, mark requirement `Unknown` with parser error excerpt. |
| Antivirus blocking | Detect repeated `EACCES` clusters in known AV paths; surface a hint to exclude `devtools` from real-time scanning. |
| OneDrive placeholder encountered | Skip file (attribute check), never open, never recall. |

**Safety note on reads.** Opening a OneDrive/Dropbox placeholder file triggers a network download
and can block for seconds or hydrate gigabytes. Every open must check attributes first:
`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (0x400000)`, `FILE_ATTRIBUTE_RECALL_ON_OPEN (0x40000)`,
`FILE_ATTRIBUTE_OFFLINE (0x1000)`, and combined with `IO_REPARSE_TAG_CLOUD*`. On macOS the
equivalent is the `com.apple.fileprovider` / dataless-file flags. This is a correctness
requirement, not an optimisation: a naive implementation can cost a user real money on a metered
connection.

### 6.9 Scan algorithm (pseudocode)

```text
fn scan(roots, config) -> ScanReport:
    tree      = Store::previous_snapshot()
    visited   = HashSet<(VolId, FileId)>
    queue     = WorkQueue::new(roots, depth=0)
    projects  = []

    parallel workers:
        dir = queue.pop()
        if dir.depth > config.max_depth and not dir.extended: continue
        if config.prune(dir): report.prune(dir); continue
        entries = readdir(dir)                      // one syscall-ish
        if error: report.diagnostic(dir, err); continue

        markers = entries.filter(is_marker_name)
        if markers.is_empty():
            queue.push(children(dir) where is_dir and not reparse_point)
            continue

        candidate = Candidate{ dir, markers }
        if candidate.has_strong_marker():
            projects.push(candidate)
            // do not descend into build outputs / vendored dirs, but DO descend
            // into plausible sub-projects (see nesting rules)
        queue.push(relevant_children(dir))

    for project in projects:                        // phase 2, bounded parallelism
        project.requirements = extract(project, cache=tree.marker_cache)

    reconciled = reconcile(projects, tree)          // identity, nesting, workspace merge
    Store::write_atomic(reconciled)
    return report
```

### 6.10 Scan report (user-visible)

```
Scanned C:\ — 1,284,301 dirs, 42.7 GB marker data skipped
  visited        1,284,301 dirs
  pruned           981,120 (node_modules 402k, .git 118k, build outputs 210k, other 251k)
  skipped              1,204 (permission denied 1,198, large dir 6)
  18 minutes → 38 s  (USN fast path)

Found 412 projects (398 full, 14 partial)
  node 240 · java 88 · dotnet 31 · python 27 · go 12 · rust 9 · mixed 5
  12 new since last scan, 3 moved, 2 disappeared
```

---

## 7. Detection catalog

### 7.1 Strong markers (project + ecosystem)

| Marker | Ecosystem | Notes |
|---|---|---|
| `package.json` | node | Weakened if no `node_modules`, no lockfile, no scripts — still a project (library) |
| `pnpm-lock.yaml` / `yarn.lock` / `package-lock.json` / `bun.lockb` | node | Also establishes package manager |
| `.nvmrc`, `.node-version` | node | Version pin |
| `pom.xml`, `mvnw`, `.mvn/` | java | |
| `build.gradle(.kts)`, `settings.gradle(.kts)`, `gradlew` | java (gradle) | |
| `*.csproj`, `*.fsproj`, `*.vbproj`, `*.sln`, `Directory.Build.props` | dotnet | |
| `global.json` | dotnet | SDK pin |
| `pyproject.toml`, `requirements.txt`, `Pipfile`, `setup.py`, `poetry.lock`, `uv.lock` | python | |
| `go.mod`, `go.work` | go | |
| `Cargo.toml`, `rust-toolchain.toml` | rust | |
| `Gemfile`, `.ruby-version`, `*.gemspec` | ruby | |
| `composer.json` | php | |
| `mix.exs` | elixir | |
| `pubspec.yaml` | flutter/dart | |
| `CMakeLists.txt`, `Makefile` (+ `*.c/*.cpp`) | cpp | `Makefile` alone is weak |
| `flake.nix`, `shell.nix`, `devenv.nix` | nix | informational |
| `.git` | (any) | Project-ness signal, not ecosystem |

### 7.2 Weak markers (supporting evidence only — never sufficient alone)

`.editorconfig`, `README.md`, `LICENSE`, `.gitignore`, `.github/`, `.vscode/`, `.idea/`,
`.devcontainer/`, `docker-compose.yml`, `Dockerfile`, `Justfile`, `Taskfile.yml`, `LICENSE.md`,
`CONTRIBUTING.md`, `.pre-commit-config.yaml`.

Weak markers serve two purposes: they lift a directory's confidence score (used to suppress
fixtures, §7.4) and they unlock the bounded depth extension (§6.4).

### 7.3 Container/VM requirements (informational in v1)

These describe a requirement the tool cannot install locally; they are surfaced as
**`NotManaged`** with an explanation, and included in exports:

| Marker | Extracted |
|---|---|
| `Dockerfile` / `Containerfile` | `FROM <image>:<tag>` lines → base runtime requirement |
| `docker-compose.yml` | service images, ports |
| `.devcontainer/devcontainer.json` | `image`, `features`, `containerEnv` |
| `mise.toml` / `.mise.toml` / `.tool-versions` | authority-backed tool pins (see §9.3) |
| `.sdkmanrc` | `java=`, `gradle=`, `maven=` |
| `rust-toolchain.toml` | `channel`, `components`, `targets` |
| `.java-version` | exact JDK |
| `volta` block in `package.json` | `node`, `npm`, `yarn` |
| `.nvmrc` with `lts/*` | LTS-family requirement (§9.4) |

### 7.4 False-positive suppression

Fixtures, examples, and vendored code produce mountains of noise. Scoring, not simple presence:

```
confidence = 1.0
  + 0.15 if has_lockfile
  + 0.10 if has_git
  + 0.10 if has_ci_config
  - 0.45 if path matches  **/test(s)?/fixtures/**, **/examples?/**, **/samples?/**
  - 0.45 if path matches  **/node_modules/**, **/.git/**, or any pruned class (defence in depth)
  - 0.30 if dir name in {fixtures, testdata, mocks, __fixtures__, demo, playground}
  - 0.25 if the containing dir is named `fixtures`/`examples`
  - 0.20 if the manifest has no name/build/scripts/dependencies keys at all
  - 0.15 if located inside a `.cargo/`, `.m2/`, `.gradle/`, `.nuget/` cache subtree
accept if confidence >= 0.5, else store as `suppressed` (visible under "show hidden" with the
reason, never silently dropped)
```

Every suppression reason is retained and inspectable. "Show me what you decided not to call a
project, and why" is a first-class question.

---

## 8. Toolchain inventory

Discovery of *installed* tools is a different problem from project scanning: the search space is
small and known, but the variance between managers is large.

### 8.1 Provider model

A **Provider** owns one Tool. It answers five questions:

```rust
trait Provider {
    fn tool(&self) -> Tool;                                  // Node, Jdk, Maven, …
    fn detect_managers(&self, ctx) -> Vec<Detection>;        // which managers are present
    fn list_installed(&self, ctx) -> Vec<Toolchain>;         // versions, paths, source, size
    fn install(&self, plan) -> Result<ActionOutcome>;        // delegate to manager or download
    fn pin(&self, project, version) -> Result<PinEdit>;      // write the right pin file
    fn activate(&self, version) -> Result<EnvPatch>;         // env vars to select it
}
```

`EnvPatch` is the heart of shell integration: a provider returns
`{ PATH_PREPEND: [..], JAVA_HOME: .. }` rather than executing a shell command, so
`devtools env` composes multiple providers into one environment deterministically without
sourcing vendor scripts (which are slow, interactive, and shell-specific).

### 8.2 Built-in provider targets

| Tool | Install sources we detect | Version extraction |
|---|---|---|
| **node** | nvm-windows (`%APPDATA%\nvm`), nvm-sh (`~/.nvm/versions/node`), fnm (`~/.local/share/fnm` / `%APPDATA%\fnm`), volta (`~/.volta/tools/image/node`), asdf, mise, system (Program Files/`/usr/local`), Chocolatey, winget, Homebrew, bun-managed | dir name + `node --version` verify |
| **pnpm / yarn / bun / npm** | corepack shims, `pnpm store path`, per-project `.yarn/releases`, global installs | `--version`, `packageManager` field |
| **jdk** | SDKMAN (`~/.sdkman/candidates/java`), asdf/mise, `~/.jdks` (IntelliJ/GraalVM), `JAVA_HOME`, `C:\Program Files\Java`, `C:\Program Files\Eclipse Adoptium`, Homebrew, `/usr/lib/jvm` | dir name + `java -version` (stderr!) |
| **maven** | wrapper distributions (`~/.m2/wrapper/dists`), system, SDKMAN, homebrew | `mvn -v` (first line), wrapper dir names |
| **gradle** | wrapper distributions (`~/.gradle/wrapper/dists`), system, SDKMAN, homebrew | `gradle --version`, dir names |
| **dotnet-sdk** | `dotnet --list-sdks`, `%ProgramFiles%\dotnet\sdk`, `~/.dotnet` | command output (authoritative) |
| **python** | pyenv, asdf/mise, uv-managed (`~/.local/share/uv/python`), conda envs (flagged), system, Windows Store alias (flagged as stub!) | dir name + `--version` |
| **go** | `go env GOROOT`, `~/sdk/go*`, system | `go version` |
| **rustc** | rustup (`~/.rustup/toolchains`), system | dir name + `rustc --version` |
| **ruby** | rbenv, rvm, asdf, system | dir name + `ruby -v` |
| **php** | system, brew, Herd, XAMPP | `php -v` |

### 8.3 Probing rules (learned the hard way)

1. **Never trust a directory name.** `graalvm-ce-25.0.2` is a JDK whose *reported* version is
   `25.0.2`; `jdk-21.0.1+12` is also a JDK. Always confirm by executing the binary once per
   (path, mtime) and cache it.
2. **Some tools report to stderr** (`java -version`, `mvn -v` partially). Capture both streams.
3. **Windows Store Python is a stub** (`WindowsApps\python.exe`) that opens the Microsoft Store.
   Detect the path and mark it `stub — not a real interpreter`.
4. **Never run a probing command without a timeout** (default 5 s) and stdin closed. A broken
   `java` install can hang on a missing DLL dialog.
5. **Never probe with a working directory inside a project** — some tools pick up local config
   (`mvn` reads `.mvn/`, `dotnet` reads `global.json`). Probe from a neutral temp dir.
6. **Version managers on PATH are proxies, not installs.** `C:\Program Files\nodejs\node.exe` may
   be an nvm symlink whose target changes. Record the *target* and mark proxies as such.
7. **Deduplicate by `(canonical_path, tool, version)`**; a single JDK reachable via
   `JAVA_HOME`, SDKMAN's `current` symlink, and PATH is one toolchain with three pointers.

### 8.4 Toolchain record

```rust
struct Toolchain {
    id: ToolchainId,
    tool: Tool,
    version: Version,              // normalised (§9.2)
    raw_version: String,           // exactly as reported
    path: PathBuf,
    canonical_path: PathBuf,
    manager: Option<ManagerKind>,  // Nvm, Fnm, Volta, Sdkman, Asdf, Mise, Rustup, Pyenv, System, …
    is_global_default: bool,
    is_proxy: bool,
    needs_elevation: bool,
    disk_bytes: u64,
    verified_at: DateTime,
    health: Health,                // Ok | Broken(reason) | MissingBinary | Stub
}
```

`health` matters: an inventory that lists a JDK which cannot run is worse than one that does not
list it, because it produces confident wrong advice.

---

## 9. Requirement extraction and resolution

### 9.1 The constraint algebra

One normalised type, ecosystem-agnostic, with ecosystem-specific parsers in front of it:

```rust
enum Constraint {
    Exact(Version),
    Range { min: Option<Version>, max: Option<Version>,
            min_inclusive: bool, max_inclusive: bool },
    Any,                 // declared but unconstrained
    Lts,                 // "latest LTS of family"
    Latest,              // "whatever is newest"
    Toolchain(String),   // named distribution, e.g. sdkman "21.0.2-tem"
    Unknown { raw: String, reason: String },
}

enum Provenance {
    File { path: PathBuf, line: u32, key: Option<String> },
    Command { description: String },   // "declared by wrapper"
    Inferred { rule: String },          // "no pin found, defaulted from engines"
    User { edited_at: DateTime },
    GlobalDefault,
}
```

Every Requirement carries `Provenance` — this is non-negotiable for goal G2. The UI renders
"Java 17 — from `pom.xml`, property `maven.compiler.release`" and the CLI prints the same on
`--explain`.

Requirements also carry **strength**:

| Strength | Meaning | Affects |
|---|---|---|
| `Pinned` | Version manager pin file (`.nvmrc`, `global.json`, `.sdkmanrc`) | Must be satisfied exactly; mismatch = error |
| `Declared` | Manifest/build file states a version or range | Mismatch = warning |
| `Advisory` | `engines`, `FROM node:20`, devcontainer | Mismatch = informational |
| `Inferred` | Heuristic (e.g. `LastWriteTime` proximity, README prose) | Off by default, opt-in |

### 9.2 Version normalisation

Per-ecosystem quirks that *must* be handled or the tool produces embarrassing advice:

| Ecosystem | Input | Normalised |
|---|---|---|
| Java | `1.8`, `JavaVersion.VERSION_1_8`, `sourceCompatibility = "8"`, `release 8` | `8` (major-only compare) |
| Java | `21`, `temurin-21.0.2+13`, `graalvm-ce-25.0.2` | `21.0.2`, `25.0.2`, vendor kept separate |
| Node | `v20.11.0`, `20`, `lts/iron`, `lts/*`, `node` | `20.11.0` / `Lts(iron)` / `Lts(any)` / `Latest` |
| Node | `engines.node: ">=18 <21"` | `Range{18..21}` |
| Maven | `3.9.6` vs `3.9.12` | semver-like, but Maven's `3.9.10 > 3.9.9`; **not** string compare |
| Gradle | `gradle-8.5-bin` distribution name | `8.5` + `distribution_type=bin` |
| .NET | `8.0.100` + `rollForward: latestFeature` | `Exact(8.0.100)` + policy flag |
| Python | `3.12`, `^3.11`, `>=3.9,<4`, `~=3.10` | PEP 440 range |
| Go | `go 1.22.1`, `toolchain go1.22.3` | `1.22.1`, plus `min_toolchain` |
| Rust | `1.76.0`, `stable`, `nightly-2024-01-01`, `1.76` | channel/version + date |
| Ruby | `3.2.2`, `ruby-3.2.2` | `3.2.2` |

Semantic-version comparison is delegated to per-ecosystem comparators in `devtools-core`
(`semver`, `pep440`, `maven_version`, `dotnet_rollforward`), each with a property-test suite
(§14.2). Getting `1.8 → 8` and `3.9.10 > 3.9.9` wrong would poison every downstream conclusion.

### 9.3 Precedence

When a project declares the same tool in several places, precedence is explicit and displayed:

```
1. User override (persisted in store, per project)          ← highest
2. Explicit version-manager pin file       .nvmrc / .tool-versions / .sdkmanrc /
                                           global.json / rust-toolchain.toml / .python-version
3. Build descriptor                        pom.xml / build.gradle / package.json engines /
                                           pyproject requires-python / go.mod / *.csproj
4. Workspace root inheritance              parent's pin applies to members unless overridden
5. Global default                          the machine's default toolchain
6. None                                    → status Unknown, "no requirement declared"
```

Conflicts are *shown*, never silently resolved: "`.nvmrc` says 18, `engines` says `>=20`" is a
`Conflict` diagnostic with both provenances. That specific case is a real bug in the repo, and
the tool's job is to surface it.

### 9.4 Resolution algorithm

```
for each (project, tool) group:
    requirements = requirements sorted by precedence, user override first
    effective    = first requirement with strength >= Declared, else GlobalDefault
    if none: status = Unknown; continue

    installed = inventory.toolchains(tool)
    match = match(effective.constraint, installed)

    status =
      | Satisfied(tc)              if a satisfying toolchain is installed
      | Missing                     if constraint is Exact/Toolchain and nothing matches
      | UnsatisfiedRange{nearest}   if constraint is Range and nothing satisfies
      | NotManaged                  if ecosystem requires a container/remote (§7.3)
      | Unknown                     if constraint couldn't be parsed
      | Ignored                     if user muted this requirement

    capability = manager_capability(tool)   // can it hold N versions? multi-version? auto-switch?
    if manager_capability == Single and project.conflicts_with(another_project):
        status = plus_warning(Conflict{ other: project })
```

`manager_capability` drives honest advice: with only a system-wide Node install, "install Node
16 too" is impossible without first installing a version manager. The tool must say
**"you need a manager before you need a version"** rather than offering a broken action.

For `Lts` and `Latest` constraints, resolution is **offline**: it uses a bundled, versioned
catalog (`tool-catalog.json`, shipped in the binary, updated ~weekly via an opt-in refresh) of
released versions per tool. No network call is required to answer "what is the current Node LTS";
network is only used when the user explicitly installs.

### 9.5 Extraction limits and honesty

- Files > 2 MB: parse bounded region (first/last 64 KB) and mark the requirement
  `partial: true`. A 40 MB `package-lock.json` is not a requirements source anyway.
- Remote parent POMs are **never** fetched. Maven inheritance that requires the network resolves
  to `Unknown{reason: "parent POM not present in local repository"}`. This is a deliberate
  no-network-while-scanning rule.
- Makefiles and Dockerfiles are parsed with *conservative* regexes and marked `Advisory`; we do
  not execute anything or interpret variable expansion beyond trivial cases.
- Every `Unknown` carries a one-line human reason. A blank version cell is a bug.

---

## 10. Management actions and safety

### 10.1 The action model

Every mutation is an **Action Plan**: a list of steps, each with `description`, `targets`,
`expected_effect`, and `reversible: bool`. Plans are produced by the core, rendered as a dry run,
confirmed, then executed with an audit trail.

```rust
struct ActionPlan {
    id: PlanId,
    title: String,                  // "Install 3 Node versions, pin 2 projects"
    steps: Vec<Step>,
    estimated_download_bytes: u64,
    requires_network: bool,
    requires_elevation: bool,
    risk: Risk,                     // Read | Write | Destructive | External
}

enum Step {
    InstallToolchain { tool, version, method: InstallMethod },
    WritePinFile { path, content, backup: bool },
    DeletePath { path, kind: Trash | Purge, expected_bytes },
    RunProviderCommand { provider, argv, timeout },
    RewriteLockfile { path, format },
}
```

### 10.2 Action catalogue (v1.1)

| Action | Step kind | Safety |
|---|---|---|
| **Install missing version** | delegate to the owning manager (`nvm install 18.20.4`, `sdk install java 21.0.2-tem`, `mise install`, `rustup toolchain install`, `dotnet-install`) — or direct download with SHA-256/SHA-512 verification + signature check where published | Network; never piped to a shell |
| **Pin version to project** | write/update the manager's native pin file (`.nvmrc`, `global.json`, `.sdkmanrc`, `rust-toolchain.toml`, `.python-version`) | Backup written alongside; diff shown |
| **Open shell with env** | `EnvPatch` applied in a child shell; no global mutation | Read-only |
| **Prune unused toolchain** | delegated `uninstall`; refuses if version is global default or referenced by any project | Destructive → Trash where the manager allows |
| **Reclaim build artifacts** | delete `node_modules`, `target`, `obj`, `.gradle`, `dist` across selected projects | Destructive → Trash, with byte total and per-project list |
| **Clean package caches** | `npm cache clean`, `pnpm store prune`, `go clean -modcache` (opt-in, off by default) | Destructive |
| **Export manifest** | write `devtools.lock`, `mise.toml`, `.sdkmanrc`, or a `SETUP.md` | Write, in-project only |
| **Archive project** | move to an archive root and mark ignored | Write + move |

Deliberately absent: deleting source, `git gc`, committing anything, editing dependency versions,
running builds.

### 10.3 Safety rules (enforced in code, not by convention)

1. **Read-only by default.** Mutations require `--allow-writes` (CLI) or an explicit one-time
   toggle (GUI), persisted per session.
2. **No writes outside allowlisted paths.** A mutating step's target must be inside a scanned
   root AND inside a known project, or inside a known toolchain install path. Anything else is
   rejected by the executor, not just hidden by the UI.
3. **Symlink discipline.** Before any delete: `lstat` every path in the subtree; refuse if any
   entry is a reparse point/symlink leading outside the target root. Never follow links during
   deletion (this is how people delete their home directory).
4. **Trash, not `rm`.** Use the platform trash (Recycle Bin / `NSFileManager` trash / FreeDesktop
   trash). Permanent deletion is a separate, explicitly-typed confirmation.
5. **Dry run is the default output of every mutating command.** `devtools prune` prints the plan;
   `devtools prune --apply` executes it.
6. **Append-only audit log** in the `audit` table of `devtools.db` (in `~/.local/share/devtools` or `%APPDATA%\devtools`):
   timestamp, plan id, step, target, before/after hash where applicable, outcome. Never rotated
   by the app; never uploaded.
7. **Backups for text edits.** Pre-image written to `~/.local/share/devtools/backups/<plan>/`.
8. **Interlocks.** Refuse to prune a version that is `is_global_default`, referenced by any
   project, or is the only satisfying toolchain for a project — unless `--force` with an explicit
   list of affected projects.
9. **No shell interpolation.** Provider commands are `argv` arrays; no string concatenation, no
   `sh -c`, ever. Escaping bugs become argument injection.
10. **Download integrity.** HTTPS only, pinned host allowlist per provider, checksum verified
    before extraction, archive extracted with path-traversal protection (reject `..` and absolute
    entries — zip-slip).

### 10.4 Undo

Where the underlying manager supports it, `devtools undo <plan-id>` reverses: re-writes backups,
uninstalls installed versions, restores trashed paths. Explicitly best-effort and labelled so in
the UI; nothing is promised that the manager cannot deliver.

---

## 11. Storage

### 11.1 Schema (SQLite, WAL)

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous  = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;

CREATE TABLE schema_meta (key TEXT PRIMARY KEY, value TEXT);

CREATE TABLE roots (
  id INTEGER PRIMARY KEY,
  path TEXT NOT NULL UNIQUE,
  enabled INTEGER NOT NULL DEFAULT 1,
  kind TEXT NOT NULL,                 -- local | network | removable | cloud
  max_depth INTEGER,
  added_at TEXT NOT NULL,
  last_scanned_at TEXT,
  state TEXT NOT NULL DEFAULT 'idle'  -- idle | scanning | unavailable | error
);

CREATE TABLE scan_runs (
  id INTEGER PRIMARY KEY,
  started_at TEXT NOT NULL,
  finished_at TEXT,
  mode TEXT NOT NULL,                 -- walk | usn | incremental
  dirs_visited INTEGER, dirs_pruned INTEGER,
  errors INTEGER, partial INTEGER NOT NULL DEFAULT 0,
  report_json TEXT                    -- diagnostics summary + prune breakdown
);

CREATE TABLE projects (
  id INTEGER PRIMARY KEY,
  key TEXT NOT NULL UNIQUE,           -- ProjectKey (§6.6), stable across moves
  path TEXT NOT NULL,
  real_path TEXT NOT NULL,            -- symlink-resolved
  name TEXT,                          -- friendly, from manifest or dir
  git_remote TEXT,
  git_branch TEXT,
  last_commit_at TEXT,
  last_activity_at TEXT,              -- max(commit, mtime of markers)
  confidence REAL NOT NULL,
  state TEXT NOT NULL,                -- active | ignored | archived | suppressed
  suppressed_reason TEXT,
  first_seen_at TEXT NOT NULL,
  last_seen_at TEXT NOT NULL,
  last_scan_id INTEGER REFERENCES scan_runs(id)
);
CREATE INDEX idx_projects_path ON projects(path);
CREATE INDEX idx_projects_state ON projects(state, last_activity_at DESC);
CREATE UNIQUE INDEX idx_projects_key ON projects(key);

CREATE TABLE project_ecosystems (
  project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  ecosystem TEXT NOT NULL,
  weight REAL NOT NULL,                -- share of detected markers
  PRIMARY KEY (project_id, ecosystem)
);

CREATE TABLE markers (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  rel_path TEXT NOT NULL,
  kind TEXT NOT NULL,                  -- strong | weak
  ecosystem TEXT,
  size INTEGER, mtime_ns INTEGER, file_id TEXT,
  parser TEXT, parser_version INTEGER,
  UNIQUE (project_id, rel_path)
);

CREATE TABLE toolchains (
  id INTEGER PRIMARY KEY,
  tool TEXT NOT NULL,                  -- node | pnpm | jdk | maven | gradle | dotnet-sdk | …
  version TEXT NOT NULL,               -- normalised
  raw_version TEXT,
  path TEXT NOT NULL,
  canonical_path TEXT NOT NULL,
  manager TEXT,                        -- nvm-windows | fnm | sdkman | asdf | mise | rustup | system …
  is_global_default INTEGER NOT NULL DEFAULT 0,
  is_proxy INTEGER NOT NULL DEFAULT 0,
  health TEXT NOT NULL DEFAULT 'ok',
  health_detail TEXT,
  disk_bytes INTEGER,
  verified_at TEXT,
  UNIQUE (tool, version, canonical_path)
);

CREATE TABLE project_tools (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  tool TEXT NOT NULL,
  constraint_kind TEXT NOT NULL,       -- exact | range | any | lts | latest | toolchain | unknown
  constraint_json TEXT NOT NULL,
  raw TEXT,
  strength TEXT NOT NULL,              -- pinned | declared | advisory | inferred
  provenance_json TEXT NOT NULL,
  provenance_rank INTEGER NOT NULL,    -- precedence (§9.3)
  origin TEXT NOT NULL                 -- extracted | user | inherited | default
);

CREATE TABLE resolutions (
  id INTEGER PRIMARY KEY,
  project_tool_id INTEGER NOT NULL REFERENCES project_tools(id) ON DELETE CASCADE,
  status TEXT NOT NULL,                -- satisfied | missing | unsatisfied_range | not_managed |
                                       -- unknown | ignored | conflict
  toolchain_id INTEGER REFERENCES toolchains(id) ON DELETE SET NULL,
  detail TEXT,
  nearest_toolchain_id INTEGER REFERENCES toolchains(id) ON DELETE SET NULL,
  conflict_with TEXT,                  -- JSON array of project ids
  computed_at TEXT NOT NULL
);

CREATE TABLE project_sizes (
  project_id INTEGER PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
  total_bytes INTEGER, source_bytes INTEGER, artifact_bytes INTEGER,
  measured_at TEXT
);

CREATE TABLE overrides (                -- user intent, survives rescans
  project_key TEXT PRIMARY KEY,
  state TEXT,                           -- ignore | archive | normal
  tags_json TEXT,
  note TEXT,
  pinned_toolchains_json TEXT,          -- manual: "this project uses JDK 17"
  updated_at TEXT NOT NULL
);

CREATE TABLE audit_log (
  id INTEGER PRIMARY KEY, plan_id TEXT, ts TEXT NOT NULL,
  step_kind TEXT, target TEXT, detail_json TEXT, outcome TEXT, reversible INTEGER
);

CREATE VIRTUAL TABLE projects_fts USING fts5(
  name, path, git_remote, content='projects', content_rowid='id'
);
```

Notes:
- `constraint_json` keeps the structured constraint; no attempt to query *inside* it in SQL.
  Filtering by "needs Node 18" happens in the resolution engine over the (small) working set.
- `markers` doubles as the parse cache: `(size, mtime_ns, file_id, parser_version)` matches →
  skip; `parser_version` bump invalidates globally after an upgrade.
- Full index for 10k projects is ~10–20 MB. Rebuild from scratch is acceptable, so there is no
  backup story; the schema has a version and a migration chain, and a corrupt DB triggers a
  rebuild with one button.

### 11.2 Writes

Batched inserts inside one transaction per phase; the UI reads the previous snapshot until the
new one commits (`scan_id` + a single `UPDATE schema_meta SET value=...` at the end), so the app
never shows a half-scanned world.

---

## 12. CLI

The CLI is a first-class product, not a debug surface. All commands support `--json` for
scripting, and `--explain` for provenance.

```
devtools scan [--root PATH]... [--full] [--jobs N] [--follow-symlinks] [--fast]
devtools list [--tool node] [--status missing] [--stale 180d] [--ecosystem java] [--json]
devtools show PATH|NAME [--explain]
devtools doctor [PATH]                 # per-project gap report; exit code = # problems
devtools env [PATH] [--shell pwsh|bash|zsh|fish]
devtools shell [PATH] [--shell …]      # spawn interactive shell with env applied
devtools toolchains [--tool node] [--unused] [--json]
devtools install --plan                 # from `devtools doctor --json` output
devtools pin --tool node --version 20.11.0 [PATH]
devtools prune [--unused-toolchains] [--artifacts] [--dry-run|--apply]
devtools disk [--by project|toolchain|cache] [--json]
devtools export [--format mise|sdkmanrc|devtools.lock|setup.md]
devtools sync [--check]                 # apply devtools.lock to this machine
devtools roots add|remove|list
devtools ignore|unignore PATH
devtools daemon status|stop
devtools gui
```

Example — the flagship output:

```
$ devtools doctor ~/code/api
api  (node, java)
  node    20.11.0   pinned   .nvmrc:1                  ✔ fnm 20.11.0
  pnpm    8.15.4    declared package.json:5            ✘ missing  (nearest: 9.1.0)
  java    17        declared pom.xml:14 (release)      ✔ temurin-17.0.10
  maven   3.9.6     pinned   .mvn/wrapper/…properties  ✔ wrapper (~/.m2/wrapper/dists)

2 problems: 1 missing toolchain, 1 wrong major.
  devtools install --plan   # install pnpm 8.15.4 (2.1 MB)
```

Exit codes: `0` all satisfied, `1` problems found, `2` scan error, `3` usage error. Makes it
usable in a pre-commit hook or a CI "is my dev env sane" check.

**Shell integration** (`devtools hook init pwsh|bash`):

```powershell
# appended once to $PROFILE
function global:cd { Set-Location @args; if (Test-Path .\devtools.lock) {
    devtools env --shell pwsh | Invoke-Expression } }
```
Note the deliberate design choice: `devtools env` must run in **<150 ms**, which is why it is
in-process and reads only the project-local markers plus a cached resolution — not a full scan.

---

## 13. UI

### 13.1 Information architecture

```
┌ Dashboard ───────────┐  the "state of my machine" answer, one screen
├ Projects ────────────┤  table/virtualised list, filter, multi-select, bulk actions
│  └ Project detail ───┤  markers, requirements w/ provenance, resolutions, shell, actions
├ Toolchains ──────────┤  everything installed, who uses it, disk, health
├ Disk ────────────────┤  treemap: projects / toolchains / caches
├ Activity ────────────┤  scan history, action audit, undo
└ Settings ────────────┤  roots, exclusions, providers, catalogs, watchers
```

### 13.2 Dashboard

```
┌────────────────────────────────────────────────────────────────────────────┐
│  devtools                              Last scan 4 min ago · 412 projects  │
├────────────────────────────────────────────────────────────────────────────┤
│                                                                            │
│   ⚠  7 projects need something you don't have        [ Review & install ]  │
│   ⚠  2 projects have conflicting version pins        [ Show ]              │
│                                                                            │
│   ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌────────────────┐    │
│   │ Disk used    │ │ Toolchains   │ │ Reclaimable  │ │ Stale projects │    │
│   │ 184.2 GB     │ │ 71 installed │ │  38.4 GB     │ │ 46 > 1 year    │    │
│   └──────────────┘ └──────────────┘ └──────────────┘ └────────────────┘    │
│                                                                            │
│   Ecosystem mix (projects)                                                 │
│   node    ████████████████████████ 240                                     │
│   java    █████████ 88                                                     │
│   dotnet  ███ 31                                                            │
│   python  ██ 27                                                             │
│   go/rust ██ 21                                                             │
│                                                                            │
│   Missing toolchains                                                       │
│   node 16.20.2   ← api-legacy, shop-old, crm                 [ install ]   │
│   pnpm 8.15.4    ← api, web                                 [ install ]   │
│   jdk 11         ← billing (2 projects)                     [ install ]   │
└────────────────────────────────────────────────────────────────────────────┘
```

Design rules for this screen: every number is clickable and leads to the filtered list that
produced it; no number is shown without a way to act on it; "reclaimable" is always
`dry-run-derived`, never an estimate.

### 13.3 Projects list

Virtualised table, keyboard-first (Vim bindings optional). Columns: name, path (dimmed, middle-
truncated), ecosystems, toolchain status (chips: ✔/✘ per tool), last activity, size, tags.

Filters: tool, status, ecosystem, staleness, size threshold, root, tag, free-text (FTS).
Bulk selection → actions: install missing, pin, open shell, delete artifacts, ignore, export.

### 13.4 Project detail

```
api                                        ~/code/api  ·  git: github.com/acme/api  ✔ clean
───────────────────────────────────────────────────────────────────────────────────────
Toolchains
  node    20.11.0     ┌ pinned ┐   .nvmrc:1                    ✔ fnm 20.11.0
  pnpm     8.15.4     ┌ declared ┐ package.json:5              ✘ missing     [ install 8.15.4 ]
  java    17          ┌ declared ┐ pom.xml:14 (release)        ✔ temurin-17.0.10
  ↑ why 17?  "maven.compiler.release, line 14; no .sdkmanrc or .java-version present"

Environment preview                                     [ Open shell ]
  PATH         + ~/.local/share/fnm/node-versions/v20.11.0/installation/bin
  JAVA_HOME    = ~/.sdkman/candidates/java/17.0.10-tem

Health
  ⚠ two lockfiles (package-lock.json, pnpm-lock.yaml)
  ⚠ .nvmrc says 18 but engines says >=20        ← conflict, line refs on click
  ✔ gradle wrapper checksum present

Disk
  source 12.4 MB · node_modules 412 MB · target 88 MB                   [ clean ]
```

The "↑ why" affordance is the product. It is the difference between a dashboard and a tool you
trust.

### 13.5 Toolchains view

Grouped by tool; each row: version, manager, path, disk, health, "used by N projects" (click →
list), "is global default" toggle. Unused versions sorted to the bottom with a single "prune all
unused (12 versions, 4.1 GB)" action, which produces a dry-run plan first.

### 13.6 Disk view

Squarified treemap: projects → artifacts; a second mode for toolchains and caches. Hover shows
measured bytes and the command that produced them. This is where users discover that
`~/.gradle` is 14 GB and that they have 9 copies of a JDK.

### 13.7 Interaction principles

- **Explain everything.** Any computed value has a tooltip/side-panel giving file + line + rule.
- **Never block.** Scanning is background; the UI shows a progress bar with counts and a cancel.
- **Dry run in the UI too.** Destructive actions open a plan diff, not a `confirm()` dialog.
- **Empty states teach.** A fresh install says: "we scanned `C:\Users\you` and found 3 projects;
  add more roots" with a suggested list derived from `git config --global --list` and `$PATH`.
- **Accessibility:** full keyboard operation, focus rings, respects `prefers-reduced-motion`,
  WCAG AA contrast in both themes, screen-reader labels on status chips (not colour alone —
  every status has an icon *and* a glyph).

---

## 14. Testing

The correctness risk in this product is entirely in parsers and heuristics, so the tests are
shaped accordingly.

### 14.1 Fixture corpus

```
testdata/
  fixtures/
    node/nvmrc-exact/            .nvmrc: 18.20.2
    node/nvmrc-lts-alias/        .nvmrc: lts/iron
    node/engines-range/          package.json engines.node ">=18 <21"
    node/corepack/               packageManager: "pnpm@9.1.0"
    node/workspace-pnpm/         pnpm-workspace.yaml + 3 members
    node/volta/                  package.json volta block
    java/pom-release-1.8/        <maven.compiler.release>1.8</…>  → 8
    java/pom-parent-missing/     parent POM absent → Unknown
    java/gradle-wrapper/         distributionUrl checksum + version
    java/gradle-toolchain/       toolchain { languageVersion = 21 }
    java/sdkmanrc/               .sdkmanrc java=21.0.2-tem
    dotnet/globaljson-rollforward/  version 8.0.100, rollForward latestFeature
    python/pep440/               requires-python ">=3.9,<4"
    python/uv-workspace/         [tool.uv.workspace]
    go/toolchain-directive/      go 1.22.1 + toolchain go1.22.3
    rust/toolchain-toml/         channel 1.76.0 + components
    ruby/rbenv/                  .ruby-version
    nesting/monorepo-nested/     parent project with member subprojects
    nesting/vendored-legacy/     child with own lockfile inside parent
    falsepos/test-fixture/       **/test/fixtures/node-module-like
    falsepos/dotnet-bin-obj/     bin/ and obj/ with stray json
  expected/     ← golden JSON of expected extraction per fixture
```

### 14.2 Test layers

| Layer | What | Tool |
|---|---|---|
| Unit | Constraint algebra: normalise, compare, intersect, match | `#[test]` |
| Property | Comparators: antisymmetry, transitivity, `1.8≡8`, `3.9.10>3.9.9`, PEP 440 pre-releases | `proptest` |
| Golden | Each fixture dir → expected requirements JSON | `insta` snapshots |
| Fuzz | Parsers on malformed XML/JSON/TOML/YAML must never panic | `cargo-fuzz` |
| Filesystem | Directory trees built in temp dirs: nesting, junctions, permissions, long paths | `tempfile` + OS-specific setup |
| Integration | Real repos: clone ~20 small public repos at pinned commits, assert detection | network-gated, nightly |
| Performance | Synthetic tree generator (1M files, 150k dirs) + timing budget assertions | `criterion` |
| UI | Playwright on the Tauri webview: scan → filter → dry-run → apply | Playwright |

### 14.3 The corpus test (the one that matters)

Hand-label 300 real repos (the author's own disk plus a scripted clone set) with ground truth:
is-project, ecosystems, required versions. Then:

- **Recall**: fraction of true projects detected ≥ 0.98.
- **Precision**: fraction of detected that are true ≥ 0.97.
- **Version accuracy**: fraction of extracted requirements matching ground truth ≥ 0.99.

Any regression on this corpus blocks release. This is the only honest measure of the product.

### 14.4 Performance harness

Generator creates a realistic tree (many `node_modules` with 30k files each, a `.git` per repo,
build outputs) and asserts: cold walk ≤ budget, warm rescan ≤ 2 s, RSS ≤ 300 MB, zero `stat`
calls on non-marker files (verified by an instrumented syscall counter in test builds).

That last assertion is the architectural invariant of §6.2 expressed as a test.

---

## 15. Delivery plan

### M0 — Spike (1–2 weeks)

Prove the scan. Rust binary, BFS walker with pruning, 3 ecosystems (node, java/gradle, dotnet),
SQLite store, `devtools list` table output. **Gate:** scan `C:\Develop` in <60 s, correct projects
found, no false positives from `node_modules`.

### M1 — Index & diagnosis (3–4 weeks)

10 ecosystems, full parser set, resolution engine, marker cache, incremental rescan, `doctor`,
`show --explain`, export JSON. **Gate:** corpus test at 250 repos passes recall/precision targets;
`devtools doctor` output is trustworthy enough to act on.

### M2 — GUI & shell integration (3–4 weeks)

Tauri app: dashboard, projects list, detail, toolchains, disk (treemap). `devtools env` + shell
hook. **Gate:** a new user reaches a useful screen in <60 s; `cd` hook adds <150 ms.

### M3 — Actions (3–4 weeks)

Action planner/executor, install/pin/prune via providers, trash integration, audit log, dry-run
diffs, undo. **Gate:** every action has a tested dry run and a rollback path; destructive steps
are trash-backed; no action can escape allowlisted paths (adversarial tests).

### M4 — Scale & polish (ongoing)

Windows USN fast path, watcher-based live updates, plugin providers (TOML), export/sync
(`devtools.lock`), staleness/duplicate-clone health checks, i18n scaffolding.

### Sequencing rationale

Actions (M3) come *after* the GUI and diagnosis because the product's wedge is
**"I now understand my machine"**, not "let me bulk install things". Shipping mutations before
the diagnosis is trusted invites exactly the kind of damage that kills trust in a tool that
deletes files.

---

## 16. Risks

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| R1 | False positives make the list untrustworthy (fixtures, vendored code) | High | Confidence scoring with visible reasons; "show suppressed"; corpus precision gate |
| R2 | Antivirus/EDR flags a tool that reads the whole disk | High | Never scan without user-specified roots; signing (Authenticode/notarisation); documented behaviour; optional USN path uses standard Windows APIs; no persistence of file contents |
| R3 | Reading OneDrive/Dropbox placeholders triggers downloads | High | Attribute check before every open; skip by default; `FILE_FLAG_OPEN_NO_RECALL` |
| R4 | Deletion bug destroys a project | Catastrophic | Trash-only, reparse-point refusal, allowlist enforcement in executor, adversarial test suite, no source deletion ever |
| R5 | Version-resolution wrong (1.8 vs 8, Maven ordering) | Medium | Property tests, golden fixtures, `--explain` makes errors visible |
| R6 | Version-manager fragmentation (new manager every year) | Medium | Provider trait + declarative TOML manifests; built-ins only for popular managers |
| R7 | Network drives / offline paths hang the scan | Medium | Excluded by default; per-root timeouts; cancellable |
| R8 | Long paths & Unicode on Windows | Medium | `\\?\` APIs, `OsStr`/`Path` throughout (never `String` for paths), tests with emoji and 300-char paths |
| R9 | Scope creep into package management | High | Non-goals in §1.4 are contractual |
| R10 | Cross-platform drift (Windows-first, macOS broken) | Medium | CI matrix on all three, fixtures run everywhere, mac/linux smoke on each PR |
| R11 | Users expect it to find WSL/container projects | Medium | Detect `\\wsl$\` paths as roots (slow, opt-in); document container limits clearly |
| R12 | Privacy perception ("it reads my whole disk") | Medium | Local-only, no telemetry, audit log visible, open source, reproducible from source |

---

## 17. Open questions

1. **Scan scope on first run.** Default to user-profile only, or offer "whole drive"?
   *(Leaning: profile + known dev roots, with a one-click "add whole drive" that shows the cost
   estimate first.)*
2. **Daemon or not.** Is a persistent daemon acceptable to the target user? It buys live updates
   and instant CLI. *(Leaning: yes, but with a hard idle timeout and an obvious kill switch.)*
3. **Elevation for the USN path.** Worth the friction? *(Leaning: opt-in "fast mode" with a clear
   explanation; the walk is the default.)*
4. **Write `.devtools.lock` into repos by default, or only on request?** *(Leaning: request only —
   unsolicited files in someone's repo is rude.)*
5. **How aggressively to infer?** "This project has no pin; the README says Node 16" — do we say
   it? *(Leaning: infer, but only as `Advisory` with a visible "inferred" chip.)*
6. **Plugin execution.** TOML-only manifests (declarative) or WASM plugins (powerful, sandboxed,
   heavy)? *(Leaning: TOML first; WASM only if a real need appears.)*
7. **Maven/Gradle "wrapper-first" stance.** When both a wrapper and a system install satisfy a
   requirement, which do we report as *effective*? *(Leaning: wrapper, because that's what the
   project will actually run.)*

---

## 18. Appendix

### A. Full marker catalogue (extraction detail)

| File | Ecosystem | Tool | Extracts |
|---|---|---|---|
| `.nvmrc` | node | node | exact / `lts/*` / `lts/<name>` / major / `node` |
| `.node-version` | node | node | exact |
| `package.json` → `engines.node` | node | node | range |
| `package.json` → `packageManager` | node | pnpm/yarn/npm/bun | `name@version` (corepack) |
| `package.json` → `volta` | node | node/npm/yarn | exact |
| `.yarnrc.yml` → `yarnPath` | node | yarn | release file → version |
| `pnpm-workspace.yaml` | node | pnpm | membership |
| `pom.xml` | java | jdk | `maven.compiler.release` \| `.source` \| `.target` \| `<java.version>`; parent inheritance when local |
| `pom.xml` | java | maven | wrapper presence; `<modules>` |
| `.mvn/wrapper/maven-wrapper.properties` | java | maven | `distributionUrl` → exact |
| `build.gradle(.kts)` | java | jdk | `toolchain.languageVersion`, `sourceCompatibility`, `kotlin.jvmToolchain` |
| `gradle/wrapper/gradle-wrapper.properties` | java | gradle | `distributionUrl` + `distributionSha256Sum` |
| `settings.gradle(.kts)` | java | gradle | `include(...)` membership, `toolchainManagement` |
| `.sdkmanrc` | java | jdk/gradle/maven | `java=`, `gradle=`, `maven=` (candidate strings) |
| `.java-version` | java | jdk | exact / vendor-qualified |
| `global.json` | dotnet | dotnet-sdk | `sdk.version`, `rollForward`, `allowPrerelease` |
| `Directory.Build.props` | dotnet | dotnet-sdk | `TargetFramework` (informational) |
| `*.csproj` | dotnet | dotnet-sdk | `TargetFramework(s)` → minimum SDK |
| `pyproject.toml` | python | python | `requires-python`, `[tool.poetry.dependencies].python` |
| `pyproject.toml` | python | uv | `[tool.uv.workspace]`, `[tool.uv]` |
| `.python-version` | python | python | exact (may be multiple lines) |
| `Pipfile` | python | python | `[requires].python_version` |
| `go.mod` | go | go | `go <ver>`, `toolchain <ver>` |
| `go.work` | go | go | `go <ver>`, membership |
| `rust-toolchain.toml` | rust | rustc | `channel`, `components`, `targets` |
| `.ruby-version` | ruby | ruby | exact / `ruby-<ver>` |
| `Gemfile` | ruby | ruby | `ruby "x"` |
| `composer.json` | php | php | `require.php` |
| `mix.exs` | elixir | elixir | `elixir:` requirement |
| `pubspec.yaml` | dart | flutter | `environment.sdk` |
| `Dockerfile` | (container) | — | `FROM` (advisory) |
| `devcontainer.json` | (container) | — | `image`, `features` (advisory) |
| `mise.toml` / `.tool-versions` | multi | various | authority pins |
| `.devtoolsignore` | — | — | exclusion globs |

### B. Provider manifest (TOML plugin example)

```toml
# plugins/sdkman.toml
schema = 1
id        = "sdkman"
tool      = "jdk"
ecosystems = ["java"]

[detect]
# any of these existing means the manager is present
paths = ["~/.sdkman/bin/sdkman-init.sh"]

[inventory]
strategy = "directory"
root      = "~/.sdkman/candidates/java"
version_from = "dirname"
exclude   = ["current"]
verify    = { argv = ["java", "-version"], version_regex = '(?:openjdk|java) version "([^"]+)"', stream = "stderr" }

[size]
strategy = "walk"
root     = "~/.sdkman/candidates/java"

[install]
argv = ["bash", "-lc", "sdk install {{tool}} {{version}}"]   # NOTE: parsed to argv, not sh -c
timeout_secs = 600
requires_network = true

[pin]
file = ".sdkmanrc"
template = "java={{version}}\n"

[activate]
env = { JAVA_HOME = "{{root}}/{{version}}" }
path_prepend = ["{{root}}/{{version}}/bin"]

[catalog]
source = "sdkman-api"
cache_ttl_hours = 168
```

Manufacturer note in the file itself: providers can *describe* shell lines, but the executor
converts them to `argv` and refuses `sh -c`. Any provider that needs a shell is a bug in the
provider format.

### C. Worked example — resolution for one messy project

```
~/code/acme-api
  .nvmrc                         18.20.2
  package.json  packageManager   pnpm@8.15.4
  package.json  engines.node     ">=20"          ← conflict with .nvmrc
  pom.xml       release          17
  .mvn/wrapper  distributionUrl   apache-maven-3.9.6-bin.zip

Requirements (precedence order):
  node   Exact(18.20.2)     Pinned   .nvmrc:1                 rank 2
  node   Range(>=20)        Declared package.json:engines    rank 3
  pnpm   Exact(8.15.4)      Declared package.json:5          rank 3
  jdk    Exact(17)          Declared pom.xml:14              rank 3
  maven  Exact(3.9.6)       Pinned   wrapper properties      rank 2

Inventory: node 20.11.0(fnm), 22.4.0(fnm), 18.20.2(nvm-windows, not default);
           pnpm 9.1.0; jdk 17.0.10-tem, 21.0.2-tem; no maven (wrapper only)

Resolutions:
  node   Satisfied(18.20.2)  + Conflict{.nvmrc:1 vs package.json:engines}
  pnpm   Missing             nearest 9.1.0 → action: install 8.15.4
  jdk    Satisfied(17.0.10-tem)
  maven  Satisfied(wrapper 3.9.6)   [effective = wrapper, not system]
```

Two outputs matter here: the conflict is *reported*, not resolved; and Maven's "effective" choice
is the wrapper, because that is what the project will actually execute (§17.7).

### D. Vocabulary for the codebase (anti-drift)

Because this product sits between several disciplines, these words mean one thing only:

- **finding** → a project discovered by a scan
- **requirement** → what a project needs (never "dependency")
- **toolchain** → an installed instance
- **resolution** → the join of the two
- **action** → a mutation
- **plan** → the unexecuted list of steps for an action
- **provider** → the plugin for one tool

"Dependency" is reserved for libraries (npm packages, Maven artifacts) which this tool
deliberately does not model. That distinction is worth enforcing in review, because conflating
them is how this tool would drift into being a package manager (see §1.4, R9).

---

## 19. Implementation notes (v1, .NET 10)

What shipped, and where it deliberately departs from the design above.

### 19.1 Stack substitution

| Designed | Shipped | Why it still works |
|---|---|---|
| Rust core + Tauri | C# / .NET 10: libraries, CLI and Avalonia desktop application | Owner's requirement. |
| SQLite (WAL) | JSON snapshot, atomically replaced | 3,000 projects → 1.6 MB, loads in ~0.4 s. Behind `JsonStore`, so swapping in SQLite later is one class. |
| Tauri desktop app | Avalonia 12 desktop app | C# UI with no browser or WebView. |
| `devtoolsd` daemon | In-process CLI and desktop app | No background daemon. |
| USN/MFT fast path | Not implemented | The portable walk already meets the budget; the fast path remains the optimisation for whole-volume scans. |

### 19.2 Correctness details found by running it for real

These came out of scanning a 141k-directory tree and are now covered by tests:

- **.NET `rollForward`** — a `global.json` pin of `10.0.300` is satisfied by `10.0.403` under
  `latestFeature`. Ignoring the policy produces the single most annoying false alarm the product
  could emit.
- **The `go` directive is a floor, not a pin** — `go 1.27.0` is satisfied by Go 1.27.3.
- **Composer alternations** — `^7.3|^8.0` must be a union; intersecting them yields an empty range.
- **Gradle/Maven wrappers are not conflicts** — each project fetches its own version, so differing
  pins are normal.
- **Vendor trees** — PHP and Go `vendor/` must be pruned like `node_modules`.
- **Names are hostile** — `adopt-openj9-16.0.1` is JDK 16, not 9; `1.95.0-x86_64-pc-windows-msvc`
  is Rust 1.95.0, not a pre-release.
- **Inheritance needs provenance** — an inherited parent pin must be marked `inferred`, or it
  pollutes conflict detection with phantom disagreements.

### 19.3 Not implemented from the design

- MFT/USN fast path (§6.5) and journal replay.
- Filesystem watchers; rescans are explicit (`devtools scan`).
- Trash-backed deletion is available on Windows and, when `gio trash` is installed, Linux. macOS cleanup is unavailable.
- Providers as TOML plugins (§8.2, §18B); they are compiled in.
- Duplicate-clone detection, staleness scoring, and `devtools sync` from a committed lockfile.
- WSL indexes can be loaded separately into the Windows desktop when the WSL CLI is installed and has already scanned; an explicit action can rescan configured WSL roots. Automatic WSL deployment is not implemented.
- Unpacked Java, Node, Maven, and Gradle installations can be discovered in common named directories or other `bin` directories and verified explicitly. Other tool families and layouts are not exhaustively covered. Windows batch paths containing shell metacharacters are refused.
- Gradle version catalog support is limited to selected Kotlin plugin aliases with simple version values. Other catalog references remain unresolved.
- macOS trash, some desktop management flows, and comprehensive desktop accessibility and performance validation remain open.

