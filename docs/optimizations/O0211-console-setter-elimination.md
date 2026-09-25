# O0211 — Redundant console-setter elimination

| | |
|---|---|
| **Status** | ⬜ Not implemented on the IR path |
| **Stage** | — |
| **Source** | None on the compiled path. `CodeGen/OptPruner.cs` still holds the syntax-level version, which only `--emit-basic` runs |
| **Gate** | `--optimize` |
| **Split from** | [O0010](O0010-redundant-statement-elimination.md) (which is now `DEF SEG` coalescing) |

## What it is

A console-state statement that sets the value already in effect changes nothing
observable and is dropped — the same argument as `DEF SEG` coalescing, applied
to the console subsystem's setters.

Not implemented on the IR path: the setters lower to runtime calls (`rt_color`,
`rt_locate`, ...), and nothing yet knows which of them set state the next one
replaces. A setter can also raise an error (an out-of-range `LOCATE` is Error 5),
so dropping one needs its arguments proven in range, not just repeated.

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
