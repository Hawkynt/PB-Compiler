# CI/CD Pipeline — {{REPO}}

> Everything in this folder is the automated pipeline for this repository.
> Workflows live here, their helper scripts live in `scripts/`.
>
> This is a **reusable template**. Before first use, fill in every `{{TOKEN}}`
> placeholder (see [Placeholders](#placeholders)) and grep the workflows for
> `# TODO:` to find each substitution site.

## What this does

Three workflows, one shared build block, three helper scripts:

| File                            | Trigger                             | Purpose                                   |
|---------------------------------|-------------------------------------|-------------------------------------------|
| `ci.yml`                        | push + PR + `workflow_call`         | Build + categorised test tiers + coverage |
| `ci.generic.yml`                | push + PR + `workflow_call`         | Toolchain-agnostic CI skeleton (non-.NET) |
| `release.yml`                   | **manual dispatch**                 | Package + publish, then tag `vyyyyMMdd` |
| `nightly.yml`                   | successful CI run on `main`         | Publish `nightly-yyyyMMdd` prerelease   |
| `_build.yml`                    | `workflow_call` (internal)          | Generic publish/packaging building block  |
| `scripts/version.pl`            | invoked by the workflows            | Stamp each csproj's own `<Version>` + build |
| `scripts/update-changelog.mjs`  | invoked by the workflows            | Bucketise commits into CHANGELOG.md       |
| `scripts/prune-nightlies.mjs`   | invoked by the workflows            | 3-gen (GFS) retention of nightlies        |

> `ci.yml` is the .NET pipeline. For anything else (Go / TS-JS / Perl / PHP /
> Batch / PowerShell / Docker / LaTeX-PDF / docs / C++ / Asm / …), use
> `ci.generic.yml` instead (rename it to `ci.yml`) and fill in its build/test
> placeholders — snippets for each toolchain are in the file header.
>
> **Minimum-viable CI is an invariant** (STANDARD.md §4): the required step
> must perform at least one real verification (compiler, interpreter syntax
> pass, linter, renderer, or test run). The generic template's placeholder
> steps therefore **fail until replaced** — a CI that is green without
> checking anything is forbidden.

## How it works

```
                push / PR
                    │
                    ▼
            ┌───────────────┐
            │    ci.yml     │──► tiered tests on ubuntu + windows
            └───┬───────┬───┘    + coverage on ubuntu
                │       │
   dispatch ────┤       │  on success on main (default branch)
                ▼       ▼
        ┌──────────┐  ┌─────────────┐
        │ release  │  │  nightly    │
        │  .yml    │  │   .yml      │
        └────┬─────┘  └─────┬───────┘
             │              │
             ▼              ▼
        (both call _build.yml)
             │              │
             │   Packages the build into shippable artifacts
             │   (your repo plugs its packaging into _build.yml's
             │   clearly-marked TODO steps).
             ▼              ▼
  publish + tag vyyyyMMdd  nightly-yyyyMMdd (prerelease)
                                │
                                ▼
                       scripts/prune-nightlies.mjs
                       (GFS: 7 daily + 4 weekly + 3 monthly)
```

## Test tiers

`ci.yml` runs the test suite in tiers, split by trait/category filter:

| Category           | Runs on every PR?      | Purpose                              |
|--------------------|------------------------|--------------------------------------|
| _default_          | ✓ (must pass)          | Unit tests, no external tools        |
| `EndToEnd`         | ✓ (allow-fail)         | Round-trip through real external CLIs|
| `OsIntegration`    | ✓ (allow-fail)         | Host-OS facilities / binary shell-out|
| `PolyglotInterop`  | ✓ (allow-fail)         | Other-language readers (Py/Perl/...) |
| `Performance`      | ✓ (allow-fail)         | Wall-clock timing asserts (flaky on shared runners) |

Core tests are **required**; the external-tool tiers are **advisory**
(`continue-on-error: true`) so an unavailable CLI on a runner doesn't block a
merge. Generalise the category names to whatever your suite uses, but keep the
required-vs-advisory split: the required tier's filter EXCLUDES every advisory
category, and each advisory tier INCLUDES exactly one.

## What it's for

- Every PR is built and tested on ubuntu + windows before it can merge.
- Every merge to `main` (the canonical default branch — rename old `master` repos, STANDARD.md §2) produces a **tested** nightly prerelease.
- A **manual dispatch** cuts a stable release from artifacts built by `_build.yml`, then tags the dated `vyyyyMMdd` Release at that commit.
- Old nightlies are auto-pruned on a **Grandfather-Father-Son** schedule.

## Why it's built this way

- **No cron triggers.** Event-driven only — CI fires on PRs, nightlies fire when CI passes on main, stable releases fire on manual dispatch.
- **Files drive versions, per-package, never tags.** Each csproj keeps its own `<Version>`; `version.pl --stamp` appends the commit count. There is no single repo version, so the repo-level Release/tag is the date marker `vyyyyMMdd`.
- **Release calls CI via `workflow_call`.** Calling ci.yml explicitly keeps tests and releases in lockstep with zero copy-paste.
- **Nightly builds from the `workflow_run` payload's SHA**, not branch tip — so a nightly is always a build of code CI actually validated.
- **`_build.yml` is the single packaging block**, shared by release and nightly so they never diverge. It runs on windows-latest by default because one Windows host can publish for both Windows and Linux targets without cross-runner artifact passing (switch to ubuntu-latest if you only need Linux).
- **3-generation (GFS) retention**, not "keep last N". GFS guarantees at least one build per week for a month and one per month for a quarter.

## Placeholders

Fill these in across the workflow files (grep for `# TODO:`):

| Token               | Meaning                                                        |
|---------------------|----------------------------------------------------------------|
| `{{REPO}}`          | The repository name (this README's title).                     |
| `{{SOLUTION}}`      | The `.sln` / `.slnx` to restore + build.                       |
| `{{PROJECT}}`       | The project to publish/pack (e.g. `MyApp/MyApp.csproj`).        |
| `{{TEST_PROJECT}}`  | The test project / dir `dotnet test` runs.                     |
| `{{PACKAGE}}`       | The product / artifact base name + display name.               |
| `{{DOTNET_VERSION}}`| The SDK channel to install (default `10.0.x`).                 |

## Scripts

### `version.pl`

The one versioner, identical in every repo. Each package's version is derived
from the **nearest declaration — whatever is in place**, first hit wins:

1. the manifest's own field — `*.csproj`/`*.fsproj`/`*.vbproj` (`<Version>`),
   `package.json` & `composer.json` (`"version"`), `*.pm` (`$VERSION`)
2. the nearest **ancestor** `Directory.Build.props`/`.targets` `<Version>`
   (MSBuild inheritance, .NET only)
3. the repo-root `VERSION` file

BUILD = commits touching the **declaring file's parent folder** (recursive,
`git rev-list --count HEAD -- <dir>`; a repo-root declaration ⇒ whole-repo
count). `--stamp` rewrites the **declaring** files only, so inheritance stays
intact. Composition respects the ecosystem: .NET/Perl get `X.Y.Z.BUILD`,
semver (node/php) get `X.Y.Z+BUILD` (build-metadata). Repos with no
version-bearing file (e.g. Go — versioned by tags) are left untouched.

> **NuGet packages: prefer own folder + own `<Version>`.** An untouched folder
> composes the *identical* version on the next release, so the push step's
> `--skip-duplicate` re-uses the already-published package instead of
> re-uploading it (`C--FrameworkExtensions` relies on this heavily). A
> centralised props/VERSION declaration is a valid starting point, but it
> bumps *all* heirs on every commit below it — migrate to per-package folders
> once a repo ships more than one package.

```
perl .github/workflows/scripts/version.pl --stamp  # rewrite the version in every DECLARING file
perl .github/workflows/scripts/version.pl --build  # print the repo-wide build number (commit count)
perl .github/workflows/scripts/version.pl --list   # "<file>\t<effective-version>" per package,
                                                   # inherited ones annotated with their source
```

> There is no single repo version. Stable releases are tagged with a **date
> marker** `vyyyyMMdd`, not a version.

### `update-changelog.mjs`

Prepends a new section to `CHANGELOG.md` and/or writes release-notes bodies (`--notes <file>`). Commit-subject convention: `+` Added, `*` Changed, `#` Fixed, `-` Removed, `!` TODO, anything else → Other.

Changelog semantics (the contract — see STANDARD.md §4):

- **Releases** measure from the last **stable** tag (`v[0-9]*`) → a release's notes contain *everything since the last release*; same-day `nightly-*` tags never swallow part of the range.
- **Nightlies** measure from the nearest tag of any kind → a nightly's notes contain *only the delta since the previous nightly* (or release, whichever is nearer). `nightly.yml` passes `--notes-only` so `CHANGELOG.md` is only ever committed by `release.yml`.
- The workflow's own `* update changelog for vyyyyMMdd` commits are filtered out of all notes (`isChangelogCommit`), and `release.yml` tags the release **on** that commit — bookkeeping never pollutes the next range.

### `prune-nightlies.mjs`

GFS retention with `DAILY_KEEP=7`, `WEEKLY_KEEP=4`, `MONTHLY_KEEP=3`. Dry-run with `--dry-run`. Invariant: the **newest** nightly is always kept (even with a misconfigured daily quota) — the next nightly's changelog delta is measured against its tag, so deleting it would re-report already-published changes.

## Who maintains this

This is the shared template for the Hawkynt repo family. When changing it,
prototype in the template then mirror the change to the consuming repos.

## Release artifacts

| Artifact                                 | Produced by          |
|------------------------------------------|----------------------|
| `app-artifacts` (binaries, optional)     | release + nightly    |
| `nuget-packages` (`*.nupkg`, optional)   | release + nightly    |
| Coverage HTML report                     | ci.yml (coverage job)|
