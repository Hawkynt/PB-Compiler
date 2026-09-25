# O0005 — Register residency (8086 tier)

| | |
|---|---|
| **Status** | ✅ Implemented — the 8086 tier is complete; the multi-register 386 tier is [O0058](O0058-386-register-allocation.md) |
| **Stage** | IR middle end (`Mem2Reg`) + x86 back end (register allocation) |
| **Source** | `Ir/Passes/Mem2Reg.cs`; `Backend/LinearScanAllocator.cs` — `TryCoalesced` (`_resident` preference); `Backend/LivenessAnalysis.cs` — `LoopCarried` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `BackendResidencyTests`, `tests/diff/DIFF52.BAS`, `DIFF90.BAS` (nested), `DIFF91.BAS` (conditional body), `DIFF96.BAS` (DO loop), scenarios `HotAccumulatorWinsTheRegister`, `AccumulateOverArrayIsHandQuality` |
| **Related** | [O0030](O0030-induction-variable-strength-reduction.md), [O0072](O0072-register-reassignment.md) |
| **Split into** | [O0194](O0194-accumulator-residency.md), [O0195](O0195-nested-counter-residency.md), [O0196](O0196-do-loop-residency.md), [O0197](O0197-dual-accumulators.md), [O0198](O0198-resident-read-modify-write.md), [O0199](O0199-branch-tolerant-residency.md) |

## What it is

Locals normally live in stack cells and are loaded and stored per use.
`Mem2Reg` promotes every scalar whose address is not taken into SSA values, and
the linear-scan allocator gives those values registers. Under
`$OPTIMIZE SPEED` a value that is live all the way round a loop
(`LivenessAnalysis.LoopCarried`) is offered **SI** and **DI** first: they are
the two registers no fixed-register sequence (multiply, divide, variable shift,
runtime call arguments) claims, so a loop-carried value parked there is the
least likely to be displaced. It is a preference, not a reservation: a value the
pair cannot take falls back to the ordinary pool order.

**This page covers the `FOR` counter in SI.** The
other residency shapes — the DI accumulator, nested counters, `DO`-loop and dual
accumulators, resident read-modify-write, and residency across a conditional —
each have their own entry (see *Split into* above), and the element pointer in
BX belongs to
[O0030](O0030-induction-variable-strength-reduction.md).

A post-loop read of the counter reads the SSA value the loop exit carries, so
it sees the increment-then-test end value PB guarantees (QUIRK 2.28); a variable
that stays in memory is stored wherever the program writes it.

## Sample

```basic
DIM i%, s%
FOR i% = 1 TO 100
  s% = s% + i%
NEXT
PRINT s%
```

## Without the optimizer

Both variables round-trip through memory every iteration:

```asm
Top:
    mov     ax, [i]
    cmp     ax, 0064h
    jg      Done
    mov     ax, [s]
    add     ax, [i]
    mov     [s], ax
    mov     ax, [i]
    inc     ax
    mov     [i], ax
    jmp     Top
Done:
```

## With the optimizer

```asm
    mov     si, 0001h        ; counter resident
    xor     di, di           ; accumulator resident
Top:
    cmp     si, 0064h
    jg      Done
    add     di, si
    inc     si
    jmp     Top
Done:
    mov     [i], si          ; flushed on exit, holds the end value
    mov     [s], di
```

## Equivalent BASIC

There is no BASIC spelling for "keep this in a register" — the observable
program is unchanged:

```basic
DIM i%, s%
FOR i% = 1 TO 100 : s% = s% + i% : NEXT
PRINT s%
```

## Why it is safe

Only a variable whose every use is a direct load or store is promoted, so
nothing can observe its memory cell while a register holds the value — a
variable whose address is taken (BYREF argument, `VARPTR`, inline asm) stays in
memory. The allocator never assigns a register across an instruction that
clobbers it (a call, a fixed-register sequence, an inline asm block's hold); a
value live across one is split or spilled instead. The residency preference
only changes which register an interval gets, never whether the function
allocates.

## Limits

- Straight-line residency cannot pay on 8086 — a single use costs one cell
  access either way.
- Cross-call residency is ABI-impossible on 8086.
- The SI/DI preference covers two loop-carried values; any further ones take
  whatever the ordinary pool order leaves free. Residency for LONG values in
  single 32-bit registers is the 386 tier
  ([O0058](O0058-386-register-allocation.md)).
