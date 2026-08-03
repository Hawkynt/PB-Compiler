# P0007 — Trivial-I/O lowering

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Whole-program shape recognition, before runtime selection |
| **Source** | `CodeGen/CodeGenerator.Trivial.cs` |
| **Gate** | `--optimize` |
| **Verified by** | execution in DOSBox (25-byte hello world) |
| **Related** | [P0001](P0001-runtime-trimming.md), [P0005](P0005-com-output.md), [R0001](R0001-fast-text-output.md) |

## What it is

Some programs have no run-time behavior at all — their entire output is known at
compile time. A program whose only effects are `PRINT`ing compile-time strings
and integrals, and `END`, lowers to a raw COM-style image: the whole output text
— including PB number formatting, 14-column comma zones and CRLFs — is
precomputed and written with **one** DOS call.

`AH=9` when the text contains no `$`, otherwise `AH=40h`; exit via `INT 20h` or
`AH=4Ch`.

## Sample

```basic
PRINT "Hello, World!"
```

## Without the optimizer

14 254 bytes: the full runtime, the string console path, column tracking, the
capture buffer, the formatter.

## With the optimizer

**25 bytes**, verified running in DOSBox:

```asm
    org     100h
    mov     dx, msg
    mov     ah, 9
    int     21h
    int     20h
msg db "Hello, World!", 0Dh, 0Ah, "$"
```

## Equivalent BASIC

Conceptually the program has been constant-folded end to end:

```basic
' the entire observable behavior is one write of a known byte string
```

## Why it is safe

The recognition is a **whole-program** shape test and falls back to the generic
runtime the moment anything can be observed differently: column state carried
across statements, `PRINT` zones with runtime operands, `USING`, a non-literal
operand, file or `STDOUT` redirection, an error handler, or any statement with
another effect. Where it does apply, the emitted bytes are exactly the bytes the
generic path would have written, computed at compile time instead of at run
time.
