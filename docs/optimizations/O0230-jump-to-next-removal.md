# O0230 — Jump-to-next removal

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Assembler |
| **Source** | `Asm/Assembler.Peephole.cs` |
| **Gate** | `--optimize` |
| **Split from** | [O0035](O0035-jump-relaxation.md) (which is now short-form relaxation) |

## What it is

A `JMP` whose target is the immediately following instruction does nothing. It
is removed outright.

The shape is not hypothetical: it is the arm-closing jump of every `IF` with no
`ELSE`, which the emitter writes before it knows what follows.

## Sample

```basic
DIM x%
IF x% = 0 THEN PRINT "zero"
PRINT "done"
```

## Without / with

```asm
    ...                      ; without
    jmp     EndIf            ; to the very next instruction
EndIf:
    ...

    ...                      ; with: the jump is gone, EndIf falls through
EndIf:
    ...
```

## Why it is safe

Control reaches the same instruction either way; the label stays bound at the
same position, so every other reference to it is unaffected. On an 8086 the
saving is not only two or three bytes but a **taken transfer**, which flushes the
prefetch queue.
