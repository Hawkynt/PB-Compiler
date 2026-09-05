# CI/CD pipeline — PB-Compiler

Four workflows, one shared build block, three helper scripts. This is the
repository's own pipeline, filled in from the shared Hawkynt template; the
template itself and the family standard it implements live in
[`Hawkynt/RepositoryTemplate`](https://github.com/Hawkynt/RepositoryTemplate),
which `smoke.yml` calls directly.

| File                           | Trigger                                  | Purpose                                              |
| ------------------------------ | ---------------------------------------- | ---------------------------------------------------- |
| `smoke.yml`                    | push to any branch except `main`          | Fast tier: one OS, fast tests, minutes                |
| `ci.yml`                       | pull request + `workflow_call` + dispatch | Syntax oracle, then the full matrix and every battery |
| `nightly.yml`                  | push to `main` + dispatch                 | Publish the `nightly-yyyyMMdd` prerelease             |
| `release.yml`                  | **manual dispatch**                       | Run CI, package, publish, tag `vyyyyMMdd`             |
| `_build.yml`                   | `workflow_call` (internal)                | The single publish/packaging block                    |
| `scripts/version.pl`           | invoked by `_build.yml`                   | Stamp each csproj's own `<Version>`                   |
| `scripts/update-changelog.mjs` | invoked by nightly + release              | Bucketise commits into `CHANGELOG.md` / release notes |
| `scripts/prune-nightlies.mjs`  | invoked by `nightly.yml`                  | 3-generation (GFS) retention of nightlies             |

## How it works

```
   push to a working branch          pull request
              │                            │
              ▼                            ▼
       ┌────────────┐              ┌──────────────┐
       │ smoke.yml  │              │    ci.yml    │──► syntax-oracle job (required)
       │ ubuntu     │              │              │──► test job: ubuntu + windows
       │ fast tests │              └──────────────┘
       └────────────┘                     │
                                          │ merge
                                          ▼
                                     push to main
                                          │
   manual dispatch ───┐                   │
                      ▼                   ▼
              ┌────────────┐      ┌──────────────┐
              │ release.yml│      │ nightly.yml  │
              └─────┬──────┘      └──────┬───────┘
                    │  (both call _build.yml)     │
                    ▼                             ▼
          publish + tag vyyyyMMdd    nightly-yyyyMMdd (prerelease)
                                                  │
                                                  ▼
                                       prune-nightlies.mjs
                                       (7 daily + 4 weekly + 3 monthly)
```

`_build.yml` publishes self-contained `pbc` binaries for `win-x64` and
`linux-x64`. This repo ships no NuGet packages, so `push-nuget` stays false
everywhere except a manual release, where there is nothing to push.

## What ci.yml actually runs

Two jobs. **`syntax-oracle`** is deliberately separate and required: it feeds
every project statement form to genuine PBC 3.50 and BASIC PDS 7.0/7.1 under
DOSBox, so an unrelated failure in the ordinary suite cannot quietly skip the
historical accept/reject contract. It needs the `PB_TOOLCHAIN_KEY` secret (see
`tools/README.md`).

**`test`** runs on ubuntu + windows:

| Step                        | Required? | What it proves                                            |
| --------------------------- | --------- | --------------------------------------------------------- |
| Core tests                  | ✓         | The NUnit suite, plus the DOSBox execution tests on Linux  |
| Round-trip back-emitter gate| ✓ (Linux) | Every corpus program emit-basic's back to compiling pb35   |
| DOS golden battery          | advisory  | `tests/*.BAS` through the CLI and DOSBox vs `*.expected`   |
| Differential oracle battery | advisory  | `tests/diff/**` vs the genuine vintage compilers           |
| Performance tests           | advisory  | Wall-clock assertions (`Performance` category)             |

The advisory steps carry `continue-on-error: true`: an emulator quirk or an
absent oracle toolchain must not block a merge, and a wall-clock assertion on a
shared runner never gates anything.

> **`TestCategory`, never `Category`.** The NUnit VSTest adapter exposes the
> trait as `TestCategory`. A `Category=` filter still *executes* the fixtures it
> claims to exclude and only narrows the reporting, which is how the core tier
> once ran the whole suite twice.

The suite currently defines `Slow` and `Probe`. `smoke.yml` excludes `Slow` (the
corpus-wide batteries) and `Performance` so the fast tier stays fast; `ci.yml`
runs everything.

## Why it is built this way

- **No cron.** Event-driven only: smoke on push, CI on pull requests, nightlies
  on merges to `main`, stable releases on manual dispatch.
- **Files drive versions, never tags.** Each csproj keeps its own `<Version>`
  and `version.pl --stamp` appends the commit count. There is no single repo
  version, so the repo-level tag is the date marker `vyyyyMMdd`.
- **`release.yml` calls `ci.yml` via `workflow_call`**, so tests and releases
  stay in lockstep with no copy-paste.
- **`_build.yml` is the only packaging block**, shared by release and nightly so
  the two cannot diverge.
- **GFS retention, not "keep last N".** Grandfather-Father-Son guarantees at
  least one build per week for a month and one per month for a quarter.

## Scripts

### `version.pl`

Each package's version comes from the nearest declaration, first hit wins: the
manifest's own `<Version>`, then the nearest ancestor
`Directory.Build.props`/`.targets`, then a repo-root `VERSION` file. Here that
resolves to `Directory.Build.props`. BUILD is the commit count touching the
declaring file's parent folder, so a root-level declaration means the whole-repo
count. `--stamp` rewrites only the declaring files, leaving MSBuild inheritance
intact.

```
perl .github/workflows/scripts/version.pl --stamp  # rewrite every declaring file
perl .github/workflows/scripts/version.pl --build  # print the build number
perl .github/workflows/scripts/version.pl --list   # per-package effective versions
```

### `update-changelog.mjs`

Prepends a section to `CHANGELOG.md` and/or writes a release-notes body
(`--notes <file>`), bucketing commit subjects by their prefix: `+` Added,
`*` Changed, `#` Fixed, `-` Removed, `!` TODO, anything else Other.

- **Releases** measure from the last stable tag (`v[0-9]*`), so a release's
  notes carry everything since the previous release and a same-day `nightly-*`
  tag cannot swallow part of the range.
- **Nightlies** measure from the nearest tag of any kind, so a nightly carries
  only the delta since the previous one. `nightly.yml` passes `--notes-only`;
  `CHANGELOG.md` is committed by `release.yml` alone.
- The workflow's own `* update changelog for vyyyyMMdd` commits are filtered out
  of every range (`isChangelogCommit`), and `release.yml` tags **on** that
  commit, so bookkeeping never pollutes the next range.

### `prune-nightlies.mjs`

GFS retention with `DAILY_KEEP=7`, `WEEKLY_KEEP=4`, `MONTHLY_KEEP=3`; `--dry-run`
to preview. The newest nightly is always kept even under a misconfigured quota,
because the next nightly's changelog delta is measured from its tag.

## Release artifacts

| Artifact                                    | Produced by       |
| ------------------------------------------- | ----------------- |
| `app-artifacts` — self-contained `pbc` for win-x64 + linux-x64 | release + nightly |
