# Requirements — PB-Compiler

IREB-style requirements for a PowerBASIC 3.5 (DOS) compatible compiler written
in C#, emitting 16-bit real-mode DOS artifacts.

## Vision

A single cross-platform .NET CLI (`pbc`) that compiles unmodified
PowerBASIC 3.5 source (`.BAS`, `.SUB`, `.INC`, `.BI`) into DOS MZ executables
(`.EXE`), compiled units (`.PBU`) and unit libraries (`.PBL`) which run
unchanged under DOSBox / real DOS on 16-bit real-mode x86.

PB 3.5 is the reference point rather than the boundary: `--dialect` reaches back
to Turbo Basic 1.0 and across to the Microsoft line (BASICA/GW-BASIC through
QuickBASIC to BASIC PDS 7.1), each verified against its own genuine compiler or
interpreter (M13, M14).

## Stakeholders & context

- Retro-computing developers maintaining PB 3.5 codebases (e.g.
  `Hawkynt/PB-SvgaLibrary`) without access to the proprietary `PBC.EXE`.
- CI pipelines that must compile PB sources on modern hosts and verify the
  binaries under emulation (DOSBox, headless).

## Functional requirements (MoSCoW)

### Must

- **M1** Compile PB 3.5 source to DOS MZ `.EXE` running in real mode on 8086+
  (`$CPU` honored; 386 instructions only when `$CPU 80386`/`-G386`).
- **M2** Full language core: implicit/explicit typing (suffixes `%`, `&`, `!`,
  `#`, `##`, `$` plus PB 3.x `?`, `??`, `???`, `&&`, `@`, `@@`, `$$`;
  `DEFINT`..`DEFEXT` plus `DEFQUD`/`DEFFIX`/`DEFBCD`/`DEFFLX`), all control
  flow (`IF`, `SELECT CASE`, `FOR`, `DO`, `WHILE`, `GOTO`, `GOSUB`,
  `ON x GOTO/GOSUB`, `GOTO/GOSUB/CALL DWORD`), `SUB`/`FUNCTION` with
  `BYVAL`/`BYREF`/`SEG` (incl. argument-position `BYVAL` override),
  `STATIC`/`LOCAL`/`SHARED`/`PUBLIC`/`COMMON`, `TYPE`/`UNION` (incl.
  whole-value comparison), data pointers (`x PTR`, `@p`, `@p[i]`),
  `ASCIIZ * n`, static & `$DYNAMIC` arrays, `DATA`/`READ`/`RESTORE`,
  `DEF FN`.
- **M3** String machinery: dynamic strings with heap, fixed-length strings,
  flex strings, the complete string intrinsic set (`MID$`, `INSTR`, `STR$`,
  `VAL`, `LTRIM$`, …) including statement form `MID$(a$, n) = b$`.
