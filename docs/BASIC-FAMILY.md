# The DOS-era BASIC family — cross-dialect matrix

Groundwork for cross-compiling the other BASIC dialects of the PB era with the
same `--dialect` mechanism (and the same oracle-driven differential harness)
already used for the PowerBASIC lineage. Everything in this document is
**research from period documentation and folklore** — none of it is
oracle-verified yet. The verification path is the same as for PB: drop the
genuine interpreter/compiler into `tools/<dialect>/`, write
`tests/diff/<dialect>/*.BAS` batteries, and make the outputs byte-identical
before claiming support (see "Oracles & harness" below).

## Lineage

```
BASICA (IBM, ROM-hooked)──┐
                          ├── GW-BASIC 1.0..3.23 (Microsoft, interpreter)
                          │      └── QuickBASIC 1.0..4.5 (compiler + IDE)
                          │             ├── QBasic 1.1 (interpreter subset, DOS 5+)
                          │             └── BASIC PDS 7.0/7.1 ("QB Extended")
                          │                    └── Visual Basic for DOS 1.0
                          └────────────(language conventions)──────────────┐
Turbo Basic 1.0/1.1 (Borland 1987) ── PowerBASIC 2.0/2.1 ── PB 3.0..3.5 ◄──┘
```

PowerBASIC is the direct continuation of Turbo Basic (same author, Bob Zale);
the QB line is its main "compatibility competitor" — most PB statements exist
to compile QB sources unchanged.

## Feature matrix (candidate `--dialect` values in parentheses)

