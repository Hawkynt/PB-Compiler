# PB-Compiler

[![License](https://img.shields.io/github/license/Hawkynt/PB-Compiler)](https://github.com/Hawkynt/PB-Compiler/blob/main/LICENSE)
[![Language](https://img.shields.io/badge/language-C%23-178600)](https://github.com/Hawkynt/PB-Compiler)

[![CI](https://github.com/Hawkynt/PB-Compiler/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/PB-Compiler/actions/workflows/ci.yml)
![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/PB-Compiler?branch=main)
![Activity](https://img.shields.io/github/commit-activity/m/Hawkynt/PB-Compiler)

[![Stars](https://img.shields.io/github/stars/Hawkynt/PB-Compiler?color=FFD700)](https://github.com/Hawkynt/PB-Compiler/stargazers)
[![Forks](https://img.shields.io/github/forks/Hawkynt/PB-Compiler?color=008080)](https://github.com/Hawkynt/PB-Compiler/network/members)
[![Issues](https://img.shields.io/github/issues/Hawkynt/PB-Compiler)](https://github.com/Hawkynt/PB-Compiler/issues)
![Code Size](https://img.shields.io/github/languages/code-size/Hawkynt/PB-Compiler?color=4CAF50)
![Repo Size](https://img.shields.io/github/repo-size/Hawkynt/PB-Compiler?color=FF9800)

[![Release](https://img.shields.io/github/v/release/Hawkynt/PB-Compiler)](https://github.com/Hawkynt/PB-Compiler/releases/latest)
[![Nightly](https://img.shields.io/github/v/release/Hawkynt/PB-Compiler?include_prereleases&sort=date&filter=nightly*&label=nightly&color=FF9800)](https://github.com/Hawkynt/PB-Compiler/releases)
[![Downloads](https://img.shields.io/github/downloads/Hawkynt/PB-Compiler/total)](https://github.com/Hawkynt/PB-Compiler/releases)

**A from-scratch PowerBASIC-family compiler that turns vintage BASIC into real
16-bit DOS executables — written in modern C#, runnable on any 64-bit host.**

PB-Compiler (`pbc`) reads unmodified BASIC source from the DOS era and emits real
binaries you can run on actual DOS or in DOSBox:

- **`.EXE`** — DOS MZ executables for 8086+ real mode,
- **`.PBU`** — compiled units (`$COMPILE UNIT`),
- **`.PBL`** — unit libraries (linkable via `$LINK`).

Two things make it interesting. First, **fidelity**: for the historic dialects
it doesn't merely *resemble* the genuine compilers — it is driven against the
original binaries and produces **byte-identical output**, documented bugs and
all. Second, **a forward path**: a real SSA-based optimization pipeline and
lean-output backend — available in *every* dialect via `--optimize` and on by
default in the `pb36` language-features superset — that drops a hello world from a
fat always-linked runtime to a 25-byte image while keeping every existing program
byte-identical.

## Why

PowerBASIC for DOS is proprietary, 16-bit and long out of print — it cannot run
on modern 64-bit hosts. PB-Compiler is a clean-room, cross-platform
reimplementation so that PB codebases (such as
[PB-SvgaLibrary](https://github.com/Hawkynt/PB-SvgaLibrary)) can be built and
verified on modern machines and CI, while the binaries it produces still run on
the original target: 8086+ real mode under DOS or DOSBox. Along the way it grew
into a broader DOS-BASIC toolchain — Turbo Basic, QuickBASIC and BASIC PDS — and
into `pb36`, a what-if "next PowerBASIC" that keeps the language but rebuilds the
back end around modern optimization.

## Supported dialects

The dialect is chosen with `--dialect` (default `pb35`). Selecting an older
dialect both **gates** newer language features (they raise a diagnostic) and
**re-enables that version's documented bugs** — bug compatibility is part of
fidelity (see [docs/QUIRKS.md](docs/QUIRKS.md)). Two product families share the
flag: the Borland lineage (Turbo Basic → PowerBASIC, same author, Bob Zale) and
the Microsoft lineage (QuickBASIC → BASIC PDS).

| Dialect | Flag | Family / era | Notes |
|---|---|---|---|
| Turbo Basic 1.0 / 1.1 | `tb10` `tb11` | Borland, 1987 | PowerBASIC's direct ancestor; 16-significant-digit "double-everything" runtime, three-digit exponents. Byte-identical to genuine TB.EXE. |
| PowerBASIC 2.0 / 2.1 | `pb20` `pb21` | Borland | Core BASIC baseline (no inline `!` asm, no QUAD, no pointers). `pb21` verified byte-identical against PB.EXE 2.10. |
| PowerBASIC 3.0 | `pb30` | Borland, 1993 | Inline assembler, 80386 codegen, unsigned types, QUAD, TYPE/UNION, HUGE arrays. Byte-identical against PBC.EXE 3.0c. |
| PowerBASIC 3.1 | `pb31` | Borland | Typed radix literals, whole-UDT compare, `ALIAS`, `ANY` parameters. |
| PowerBASIC 3.2 | `pb32` | Borland | Data and code pointers, `VARPTR32`/`STRPTR32`/`CODEPTR32`, identifier underscores. |
| **PowerBASIC 3.5** | **`pb35`** | Borland, 1997 | **The reference dialect (default).** ASCIIZ, `&` concat, VIRTUAL arrays, `REDIM PRESERVE`, indexed pointers, STDIN/STDOUT, `TRIM$`, `SIZEOF`, and more. Byte-identical against PBC.EXE 3.50. |
| **PowerBASIC 3.6** | **`pb36`** | Borland (envisioned) | **The language-features superset.** A strict superset of `pb35`: every `pb35` program compiles unchanged with byte-identical behavior, plus opt-in modern syntax. |
| BASICA / GW-BASIC | `basica` `gw` | Microsoft (interpreters) | The classic line-numbered interpreters. SINGLE is stored in genuine Microsoft Binary Format (MBF) with x87 conversion on load/store; DOUBLE (MBF 8-byte) and the interpreter-oracle harness are in progress (see roadmap below). |
| QuickBASIC 1.0–4.5 | `qb10` `qb20` `qb30` `qb40` `qb45` | Microsoft | QB display model (D exponents), BASCOM runtime heritage (^Z, half-away rounding) through 3.0. Verified byte-identical against the genuine BASCOM/BC/QB toolchains. |
| QBasic | `qbasic` | Microsoft (interpreter) | The MS-DOS 5.0+ interpreter — the QuickBASIC 4.5 language (IEEE floats) minus the compiler. Compiles via the QB 4.5 front end; oracle-verified by interpreter output diff (in progress). |
| BASIC PDS 7.0 / 7.1 | `pds70` `pds71` | Microsoft | "QB Extended"; 15-digit DOUBLE display. Byte-identical against BC.EXE 7.0/7.1. |

The historic dialects (everything except `pb36`) are validated by an
oracle-driven **differential harness**: when the genuine compiler is dropped into
`tools/<dialect>/`, `scripts/run-diff-tests.sh` compiles each test program with
both `pbc` and the original and asserts the outputs match byte for byte. See
[docs/DIALECTS.md](docs/DIALECTS.md) for the PowerBASIC feature matrix and
[docs/BASIC-FAMILY.md](docs/BASIC-FAMILY.md) for the cross-family lineage.

## PowerBASIC 3.6 — what's new

`pb36` answers a simple question: *what if there had been one more DOS release?*
It is the **language-features** dialect — a strict superset of `pb35` that adds
opt-in modern syntax and compiler sugar while keeping the same observable behavior.
Every `pb36` construct is rejected below 3.6 with a `requires PowerBASIC 3.6`
diagnostic, and none of it changes the meaning of an existing `pb35` program. Full
detail in [docs/PB36.md](docs/PB36.md); the highlights:

> **Optimization is a separate, dialect-independent axis.** The full optimization
> pipeline (below) is always reachable from the command line in *any* dialect via
> `--optimize`; `pb36` simply turns it on by default. So you can compile faithful
> `pb35` (or QuickBASIC, or Turbo Basic) source with the optimizer on, or compile
> `pb36` with `--no-optimize` — syntax level and optimization level are chosen
> independently.

**Declarations and initialization**
- **`DIM` with initializer** — `DIM x = value` (type inferred) or
  `DIM x AS type = value`.
- **Array initializer literals** — `DIM a%() = {10, 20, 30}`, with inclusive
  ranges (`lo..hi`) and spread of a static array (`..arr`); the same literal in
  square brackets — `DIM a%() = [99..105]` — doubles as a range/collection literal.
- **Object initializers** — `DIM p = NEW Udt { .field = value, ... }`.
- **`ENUM` blocks** — named compile-time integer constants in their own
  namespace; the enum name aliases its underlying integer type.

**Expressions and operators**
- **Compound assignment** — `+= -= *= /= \= ^= &=` (e.g. `n% += 1`, `s$ &= t$`).
- **Short-circuit ternary `IF()`** — `IF(cond, whenTrue, whenFalse)` evaluates
  only the taken branch.
- **`ANDALSO` / `ORELSE`** — short-circuiting boolean operators (vs. PB's bitwise
  `AND`/`OR`).
- **Shift / rotate / bitwise operators** — `<<`, `>>`, `<<<`, `>>>`, `<<>`,
  `<>>`, `|`, each with a compound-assignment form.
- **Scaled pointer arithmetic** — `ptr +* index` / `ptr -* index` step a typed
  pointer by element size (leaving raw `ptr + n` unscaled, as before).
- **From-end array index** — `arr(^1)` is the last element.

**Procedures**
- **Expression-bodied `FUNCTION`** — `FUNCTION F(...) AS T = expression`.
- **`FUNCTION` returning a TYPE by value** — `FUNCTION MakeP(...) AS Point`; the result
  is written straight into the assignment target (struct return, no copy).
- **Tuples / multiple return values** — `FUNCTION DivMod(...) AS (LONG, LONG)` returns an
  anonymous tuple (struct return); `q, r = DivMod(a, b)` destructures it. A tuple type
  `(T1, T2, …)` is an ordinary value aggregate (fields `Item1`…`ItemN`).
- **Overloading** — several SUBs/FUNCTIONs may share a name with different
  signatures; calls resolve to the best match.
- **Default and named parameters** — `SUB Foo(x%, y% = 10)` and `Foo(y := 5)`.
- **Nested local SUB/FUNCTION** — defined inside another proc, with true
  stack capture of the enclosing proc's locals (lifted, captured BYREF).
- **Lambdas and closures** — `FUNCTION(params) AS T => expr`, with concise
  `(a, b) => a + b` and single-parameter `x => 2 * x` forms (the `=>` arrow is a
  distinct token from `>=`), and parameter/result types inferred from the delegate
  they're bound to. A lambda may **capture** the enclosing proc's locals by
  reference (a stack closure: the environment travels with the delegate value).
- **Typed procedure pointers (delegates)** — `DIM f AS FUNCTION(types) AS type`,
  assignable from a lambda or `CODEPTR32` and **callable directly** (`PRINT f(7)`).
- **Named delegate types** — a `DECLARE`d SUB/FUNCTION name doubles as a
  procedure-pointer type, usable as a variable type or a parameter type for
  statically-checked higher-order procedures.

**Object model (compile-time, no vtables)**
- **TYPE methods and properties** — declare `SUB` / `FUNCTION` / `PROPERTY GET` /
  `PROPERTY SET` *inside* the `TYPE` block; the receiver is the keyword **`THIS`**.
  Each lifts to a plain procedure with the instance passed BYREF, fully resolved
  at compile time (no inheritance, no late binding). `o.Method(a)`, `o.Prop`, and
  `o.Prop = x` desugar to those calls.
- **Auto-implemented properties** — a `PROPERTY GET`/`SET` with no body gets a
  hidden backing field; inside a body, **`FIELD`** is that backing field and
  **`VALUE`** is the setter's incoming value. Expression bodies use `=>`:
  `PROPERTY GET Area AS LONG => THIS.W * THIS.H`, `PROPERTY SET Size() => FIELD = 2 * VALUE`.
- **Anonymous full properties** — `PROPERTY Count AS LONG` (no `GET`/`SET`)
  synthesizes *both* a trivial getter and setter over one backing field.
- **Trivial methods inline** — the optimizer inlines *any* trivial method body
  (auto-generated accessor or hand-written), treating the `THIS` receiver as the
  ordinary BYREF argument it is; a method inlined at every call site is purged. So
  `o.Count` (an anonymous property) is as cheap as a field, and a hand-written
  `FUNCTION Sum() = THIS.x + THIS.y` inlines the same way — no property-specific path.
- **Constructors** — a `SUB` named like the `TYPE` is its constructor (with `THIS`
  access); `p = Point(3, 4)` runs it with the target as the BYREF receiver.
- **`READONLY` types** — `TYPE Point READONLY … END TYPE` makes every field
  write-once: assignable only inside the type's own constructor, rejected
  elsewhere at compile time.
- **Bit-field members** — `Mode AS BIT * 3` / `Enabled AS BIT` (1..16 bits) packs
  consecutive bit-fields into a hidden 16-bit storage word; a read becomes a
  shift-and-mask and a write a read-modify-write that preserves the neighbouring
  fields — pure binder desugar over WORD arithmetic, ideal for register/hardware structs.
- **`OPERATOR` overloading** — define `OPERATOR + (other AS Vec) AS Vec` (and `=`, `<`,
  `MOD`, …) inside a `TYPE`; `THIS` is the left operand, the body sets the `RESULT`. `a + b`
  resolves to the overload at compile time (a value-type feature, no dispatch). A
  TYPE-returning operator writes through struct return (`c = a + b`); a scalar-returning
  one (`a = b`) works in any expression.
- **Compile-time generics** — generic types `TYPE Stack OF T … END TYPE`
  (`DIM s AS Stack OF LONG`) and generic procedures `FUNCTION Max OF T (…) AS T`
  (the type argument is **inferred** from the call: `Max(3, 9)`, `Max("a", "b")`).
  Each instantiation is monomorphized into ordinary concrete code (fully resolved at
  compile time, no runtime type info or boxing), so methods, properties and the
  trivial-method inliner all apply per instantiation.

**Generators (`YIELD` coroutines)**
- **First-class generators** — any `SUB`/`FUNCTION` whose body contains `YIELD` is
  automatically a generator; calling it returns a synthesized **enumerator** value
  (a UDT named after it) you can store in a variable and drive with
  `.MoveNext` / `.Current` / `.Reset`, or consume with `FOR EACH`. Parameters and
  locals persist across suspensions as enumerator fields.
- **`YIELD` anywhere in structured control flow** — inside `FOR`, `WHILE`/`DO`
  loops, `IF`, `SELECT CASE`, a `FOR EACH` over *another* generator (the inner
  iterator's state is preserved across the outer yields), and `TRY`/`CATCH`/`FINALLY`
  (the ON ERROR handler is saved in enumerator fields and re-armed per resume, since a
  yield unwinds the frame); all flattened to a resumable state machine. (Only a `TRY`
  that yields while nested inside another yielding `TRY` is not yet supported.)

**Structured exception handling**
- **`TRY` / `CATCH` / `FINALLY`** — block-structured error handling lowered onto the
  ON ERROR trap; `FINALLY` runs on every exit path.
- **`DEFER <statement>`** — scope guard: runs the statement when the enclosing block
  exits, on normal completion or while a fault unwinds; nested DEFERs run last-in-first-out.
- **Filtered / typed `CATCH`** — `CATCH <errnum>`, `CATCH WHEN <cond>`, or
  `CATCH <errnum> WHEN <cond>`; several filtered clauses are tried in order (the
  `WHEN` guard is evaluated only when the number matches), an unfiltered `CATCH` is
  the catch-all, and if no clause matches the error re-raises to the outer handler
  *after* `FINALLY`. It's sugar over the handler switch (an `IF`/`ELSEIF` chain).

**Memory and control flow**
- **`WITH expr … END WITH`** — leading-dot member access on a subject.
- **`FOR EACH v IN source`** — iterate an array's elements, a `[lo..hi]` range, or
  a generator's yielded sequence.
- **XMS / EMS arrays** — `DIM XMS a(...)` / `DIM EMS a(...)` storage classes
  alongside `VIRTUAL`.

> Closures are stack-based for now — a closure that *escapes* its defining
> procedure (e.g. returned to outlive its frame) is roadmap, to be backed by a
> heap environment (see [docs/PB36.md](docs/PB36.md)).

## Optimizations

The optimizer sits between the binder and the emitter, working on the bound
`SemanticModel` — the shared intermediate representation every dialect produces.
Because it reads each dialect's own types, wrap rules and semantics, it preserves
observable behavior for *all* dialects, not just PB. It is therefore
**dialect-agnostic machinery** (the *what if there had been one more release?* point
of `pb36`), driven entirely by the `--optimize` / `--no-optimize` switches described
above. Even with the optimizer forced on for every historic dialect, all
differential batteries still pass byte-identically — so optimization never costs
fidelity.

At its center is a real SSA mid-end (`CodeGen/Ssa/`): control-flow graph →
dominator tree and dominance frontiers → SSA construction → sparse conditional
constant propagation → dead-store elimination.

| # | Optimization | What it does |
|---|---|---|
| O1 | Constant folding | Folds pure integral expressions at the emitter, wrapped to the bound type (bit-equal to the runtime ALU). |
| O2 | Dead-code / dead-store elimination | Drops unreachable statements (`OptPruner`) and, over SSA, removes stores whose value is never really read (`Ssa/DeadStore`). |
| O3 | Common-subexpression elimination | Block-local CSE, with inheritance into dominated branches and across barrier-free merges (`OptCommonSubexpr`). |
| O4 | Strength reduction | `x * 2^n`, `x \ 2^n`, `x MOD 2^n` lower to shift/mask sequences (with PB's truncation fix-ups); richer multiplier shapes under `$OPTIMIZE SPEED`. |
| O5 | Register allocation | Keeps a FOR counter and one hot integer accumulator in SI/DI across a loop; broader allocation is roadmap. |
| O6 | Inlining | Inlines a single-result-expression FUNCTION at its call sites; induction-variable pointer-stepping for array loops. |
| O7 | Loop unrolling | Fully unrolls small constant-trip INTEGER FOR loops under `$OPTIMIZE SPEED`. |
| O8 | Peephole / zero-idiom | `XOR r,r` for zero, immediate-folded ALU ops, `INC`/`DEC` and `OR AX,AX` collapses (16- and 32-bit paths). |
| O9 | String-temp / concat folding | Folds pure literal concatenations into one pooled literal. |
| O10 | Redundant-statement / DEF SEG coalescing | Drops a `DEF SEG` whose window contains only segment-transparent statements. |
| O11 | Literal overlap pooling | Overlapping/contained string literals share bytes in one pool. |
| O12 | Float demotion | Re-types accidental SINGLE/DOUBLE loop counters back to INTEGER/LONG when every use is value-exact (`OptFloatDemotion`). |
| O13 | Fixed-point lowering | Integral-promotion trees over 16-bit leaves run on the plain 16-bit ALU instead of round-tripping the x87. |
| O14 | Tail-call optimization | Self-calls in tail position become a jump to frame entry — recursion in constant stack space. |
| O15 | UDT zero-cost copy/compare | Word/DWORD-wide block copy & compare; self-copy elided, self-compare folded. |
| O16 | Range / bounds-check elimination | A FOR-counter range lattice removes provably-safe `$ERROR` BOUNDS/OVERFLOW/divide-by-zero checks and folds invariant comparisons. |
| O17 | SCCP / branch folding | Sparse conditional constant propagation over SSA folds constant branches and proves zero-initialized reads (`Ssa/Sccp`). |
| O18 | Interprocedural constant propagation | A parameter that is the same constant at every call site and never written reads as that literal inside the callee (`OptIpcp`). |
| O19 | Definite-assignment zero elision | Drops per-invocation frame zeroing when a straight-line proof shows no local is read before assignment. |
| O20 | Idiom replacement | Recognizes and replaces common code idioms. |
| O21 | Copy propagation | A copy `y = x` redirects reads of `y` to `x` and drops the copy (`OptCopyProp`). |
| O22 | Loop-invariant code motion | Hoists a pure loop-invariant subexpression to the FOR preheader under `$OPTIMIZE SPEED`. |
| O23 | `SELECT CASE` → jump table | A dense integer `SELECT CASE` dispatches through a word jump table instead of a compare chain (`TryEmitSelectJumpTable`). |

Lean-output passes complement them (also gated on the optimizer, not the
dialect): **P1** runtime trimming (emit only the runtime sections reachable code
references), **P2** data-on-demand, **P3** BSS instead of image bytes, **P4**
right-sized memory footprint, **P6** header squeeze, and **P7** trivial-I/O
lowering (a PRINT-only program becomes a raw COM-style image). Codegen passes
**C1/C2** target 386/486 (32-bit value flow, alignment, `BSWAP`) and **R3**
widens string/block moves to DWORDs under `$CPU 80386`. Several passes are
implemented as verified subsets with deeper forms on the roadmap — see the method
coverage matrix in [docs/PB36.md](docs/PB36.md). Also on the roadmap: collapsing a
chain of mutually-exclusive `IF x = k` tests (`IF x = 1 … ELSEIF x = 2 …`) into the
same kind of jump table as `SELECT CASE`.

## Getting started

### Build

```bash
git clone https://github.com/Hawkynt/PB-Compiler
cd PB-Compiler
dotnet build -c Release
```

`pbc` (the CLI front end) is built from `pbc/`; the compiler itself lives in the
`PowerBasic.Compiler/` library.

### Compile a program

```basic
' HELLO.BAS
PRINT "Hello, World!"
```

```bash
dotnet run --project pbc -- HELLO.BAS        # -> HELLO.EXE (DOS MZ, real mode)
```

Then run the result in DOSBox:

```bash
dosbox -c "mount c ." -c "c:" -c "HELLO.EXE"
```

### Usage

```bash
pbc HELLO.BAS                 # -> HELLO.EXE (DOS MZ, real mode)
pbc --dialect pb36 HELLO.BAS  # pb36 syntax features (optimizer on by default)
pbc --dialect qb45 OLD.BAS    # compile a QuickBASIC 4.5 source
pbc --optimize OLD.BAS        # run the optimizer for any dialect
pbc --no-optimize APP.BAS     # disable the optimizer (faithful codegen)
pbc -G386 TEST.BAS            # allow 80386 instructions ($CPU 80386)
pbc UNIT.BAS                  # $COMPILE UNIT inside -> UNIT.PBU
pbc MAIN.BAS                  # $LINK "UNIT.PBU" / "MY.PBL" inside -> linked EXE
pbc lib build MY.PBL *.PBU    # bundle units into a library
pbc lib list MY.PBL           # show exports/imports of a library or unit
```

Useful options: `-O <file>` (output name), `-I <dir>` (`$INCLUDE` search path),
`-L <dir>` (`$LINK` search path), the runtime-check switches `-EB`/`-EN`/`-EO`/`-ES`
(bounds/numeric/overflow/stack), and `-OZF` (`$OPTIMIZE SPEED`). Run `pbc --help`
for the full list.

## Status

Under construction — see [REQUIREMENTS.md](REQUIREMENTS.md) for the MoSCoW
breakdown and [CHANGELOG.md](CHANGELOG.md) for progress. In short: the full
PowerBASIC 3.5 surface (lexer/preprocessor, parser, semantics, 8086–386 + x87
assembler, MZ/PBU/PBL emitters and DOS runtime) is in place and exercised against
the [PB-SvgaLibrary](https://github.com/Hawkynt/PB-SvgaLibrary) corpus; the
cross-vendor dialects, the `pb36` language features, and the optimizer (run across
every dialect) are validated by the oracle differential harness and DOSBox
execution tests.

## Layout

| Path | What |
|------|------|
| `PowerBasic.Compiler/` | Compiler library: lexer, parser, semantics, 8086 assembler, code generator, optimizer, MZ/PBU/PBL emitters, DOS runtime |
| `pbc/` | Command-line front end |
| `PowerBasic.Compiler.Tests/` | NUnit test suite (TDD, Given-When-Then) |
| `tests/` | PowerBASIC test battery executed under DOSBox (incl. `tests/diff/` differential oracles) |
| `scripts/` | DOSBox integration & differential harness |
| `docs/` | Dialect matrices, quirks, and the `pb36` design |

## Contributing

Contributions are welcome. The bar is the same one the project holds itself to:
historic-dialect changes must stay byte-identical under the differential harness,
and the test suite (NUnit, Given-When-Then) must stay green. Start with
[REQUIREMENTS.md](REQUIREMENTS.md) and the docs above.

## Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## License

Licensed under LGPL-3.0-or-later — see [LICENSE](LICENSE).
