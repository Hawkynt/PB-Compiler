# O0005 — Register residency (8086 tier)

| | |
|---|---|
| **Status** | ✅ Implemented — the 8086 tier is complete; the multi-register 386 tier is [O0058](O0058-386-register-allocation.md) |
| **Stage** | Emitter (per loop region) |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region O5`, `TryEmitForCounterInRegister`, `TryEmitNestedForCounterInRegister`, `TryEmitDoLoopInRegister`, `FindAccumulator` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `tests/diff/DIFF52.BAS`, `DIFF90.BAS` (nested), `DIFF91.BAS` (conditional body), `DIFF96.BAS` (DO loop), scenarios `HotAccumulatorWinsTheRegister`, `AccumulateOverArrayIsHandQuality` |
| **Related** | [O0030](O0030-induction-variable-strength-reduction.md), [O0072](O0072-register-reassignment.md) |
| **Split into** | [O0194](O0194-accumulator-residency.md), [O0195](O0195-nested-counter-residency.md), [O0196](O0196-do-loop-residency.md), [O0197](O0197-dual-accumulators.md), [O0198](O0198-resident-read-modify-write.md), [O0199](O0199-branch-tolerant-residency.md) |

## What it is

Locals normally live in stack cells and are loaded and stored per use. Over a
loop region, where the cost repeats every iteration, the hottest values move
into the only two callee-stable general registers the 8086 has: **SI** and
**DI**.

**This page covers the `FOR` counter in SI** when the body is SI-clean. The
other residency shapes — the DI accumulator, nested counters, `DO`-loop and dual
accumulators, resident read-modify-write, and residency across a conditional —
each have their own entry (see *Split into* above), and the element pointer in
BX belongs to
[O0030](O0030-induction-variable-strength-reduction.md).

The counter is flushed to its cell on loop exit, so a post-loop read sees the
increment-then-test end value PB guarantees (QUIRK 2.28).

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

Residency is granted only for a region the emitter has proven **clean** for the
register in question: no call (a callee owns SI/DI as scratch), no inline asm,
no `GOSUB`, no address-taking, nothing that could observe the variable's memory
cell while the register holds the live value. Every exit path flushes.

## Limits

- Straight-line residency cannot pay on 8086 — a single use costs one cell
  access either way.
- Cross-call residency is ABI-impossible on 8086.
- More than two simultaneous residents, and residency for LONG values, is the
  386 tier ([O0058](O0058-386-register-allocation.md)), which needs the
  `DX:AX → EAX` representation change from [C0001](C0001-386-codegen.md).
