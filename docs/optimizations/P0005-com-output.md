# P0005 — `.COM`-style output

| | |
|---|---|
| **Status** | ✅ Implemented — `$COMPILE COM` and `--emit-com` emit a relocation-checked PSP:0100h flat image |
| **Stage** | Image writer |
| **Source** | `CodeGen/CodeGenerator.Trivial.cs`, `Emit/MzExeWriter.cs` |
| **Gate** | `--optimize` |
| **Related** | [P0006](P0006-header-squeeze.md), [P0007](P0007-trivial-io-lowering.md), [P0004](P0004-right-sized-memory.md) |

## What it is

The tiny memory model: `CS = DS = SS`, origin 100h, no MZ header and no
relocations. DOS and DOSBox load a **signature-less** file as a `.COM` image
regardless of its extension, so a program with no relocations needs no header at
all.

The general form is now first-class: `$COMPILE COM` (or `--emit-com`) runs the
same mandatory Bound AST → IR → SSA middle-end → Low IR → x86-16 machine pipeline
as EXE output. The assembler is given a synthetic 0100h origin prefix, so labels,
jump tables, virtual BSS addresses and every ordinary fixup are resolved at the
actual DOS COM addresses from the start; the writer then strips that prefix.
PC-relative branches/calls remain naturally position-independent.

COM deliberately has no fallback representation. A load-time segment relocation,
unresolved external symbol, linked unit/library, or image/BSS footprint that would
cross `FFFFh` is diagnosed and the user must emit EXE instead.

## Sample

```basic
PRINT "Hello, World!"
```

## Without the optimizer

```
MZ header      : 32 bytes  + relocation table
Load image     : 14 222 bytes
Total on disk  : 14 254 bytes
```

## With the optimizer

```
Raw image      : 25 bytes, no header, no relocations
```

```asm
    org     100h
    mov     dx, msg
    mov     ah, 9
    int     21h
    int     20h
msg db "Hello, World!", 0Dh, 0Ah, "$"
```

## Equivalent BASIC

Unchanged — the same output, from a file a fifth of a kilobyte smaller than the
header alone used to imply.

## Why it is safe

The tiny model is only chosen when the image genuinely needs **no relocations**
(nothing references a segment that the loader must fix up) and fits one
segment. Anything else keeps the MZ path.

## Explicit-switch contract

- DOS loads the file at PSP:0100h with CS=DS=ES=SS; the existing startup saves
  the PSP segment and initializes the runtime from CS exactly as on EXE.
- The assembler resolves internal absolute offsets against an origin of 0100h;
  relative transfers need no special patch.
- Segment and unresolved-external relocations are rejected because COM has no
  relocation table.
- The file image plus virtual BSS must fit in the remaining 0xFF00 bytes of the
  segment. `$STACK` continues to use the same top-of-segment stack contract.