| Area | BASICA / GW-BASIC 3.23 (`gw`) | Turbo Basic 1.1 (`tb11`) | QuickBASIC 4.5 (`qb45`) | QBasic 1.1 (`qbasic`) | BASIC PDS 7.1 (`pds71`) | PowerBASIC 3.5 (`pb35`, reference) |
|---|---|---|---|---|---|---|
| Execution model | interpreter (tokenized lines) | compiler (IDE, in-memory or .EXE) | compiler (BC.EXE + LINK) and IDE interpreter | interpreter only | compiler | compiler (PBC.EXE) |
| Line numbers | **mandatory** on every line | optional | optional (labels allowed) | optional | optional | optional |
| Labels | numeric only | alphanumeric + numeric | alphanumeric + numeric | same as QB | same | same (underscores 3.2+) |
| Line continuation | none | none (statement per line) | `_` | `_` | `_` | trailing `_` |
| Types | INTEGER `%`, SINGLE `!`, DOUBLE `#`, STRING `$` | + LONG `&` (carried into PB 2.0) | + LONG `&` | same as QB45 | + CURRENCY `@` (64-bit scaled; collides with PB's FIX suffix!) | + BYTE/WORD/DWORD/QUAD/EXT/FIX/BCD/FLEX/ASCIIZ (see DIALECTS.md) |
| Default type | SINGLE | SINGLE | SINGLE | SINGLE | SINGLE | SINGLE |
| DEFtype | DEFINT/SNG/DBL/STR | same | same + DEFLNG | same | same | + DEFQUD/DEFFIX/DEFBCD/DEFFLX/DEFEXT |
| Radix literals | `&H`, `&O`, bare `&` octal; 16-bit | `&H`, `&O` | `&H`, `&O`; 16/32-bit by suffix | same | same | + `&B`, 64-bit, signedness rules (QUIRKS.md) |
| Max string length | 255 bytes | 32 KiB | 32 KiB | 32 KiB | 32 KiB | 32 750 bytes |
| Fixed strings / TYPE | none / none | none / none | `STRING * n` inside TYPE; TYPE...END TYPE | same | same + arrays in TYPE | TYPE/UNION, arrays in TYPE (3.5), ASCIIZ |
| SUB/FUNCTION | none (GOSUB/DEF FN only) | SUB...END SUB, multi-line DEF FN | SUB + FUNCTION, DECLARE | same | same + BYVAL | same + CDECL/ALIAS, pointers |
| `DEF FN` | single-line | single + multi-line | single + multi-line | same | same | same |
| SELECT CASE | none | SELECT CASE | SELECT CASE | same | same | same |
| DO/LOOP, EXIT | none (WHILE/WEND only) | DO/LOOP, EXIT | DO/LOOP, EXIT | same | same | same |
| Metacommands | none | `$INCLUDE`, `$INLINE`, `$STACK`, … (real statements) | `REM $INCLUDE: 'f'` / `'$INCLUDE`, `'$STATIC/'$DYNAMIC` (inside comments!) | same | same | `$INCLUDE` etc. as first-class metastatements |
| Conditional compilation | none | none | none | none | `#IF` style? **no** — none | `$IF/$ELSEIF/$ELSE/$ENDIF` |
| Inline asm | none (`CALL`/`USR` to machine code) | `$INLINE` byte lists | none | none | none | `!` statements (3.0+) + `$INLINE` |
| Event trapping | ON KEY/TIMER/COM/PEN/STRIG | same | same | same | same | same + UEVENT |
| Error handling | ON ERROR GOTO / RESUME (line numbers) | + labels | + labels, ERR/ERL | same | same | same + ERRCLEAR (3.5) |
| Graphics | SCREEN 0–9 (BASICA hardware), DRAW, PLAY | same set | SCREEN 0–13 | same | same | same + $-metastatement gated libs |
| PEEK/POKE/DEF SEG | yes (16-bit) | yes | yes | yes | yes | yes + PEEK$/POKE$, VARPTR32 family |
| CHAIN/COMMON | CHAIN (line numbers), COMMON | CHAIN .TBC? (limited) | CHAIN + COMMON (BRUN only) | n/a | CHAIN + COMMON | CHAIN .PBC + COMMON |
| Random files | FIELD/LSET/RSET/MKI$/CVI | same + TYPE-based GET/PUT | TYPE-based GET/PUT preferred | same | same | same |

## Known behavioral quirks to verify per dialect (oracle batteries)

These are the spots where byte-exactness will be won or lost; each needs a
differential battery before the dialect can be claimed:

1. **Number → text formatting.** All family members print numerics as
   `[space|-]digits[space]`, but: GW-BASIC renders DOUBLE exponents with `D`
   (`1D+20`) instead of `E`, SINGLE with `E`; significant-digit counts differ
   (GW: 7/16, QB: 7/15/16, PB: 7/15 with integral-value suppression of the
   decimal point — verified for PB 3.5). `PRINT 2/3` style rounding tails
   differ between interpreters and compilers.
2. **RND sequences.** Different generators per product (QB/QBasic:
   `x = (x*214013+2531011) AND &HFFFFFF`, seeded via RANDOMIZE timer; GW uses
   a different 24-bit LCG; PB another). RND batteries must therefore test
   *properties* (range, RANDOMIZE-with-same-seed reproducibility), or the
   generator must be replicated per dialect for byte-exact streams.
3. **Integer overflow handling.** Interpreters raise `Overflow` (error 6)
   immediately; QB compiled code raises only with debug switches; PB wraps
   silently unless `$ERROR NUMERIC ON` (already replicated for PB).
4. **FOR/NEXT boundary.** GW/QB test *before* increment (a `FOR i% = 1 TO
   32767` terminates); PB increments-then-tests and wraps (QUIRK 2.28,
   replicated). This single difference silently changes loop counts in
   cross-compiled code — it must be dialect-switched in codegen.
5. **String garbage collection pauses** (GW) — observable only via TIMER;
   ignore.
6. **`MOD`/`\` with negative operands** — same truncating semantics family
   wide; verify anyway (cheap battery).
7. **PRINT zones** — 14 columns in all family members; GW pads differently at
   the right screen edge (width 80 wrap). Compare via file output where
   possible; GW PRINT# semantics match.
8. **VAL/STR$ edge cases** — `VAL("1e3")`, `VAL("&HFF")` (GW accepts radix in
   VAL; QB too; PB does as well but verify), leading-space rules in STR$.
9. **Keyword set collisions** — e.g. `LINE INPUT` parsing, `PUT`/`GET`
   graphics vs file forms, reserved words that are valid identifiers in other
   dialects (PB reserves more). The parser's keyword tables must become
   dialect-filtered (the infrastructure exists: `_statementKeywords` lookups
   can consult `DialectFacts`).
10. **Tokenizer differences** — GW allows `?` for PRINT (all do), `'` comments
    (GW: yes), `ELSE` after `THEN` without colon, single-character DEFtype
    ranges. QB45 rejects some GW constructs (e.g. self-modifying line numbers,
    `LIST`-era statements: `AUTO`, `EDIT`, `LLIST` are interpreter commands,
    not language).

## What `--dialect gw|qb45|…` would need in the front end

- **Lexer**: dialect-gated suffix sets (no `?`/`&&`/`@` outside PB; `&` LONG
  suffix only QB45+); `&B` PB-only; mandatory line-number handling for `gw`.
- **Parser**: statement keyword sets per dialect (data-driven, extend
  `DialectFacts` with a family axis: `Family.Microsoft` vs `Family.Borland`);
  QB metacommands inside comments (`'$INCLUDE: 'F.BI'`) need a comment
  scanner; GW requires line numbers and forbids block IF/SUB.
- **Binder**: intrinsic catalogs per dialect (GW lacks `INSTR` start
  argument? — verify; QB lacks `MIN`/`MAX`/`VERIFY`/`EXTRACT$` etc. which are
  PB-only); error-code table identical enough to share.
- **CodeGen/Runtime**: FOR loop test order switch, overflow-check defaults,
  PRINT float formatting per dialect, RND generator per dialect, string
  length limits.

## Oracles & harness

| Dialect | Oracle binary | Harness invocation | Status |
|---|---|---|---|
| `pb35` | `PBC.EXE` 3.50 | harness default battery | **ACTIVE** (`tools/pb35/`), 23 batteries byte-identical |
| `pb30` | `PBC.EXE` 3.0c | `tools/pb30/PBC.EXE` + `tests/diff/pb30/` | **ACTIVE** - installed from the WinWorld 3.0c floppy via the scripted DOSBox installer drive (`tools/_downloads/postkeys.ps1` + window capture); QUIRK30 battery byte-identical, quirks 2.1/2.2, 2.26 and 16-bit HEX$/OCT$ oracle-confirmed |
| `pb32` | `PBC.EXE` 3.20 [German] | floppy + installer staged in `tools/_downloads/pb32de/` (`install32.conf` prepared) | **pending** - installer boots to a screen the capture tool sees black (graphics-mode splash?); finish interactively once, then drop `PBC.EXE` into `tools/pb32/` |
| `pb20/21` | `PB.EXE` 2.00b/2.10/2.10f | archives in `tools/_downloads/` (old-dos.ru ids 191/4254/4256/10317/10593) | **archived** - PB 2.x has no PBC.EXE (the IDE compiles); needs the TB-style IDE drive |
| `qb45` | `BC.EXE` + `LINK.EXE` | `BC T.BAS,T.OBJ; LINK T.OBJ,T.EXE,,BCOM45.LIB;` | **toolchain ready** in `tools/qb45/` (BC/LINK/LIB/BRUN45/BCOM45 from archive.org item qb-450) - awaiting the `qb45` dialect |
| `tb11` | `TB.EXE` 1.1 | IDE-only - drive via `postkeys.ps1` keystroke injection (proven on the PB 3.0c installer) | **binary ready** in `tools/tb11/` (WinWorld 5.25in floppies) |
| `qbasic` | `QBASIC.EXE` | `QBASIC /RUN T.BAS` | not yet fetched (DOS 5+ media) |
| `pds71` | `BC.EXE` (7.1) + `LINK` | same as qb45, far strings differ (`/Fs`) | not yet fetched |
| `gw` | `GWBASIC.EXE` | stdin-scripted interpreter session | not yet fetched |

`scripts/run-diff-tests.sh` already discovers `tests/diff/<dialect>/`
batteries and their `tools/<dialect>/PBC.EXE` oracles generically; the
Microsoft-family oracles need per-dialect compile/run command templates —
planned as a `tools/<dialect>/oracle.conf` snippet (autoexec lines with a
`T.BAS` placeholder) so the script stays data-driven.

## Suggested order of attack

1. **pb30/pb31/pb32 oracles** (same PBC.EXE command line, harness ready
   today) — verifies the quirk emulation already implemented (QUIRKS.md).
2. **qb45** — compiler-based like PB, large real-world source base, most
   reachable byte-exact target.
3. **qbasic/gw** — interpreters; require the oracle.conf mechanism and a
   `SYSTEM`-terminated battery convention.
4. **tb11** — only if keystroke automation proves practical.
