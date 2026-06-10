# Agent guide — PB-Compiler

Working agreement for **all** coding agents (Claude Code, Codex, Copilot, …)
and human contributors working in this repository. These rules are not
optional. The full house spec lives in the `Hawkynt/project-template` repo
(`STANDARD.md`); this file is the per-repo distillation.

## Engineering process

- **Requirements**: IREB-style requirements engineering; divide work into
  **MoSCoW** priorities (Must/Should/Could/Won't) before implementing.
- **Quality assurance**: ISTQB-style testing; specify behavior as
  **Given-When-Then**.
- **Methods**: TDD (failing test first), BDD, DDD, YAGNI, KISS, DRY.
  - *Library nuance*: for general-purpose libraries, YAGNI relaxes on the
    public API surface — you are enabling consumers you don't know yet. KISS
    still governs that surface; over-engineering is acceptable only for
    performance/memory, never for abstraction's own sake.
- **Tests**: equivalence classes, boundary values, and exceptional cases — not
  only the happy path. Don't write *too many* tests (one per equivalence
  class), but do write *enough* (every boundary, every error path).
- Wall-clock/timing assertions go into the `Performance` category (advisory
  tier in CI) — they must never block a merge.

## Commits

- **Before committing**: tests green locally, docs (`README.md` and whatever
  else the change touches) updated to reflect latest changes/usage/issues.
- **Group changes semantically/logically** — one concern per commit; never one
  big "did stuff" commit. Refactorings separate from behavior changes.
- **Every subject line starts with a prefix** (the changelog generator buckets
  by it — an unprefixed commit lands in "Other", which is a defect):
  - `+` added feature/behavior
  - `-` removed feature/behavior
  - `*` changed behavior / public API
  - `#` bug fixed
  - `!` critical todo / open issue worth recording
- Never start a subject with "fix"/"bugfix"/"changed"/"modified" — the prefix
  already says it.
- **No AI traces anywhere**: no `Co-Authored-By` AI lines, no "Generated with"
  footers, no agent mentions in messages, comments, or authorship.

## The loop (always, in this order)

1. **Before committing**: build and run the test suite locally; iterate until
   green. Update the docs.
2. **Commit** (rules above) and **push**.
3. **Wait for CI**: `gh run watch` (or poll `gh run list --branch <branch>`).
   If CI fails, fix it, go to 1.
4. **Wait for the nightly**: on the default branch (`main` — canonical
   everywhere; old-style `master` repos get renamed, never re-adopted), a
   successful CI run triggers `nightly.yml` (builds the validated SHA,
   publishes the `nightly-yyyyMMdd` prerelease, prunes old nightlies). Watch
   it the same way; if it fails, fix, go to 1.
5. **Loop until everything is green.** A pushed change isn't done while any
   workflow it triggered is red.

Stable releases are **manual** (`gh workflow run release.yml`) — never cut one
unless explicitly asked.

## Syntax & style (every language)

Distilled from the C# house style (`C--FrameworkExtensions/CONTRIBUTING.md`);
apply the *spirit* in every language. Where a language has a canonical
formatter (`gofmt`, `cargo fmt`, …), the formatter wins on mechanics — the
structural rules below still apply.

**Formatting**
- Two spaces, no tabs. K&R-style braces.
- Max ~120 chars per line; break at operators/brackets/after commas and indent.
- Methods/functions fit on one screen (~60 lines).
- Spaces around binary operators and after commas; never more than one
  contiguous blank line.

**Structure**
- **Guard clauses first, early returns over nesting** — flat code beats deep
  `if`-pyramids even at the cost of small duplication.
- Validate **all public entry points** (taint thinking), in parameter order,
  with the **most specific error type** the language offers; never re-validate
  in private internals.
- File layout: imports/usings → constants → fields → properties → constructors
  → methods; static before instance; a field used by only one function sits
  directly in front of it. One namespace/module per file; split big classes
  into logically named partials/modules.
- No empty placeholder blocks "for the future".
- Prefer overloads (or explicit variants) over long optional-parameter lists.

**Naming** (translate to the ecosystem's casing canon where it has one)
- Types/public members PascalCase; locals/parameters camelCase; private
  members `_`-prefixed; private constants `_SCREAMING_SNAKE`.
- Interfaces `I`-prefixed, abstract base classes `A`-prefixed, generic type
  parameters `T`-prefixed.
- Booleans start with `Is`/`Has`/`Can`/`Contains`/`Try`; methods start with a
  verb; avoid acronyms that aren't globally known.

**Idiom**
- **Use the newest language features available** — pattern matching, records,
  primary constructors, `yield`, async/await, null-coalescence in C# (polyfill
  older targets with the `FrameworkExtensions.Backports` NuGet); the
  equivalent modern idioms in every other language.
- Prefer the language's concise forms: type inference (`var`), expression
  bodies, initializer syntax, ternaries — when they aid readability.
- Deterministic cleanup is not optional: `using`/`defer`/RAII/`finally` —
  whatever the language gives you.
- Refactoring must **never** make code slower or more memory-hungry; know your
  language's allocation model before "tidying" hot paths.
- Comment the *why* whenever you violate a rule on purpose; meaningful
  comments on anything hard to figure out.
- Boy-scout rule: leave touched code cleaner than you found it (look ±10 lines
  around your change and tidy what you can).

## README & repo conventions

- Keep the standard README shape: title → badges → one-line `>` blockquote →
  screenshot/GIF, fixed emoji headers (`## 📦 Install`, `## 🚀 Usage`,
  `## ✨ Features`, `## 🧩 Packages`, `## 🛠️ Building`, `## ❤️ Support`,
  `## 📜 License`).
- The badge block is generated (`make-badges.mjs`), never hand-edited.
- License is LGPL-3.0-or-later for code repos, CC BY-NC-SA 4.0 for
  creative-content repos (articles, sheet music, courseware); the
  `## ❤️ Support` section and `.github/FUNDING.yml` stay intact.
- `CHANGELOG.md` is generated by the workflows from commit subjects — never
  edit it by hand.
