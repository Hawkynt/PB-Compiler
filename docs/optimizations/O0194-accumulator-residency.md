# O0194 — Hot accumulator in DI

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end + x86 back end (register allocation) |
| **Source** | `Ir/Passes/Mem2Reg.cs` (the variable becomes SSA values); `Backend/LinearScanAllocator.cs` — `TryCoalesced` (under SPEED, SI/DI are offered first to what `LivenessAnalysis.LoopCarried` reports); `Backend/CopyCoalescer.cs` |
| **Gate** | `--optimize`; the SI/DI preference needs `$OPTIMIZE SPEED` |
| **Verified by** | `tests/diff/DIFF52.BAS`, scenario `HotAccumulatorWinsTheRegister`, `BackendResidencyTests` |
| **Split from** | [O0005](O0005-register-residency.md) (which is now the FOR counter in SI) |

## What it is

A hot INTEGER accumulator lives in a register across the loop, so its
per-iteration load and store disappear.

There is no accumulator-specific rule any more. `Mem2Reg` turns the variable
into SSA values joined by a loop-header phi, and the linear-scan allocator gives
that value a register for the whole loop. Under `$OPTIMIZE SPEED` the allocator
offers **SI** and **DI** first to the values live all the way round a loop — the
self-referential accumulator (`acc = acc OP …`, `INCR`, `DECR`) and the counter —
because the fixed-register sequences (multiply, divide, variable shift, dispatch)
never claim them. A scratch that is recomputed and dies each pass is not loop-carried
and does not get the preference.

## Sample

```basic
$OPTIMIZE SPEED
DIM i%, s%, scratch%
FOR i% = 1 TO 100
  scratch% = i% AND 7        ' throwaway
  s% = s% + scratch%         ' carried: this one wins DI
NEXT
```

## With the optimizer

```asm
Top:
    ...
    add     di, [bp-scratch] ; the scratch stays in memory, read as an operand
```

Parking the scratch instead would leave the hot value loading and storing every
iteration — which is what the preference rule prevents. With enough free
registers the scratch is kept in a register as well (`add si, ax`).

## Why it is safe

Only a variable whose uses are all direct loads and stores is promoted, so no
other code can observe a cell while the register holds the value; anything else
stays in memory. Calls and inline asm declare the registers they clobber, and the
allocator keeps a value across them only in a register they leave alone. The SI/DI
choice is a preference, never a constraint: when it cannot be met, the ordinary
allocation is used.
