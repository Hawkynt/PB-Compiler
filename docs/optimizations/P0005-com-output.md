# P0005 — `.COM`-style output

| | |
|---|---|
| **Status** | ✅ Implemented via [P0007](P0007-trivial-io-lowering.md) — `$COMPILE COM` as an explicit switch is ⬜ planned |
| **Stage** | Image writer |
| **Source** | `CodeGen/CodeGenerator.Trivial.cs`, `Emit/MzExeWriter.cs` |
| **Gate** | `--optimize` |
| **Related** | [P0006](P0006-header-squeeze.md), [P0007](P0007-trivial-io-lowering.md), [P0004](P0004-right-sized-memory.md) |

## What it is

The tiny memory model: `CS = DS = SS`, origin 100h, no MZ header and no
relocations. DOS and DOSBox load a **signature-less** file as a `.COM` image
regardless of its extension, so a program with no relocations needs no header at
all.

Today this is reached through [P0007](P0007-trivial-io-lowering.md): a program
whose whole observable behavior is compile-time output lowers to a raw
COM-style image. The general form — a `$COMPILE COM` metastatement that puts an
*arbitrary* single-segment program into the tiny model — is still planned.

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

## What the explicit switch still needs

- `$COMPILE COM` parsing and a diagnostic when the program cannot fit the model
  (multiple segments, far pointers, > 64 KiB, relocations);
- stack setup inside the single segment and a `$STACK` interaction;
- the runtime's segment assumptions (string/array heaps) reduced to the tiny
  model, which is really [P0004](P0004-right-sized-memory.md)'s job.
