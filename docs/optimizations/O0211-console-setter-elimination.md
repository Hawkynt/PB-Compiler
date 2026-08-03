# O0211 — Redundant console-setter elimination

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Pre-emission pruner |
| **Source** | `CodeGen/OptPruner.cs` |
| **Gate** | `--optimize` |
| **Split from** | [O0010](O0010-redundant-statement-elimination.md) (which is now `DEF SEG` coalescing) |

## What it is

A console-state statement that sets the value already in effect changes nothing
observable and is dropped — the same argument as `DEF SEG` coalescing, applied
to the console subsystem's setters.

## Sample

```basic
COLOR 7, 0
PRINT "a"
COLOR 7, 0                   ' already in effect
PRINT "b"
```

## With the optimizer

The second setter is not emitted; the two `PRINT`s run with the same attributes
they would have had.

## Why it is safe

The value must be provably unchanged in between: anything that could alter the
console state — a call, inline asm, an interrupt, direct video access, or any
control flow that could arrive with a different state — ends the window, exactly
as it does for [O0010](O0010-redundant-statement-elimination.md).
