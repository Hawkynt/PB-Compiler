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
pbc UNIT.BAS               # $COMPILE UNIT inside -> UNIT.PBU
pbc lib build MY.PBL *.PBU # bundle units into a library
```

Run the result in DOSBox:

```bash
dosbox -c "mount c ." -c "c:" -c "HELLO.EXE"
```

## Status

Under construction — see [REQUIREMENTS.md](REQUIREMENTS.md) for the MoSCoW
breakdown and [CHANGELOG.md](CHANGELOG.md) for progress.

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
