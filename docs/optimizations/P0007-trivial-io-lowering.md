# P0007 — Trivial-I/O lowering

| | |
|---|---|
| **Status** | ⬜ Planned; the former raw-byte artifact shortcut was retired |
| **Stage** | Whole-program IR plus target machine lowering |
| **Source** | — |
| **Gate** | `--optimize` |
| **Verified by** | not implemented |
| **Related** | [P0001](P0001-runtime-trimming.md), [P0005](P0005-com-output.md), [R0001](R0001-fast-text-output.md) |

## What it should be

When a program's complete observable behavior is writing compile-time text and exiting,
the middle end can combine those effects into one target-independent operation. The
x86-16 backend may then select a compact DOS write and exit sequence through the normal
machine pipeline.

The whole output includes PowerBASIC number formatting, fourteen-column comma zones and
CRLFs. A correct implementation must stop specializing as soon as column state, `USING`,
redirection, error handling or a dynamic operand can change the result.

## Why the former implementation was retired

`Emit/DosTrivialImage.cs` inspected optimized IR and returned hand-authored x86 bytes
directly from DOS artifact construction. Selection and allocation had already been
probed for routing, but their machine product was never emitted. That made the artifact
an alternate code generator and invalidated the invariant that every production body
passes through Low IR, instruction selection, scheduling, allocation and
`X86ProductionEmitter`.

P0007 can return only as an IR or machine transformation. COM selection remains P0005;
it changes the container, not the compiler path.

## Sample

```basic
PRINT "Hello, World!"
```

## Equivalent BASIC

Conceptually the program has been constant-folded end to end:

```basic
' one write of a known byte string, followed by the same exit
```