- **M4** Numeric machinery: `INTEGER`, `LONG`, `WORD`/`DWORD`, `QUAD`
  (storage, +, −, ×, compare, PRINT/STR$ — `\`/`MOD`/bitwise deferred),
  `SINGLE`/`DOUBLE`/`EXT` (80-bit) via x87, FIX/BCD storage (arithmetic
  deferred); math intrinsics; `&H`/`&O`/`&B` literals with the verified
  PB 3.1+ signedness rules (bit-length sizing, leading-zero unsigned,
  typed suffixes).
- **M5** Console & file I/O: `PRINT`/`PRINT USING`/`LPRINT`, `INPUT`,
  `LINE INPUT`, `INKEY$`, `OPEN` (SEQUENTIAL/RANDOM/BINARY) with `GET`/`PUT`/
  `SEEK`/`FIELD`/`LSET`/`RSET`/`EOF`/`LOF`/`LOC`, `KILL`, `NAME`, `CHDIR` etc.
  via DOS int 21h.
- **M6** Hardware access: `PEEK`/`POKE`(`$`), `INP`/`OUT`, `VARPTR`/`VARSEG`/
  `CODEPTR`/`STRPTR`, `CALL INTERRUPT`/`REG`, `DEF SEG`, absolute `CALL`.
- **M7** Inline assembler: `!` statement lines with PB variable operand
  resolution (8086–80386 + x87 instruction set).
- **M8** Metastatements: `$INCLUDE`, `$COMPILE EXE|UNIT`, `$LINK PBL|PBU|OBJ`,
  `$CPU`, `$ERROR`, `$DIM`, `$DYNAMIC`/`$STATIC`, `$STACK`, `$STRING`,
  `$OPTIMIZE`, `$DEBUG`, `$FLOAT`, `$LIB`, `$EVENT`, `$IF`/`$ELSE`/`$ENDIF`,
  `$SEGMENT`, `$ALIAS`, `$OPTION`.
- **M9** Units & libraries: `$COMPILE UNIT` → `.PBU`; bundle units → `.PBL`;
  `$LINK` consumes both; exported/imported symbol resolution at link time.
  (Container format is our own documented format — binary compatibility with
  Borland-era `PBC.EXE` units is a non-goal, see W2.)
- **M10** Error handling: `ON ERROR GOTO`, `RESUME [NEXT|label]`, `ERR`/`ERL`/
  `ERROR`, `ERDEV`; runtime errors raise PB-compatible error codes.
- **M11** Verification: every compiled test program runs headless under DOSBox
  and reports via `UNITTEST.LOG` in the `[SUITE]`/`[PASS]`/`[FAIL]`/`[RESULT]`
  format used by PB-SvgaLibrary; harness fails on `[FAIL]`, crash or hang.
- **M12** Compile the PB-SvgaLibrary test battery (the "weirdest features"
  acceptance gate) and run it green under DOSBox.
- **M13** Dialect selection `--dialect` (default `pb35`) with a data-driven gate
  table, across both BASIC families: Turbo Basic / PowerBASIC (`tb10`, `tb11`,
  `pb20`, `pb21`, `pb30`, `pb31`, `pb32`, `pb35`, `pb36`) and Microsoft
  (`basica`, `gw`, `qb10`, `qb20`, `qb30`, `qb40`, `qb45`, `qbasic`, `pds70`,
  `pds71`). Using a newer feature under an older dialect diagnoses "X requires
  PowerBASIC a.b (current dialect: PB c.d)"; using one family's syntax under the
  other diagnoses "X is not available in the ... family". `pb36` is the
  envisioned successor (docs/PB36.md) - a strict superset of pb35 that adds
  language surface and defaults the optimizer on.
- **M14** Differential verification: `scripts/run-diff-tests.sh` compiles each
  battery with both `pbc` and the genuine vintage compiler or interpreter for
  that dialect, then byte-compares `RESULT.TXT`. Every program is additionally
  round-tripped - emitted back to PB 3.5 source, recompiled under `pb35` with the
  optimizer on, and re-run - and must still match the oracle. The proprietary
  oracles ride in the repo only as encrypted archives (`tools/README.md`); the
  batteries skip cleanly without `PB_TOOLCHAIN_KEY`.
- **M15** Foreign object interop: the OMF reader and linker consume genuine
  16-bit C objects and libraries from Borland C++ 3.1, Turbo C 2.0, Watcom
  C/C++ 10.0a and Microsoft C 6.0 (`docs/LINKER.md`, `CInteropTests`).

### Should

- **S1** Graphics/sound statements (`SCREEN`, `PSET`, `LINE`, `CIRCLE`,
  `PAINT`, `PALETTE`, `GET`/`PUT` graphics, `BEEP`, `SOUND`, `PLAY`) — the
  SVGA library mostly brings its own primitives, but tests may use them.
- **S2** `PRINT USING` full format-mask semantics.
- **S3** PBC.EXE-compatible command-line switches (`-FN..`, `-G386`, `-O..`,
  `-CE`, `-E..`, `-L..`) plus modern long options.
- **S4** Event trapping (`ON KEY`, `ON TIMER`, `ON COM`, … with
  `KEY ON/OFF/STOP` family).
- **S5** Chaining & overlays: `CHAIN`, `RUN`, `SHELL`, `ENVIRON`.

### Could

- **C1** `$COM` serial I/O statements (`OPEN "COM1:…"`).
- **C2** Listing/map file output, `$DEBUG` symbolic info.
- **C3** ~~Optimizations: constant folding, dead-code elimination, peephole.~~
  Delivered and long since overtaken: an SSA mid-end (`Ir/Passes/`) feeding the
  C and LLVM back ends, plus emitter, peephole, scheduler and layout tiers. The
  catalogue is `docs/optimizations/` - one document per optimization, with the
  stage each is implemented at and whether it has moved to the IR.

### Won't (this project)

- **W1** Protected-mode / DPMI output, Windows PB/CC or PB/Win dialects.
- **W2** Bit-for-bit compatibility with proprietary `.PBU`/`.PBL`/`PBC.EXE`
  binary formats (we define our own, documented in `docs/FORMATS.md`).
- **W3** A floating-point software emulation library — x87 presence is
  assumed (DOSBox always emulates one).

## Non-functional requirements

- **N1** Host: .NET 8, C# preview language features, Backports package;
  runs on Windows/Linux/macOS.
- **N2** TDD/BDD: unit tests (NUnit, Given-When-Then naming) per compiler
  stage; equivalence classes, boundary values, error paths.
- **N3** Deterministic output: identical input → byte-identical artifacts.
- **N4** Compilation speed: full SVGA battery compile < 60 s on CI hardware
  (advisory, `Performance` category).
- **N5** House standard: Hawkynt repo template (badges, CI quartet,
  changelog contract, commit prefixes).
