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

## Sourcing an implementation

Never write a format, codec, cipher or compression scheme out of your own understanding when
somebody has already got it right. Work **down** this ladder, stop at the first rung that applies,
and say in the commit body which rung you used and why the ones above it did not.

**1 — Licence-compatible source you can take.** MIT, BSD, Apache-2.0, LGPL, public domain: anything
this repository's LGPL-3.0-or-later can absorb. Search for it before writing anything. There are two
ways to take it and the choice is not cosmetic:

- **Vendor it** — a verbatim subtree under `Vendored/<Library>/` next to its own `LICENSE.txt`, kept
  in the upstream's own formatting. Do *not* restyle it: the whole point is that the next upstream
  version still applies cleanly, and a reformatted copy conflicts on every update. Keep it out of
  the published API surface with the `exclude-namespace` input of the `package-readme` action rather
  than by editing the source.
- **Convert it** — carry the algorithm across into this codebase properly. Converted code is *our*
  code, so every rule under "Syntax & style" applies to it, including the current C# language
  version (C# 14) wherever that says the same thing more plainly. Do not restate those rules
  here or anywhere else: one stale copy of them is how this guide spent years asking for a brace
  style the code had never used. A conversion that still reads like C, or like a decompiler's
  output, is not finished.

Either way, record where it came from — a `THIRD_PARTY_NOTICES.md` in the package, or a
`THIRD-PARTY-NOTICE.<Name>.txt` beside the code. Attribution is a licence term, not a courtesy.

**2 — Licence-incompatible source: use it, but not its code.** GPL where we ship LGPL, anything
proprietary, anything with no licence at all. Read it and *build material from it*: a written
specification, a set of test cases, and a third-party oracle you can run to produce expected output.
Then implement from that derived material. Do not paste it, do not transliterate it line by line,
and do not carry its file layout or its identifier names across — that is still the same copy.

**Constants are not expression.** Tables, S-boxes, magic numbers, CRC polynomials, Huffman code
tables, quantisation matrices, window and filter coefficients: copy them exactly, from whichever
source is authoritative, on every rung of this ladder. A re-derived S-box is simply a wrong S-box,
and a table somebody worked out for themselves is the defect that nothing catches until real files
arrive. Where a value is arbitrary-but-agreed, matching it *is* the specification.

**3 — Original reference material.** The specification, the standard (RFC, ITU-T, ISO, ECMA), the
academic paper, the vendor's own documentation, the format author's write-up. Prefer the normative
text over anybody's description of it; where the two disagree, the normative text wins and the
disagreement is worth a comment.

**4 — Other trusted sources.** Reverse-engineering write-ups, articles and blog posts by named
people with a track record, and long-lived project wikis that cite their evidence.

**5 — Untrusted material, by agreement only.** Forum answers, unattributed gists, wiki edits with no
provenance. Only when nothing above exists, and only where several *independent* sources agree —
majority vote, discounting the ones that plainly copied each other. Treat the result as a hypothesis
and mark it as one in the code.

Whatever rung you land on, the finished implementation is judged the same way: it must agree with an
oracle or with real files, not merely compile and look plausible. When a licence-incompatible
implementation was your oracle, keep the comparison as a test wherever it can run, and where it
cannot, commit the captured expected output with a note saying what produced it.

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
