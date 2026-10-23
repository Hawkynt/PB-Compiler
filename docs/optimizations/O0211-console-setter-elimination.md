# O0211 — Redundant console-setter elimination

| | |
|---|---|
| **Status** | 🟡 Partial — full clears and covered constant cursor positions coalesce |
| **Stage** | IR late scalar cleanup |
| **Source** | `Ir/Passes/ConsoleSetterCoalescing.cs` through `IrMiddleEndPipeline.Standard` |
| **Gate** | `--optimize` |
| **Split from** | [O0010](O0010-redundant-statement-elimination.md) (which is now `DEF SEG` coalescing) |

## What it is

A console-state statement that sets the value already in effect changes nothing
observable and is dropped — the same argument as `DEF SEG` coalescing, applied
to the console subsystem's setters.

The IR pass removes an earlier argumentless `CLS` before a second full clear,
and cursor positions overwritten by a clear or a covering `LOCATE`. Constant
coordinates must be in the conservative common range (rows 0–25, columns 0–40);
zero means an omitted coordinate, not a write. Dynamic or invalid positions are
barriers. `COLOR` coalescing is not implemented.

Only straight-line, non-observing operations may intervene. Calls, loads/stores,
inline assembly, potentially trapping arithmetic, and block boundaries end the
window. Argument side effects are never deleted with a setter.

## Sample

```basic
CLS
LOCATE 3, 2
CLS
PRINT "home"
```

## With the optimizer

Only the final `CLS` and `PRINT` remain. Execution tests compare stdout, the text
page and the cursor; IR tests also count the removed operations and verify every
pass boundary.

## Why it is safe

The value must be provably unchanged in between: anything that could alter the
console state — a call, inline asm, an interrupt, direct video access, or any
control flow that could arrive with a different state — ends the window, exactly
as it does for [O0010](O0010-redundant-statement-elimination.md).

`SCREEN` is not an unconditional clear. Genuine QuickBASIC 4.5 under DOSBox
Staging leaves `POS(0)=2`, `CSRLIN=3` after `LOCATE 3,2:SCREEN 0` when text mode
is already active. The same VGA probe raises Error 5 for `SCREEN 4` and for a
following `PSET (1,1)` in text mode. Eliminating those calls would erase errors.
Mode and drawing elimination therefore require additional mode/capability and
error-flow proofs; this pass keeps them.
