# PB-Compiler

A PowerBASIC 3.5 (DOS) compatible compiler written in C#, targeting 16-bit
real-mode DOS.

`pbc` compiles unmodified PowerBASIC 3.5 source into:

- **`.EXE`** — DOS MZ executables that run on real DOS / DOSBox,
- **`.PBU`** — compiled units (`$COMPILE UNIT`),
- **`.PBL`** — unit libraries (linkable via `$LINK`).

## Why

PowerBASIC 3.5 for DOS is proprietary, 16-bit and long out of print — it
cannot run on modern 64-bit hosts. This project provides a clean-room,
cross-platform reimplementation of the compiler so PB 3.5 codebases (such as
[PB-SvgaLibrary](https://github.com/Hawkynt/PB-SvgaLibrary)) can be built and
verified on modern machines and CI, while the produced binaries still run on
the original target: 8086+ real mode under DOS or DOSBox.

## Usage

```bash
pbc HELLO.BAS              # -> HELLO.EXE (DOS MZ, real mode)
pbc -G386 TEST.BAS         # allow 80386 instructions ($CPU 80386)
pbc --dialect pb31 OLD.BAS # gate features per dialect (pb20|pb21|pb30|pb31|pb32|pb35)
pbc UNIT.BAS               # $COMPILE UNIT inside -> UNIT.PBU
pbc MAIN.BAS               # $LINK "UNIT.PBU" / $LINK "MY.PBL" inside -> linked EXE
pbc lib build MY.PBL *.PBU # bundle units into a library
pbc lib list MY.PBL        # show exports/imports of a library or unit
```

Run the result in DOSBox:

```bash
dosbox -c "mount c ." -c "c:" -c "HELLO.EXE"
```

## Status

Under construction — see [REQUIREMENTS.md](REQUIREMENTS.md) for the MoSCoW
breakdown and [CHANGELOG.md](CHANGELOG.md) for progress.

| Stage | State |
|-------|-------|
| Lexer + preprocessor ($INCLUDE, $IF/$ELSEIF) | ✅ full PB 3.5 token set incl. `?`/`??`/`???`/`&&`/`@`/`@@`/`$$` suffixes, PB 3.1+ radix rules and the `&` concat operator, corpus-validated |
| Dialect gating (`--dialect pb20..pb35`) | ✅ data-driven gate table (`Syntax/Dialect.cs`): inline asm/unsigned/QUAD (3.0), typed radix/ALIAS/ANY/UDT compare (3.1), pointers/code pointers/underscores (3.2), ASCIIZ/`&`/$ELSEIF/TRIM$/… (3.5); old dialects also re-enable that version's documented bugs (signed radix pre-3.1, equate folding bug 3.0–3.2, 16-bit HEX$ pre-3.1 — [docs/QUIRKS.md](docs/QUIRKS.md)) |
| Parser | ✅ full grammar; the whole [PB-SvgaLibrary](https://github.com/Hawkynt/PB-SvgaLibrary) corpus parses (27&nbsp;772 statements) |
| Semantic analysis | ✅ all 31 corpus suites bind error-free |
| 8086–386 + x87 assembler, MZ writer | ✅ 680 golden-byte tests |
| Code generator + DOS runtime | ✅ integers/longs/floats (WORD→LONG arithmetic promotion, unsigned compares), control flow (FOR with LONG/float counters and variable STEP, SELECT on longs/strings), dynamic strings, SUB/FUNCTION frames, static & REDIM arrays (LIFO heap reclaim), array/ANY parameters, UDTs, sequential + RANDOM/BINARY file I/O (GET/PUT, GET$/PUT$, SEEK, LOF), DATA/READ/RESTORE, ON ERROR/RESUME/ERR, console INPUT/LINE INPUT/INKEY$, PRINT USING (literal formats), DEF SEG/PEEK/POKE/INP/OUT/WAIT, VARPTR/STRPTR families, REG + CALL INTERRUPT, CODEPTR32 far thunks + CALL/GOTO/GOSUB DWORD, data pointers (`x PTR`, `@p`, `@p[i]`, VARPTR32/STRPTR32), ASCIIZ buffers, QUAD storage/+/−/×/compare/print, TYPE/UNION whole-value compare, TRIM$, RND(a,z), CVx offsets, ASC statement, SIZEOF, ERRCLEAR, STDIN/STDOUT, SETEOF, CONSIN/CONSOUT, SHIFT/ROTATE/BIT, SWAP, SCREEN/CLS/LOCATE/BEEP/SOUND, RND/TIMER |
| Inline assembler | ✅ wired into codegen: the whole corpus (5&nbsp;100+ `!` statements) assembles; locals/params resolve to BP cells (BYREF = pointer slot), BASIC labels are jump targets |
| Corpus run gate | ✅ all 31 PB-SvgaLibrary suites compile **and run** under DOSBox: 1&nbsp;139 assertions, 0 failures |
| PBU/PBL units & linker | ✅ `$COMPILE UNIT` emits .PBU (exports with signature hashes, runtime/DECLARE imports, near/data/segment/import fixups); `$LINK "X.PBU"/"Y.PBL"` resolves DECLAREd procedures at compile time (libraries on demand, transitively), signature mismatches are compile errors; cross-unit numeric/string/BYREF calls verified under DOSBox |
| DOSBox harness + CI | ✅ golden battery (incl. stdin-redirected INPUT tests) + execution tests, headless |
| Differential harness vs. genuine PBC 3.50 | ✅ `scripts/run-diff-tests.sh`: 21 batteries (numerics, radix rules, suffixes, concat, QUAD incl. \/MOD/bitwise/SHIFT/ROTATE, ASCIIZ, 3.5 surface, pointers, UDT compare/$ELSEIF, code pointers, SETEOF/ERRCLEAR/BYVAL, vendor string surface, ARRAY SORT/SCAN + LSET/RSET + USING$, FIX/BCD arithmetic, HUGE/VIRTUAL/ABSOLUTE/REDIM PRESERVE, $ERROR defaults + traps + $OPTIMIZE SPEED, FIELD/ERL/$STRING/string-manager ABI, CHAIN with COMMON) byte-identical to the original compiler; per-dialect oracle batteries (`tests/diff/pb30/` + `tools/pb30/PBC.EXE`, …) activate automatically when the genuine binary is provided |
| Vendor example corpus (PB 3.5 ships) | ✅ `scripts/run-vendor-corpus.sh`: 37/40 of `tools/pb35/EXAMPLE/*.BAS` compile (incl. rebuilding PB35.PBL from the unit sources with our own toolchain); the 3 remaining need TSR popups (ASCIITSR) or CGA/EGA raster graphics (BALL, EGABALL) |
| Memory models & error checking | ✅ HUGE (DOS 48h, segment-stepping), VIRTUAL (EMS int 67h, FRE(-11)), ABSOLUTE (`AT seg`), REDIM PRESERVE; $ERROR BOUNDS/NUMERIC/OVERFLOW/STACK (+ -EB/-EN/-EO/-ES) raising errors 9/6/6/201; $OPTION SIGNED/CNTLBREAK/GOSUB; $OPTIMIZE SIZE\|SPEED (-OZF); $STRING limits (error 15); CHAIN/RUN/$COMPILE CHAIN with COMMON handoff; SHELL/EXECUTE via DOS EXEC |

## Layout

| Path | What |
|------|------|
| `PowerBasic.Compiler/` | Compiler library: lexer, parser, semantics, 8086 assembler, code generator, MZ/PBU/PBL emitters, DOS runtime |
| `pbc/` | Command-line front end |
| `PowerBasic.Compiler.Tests/` | NUnit test suite (TDD, Given-When-Then) |
| `tests/` | PowerBASIC test battery executed under DOSBox |
| `scripts/` | DOSBox integration harness |

## License

[LGPL-3.0-or-later](LICENSE)
