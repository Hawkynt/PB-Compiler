# O0007 — Loop unrolling

| | |
|---|---|
| **Status** | ✅ Implemented (constant-trip INTEGER `FOR`, at most 4 iterations, straight-line body) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region O7`, `TryEmitUnrolledFor`, `CountUnrollableStatements` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **IR** | ✅ `Ir/Passes/LoopUnroll.cs` — full unroll of a constant-trip counted loop, in `IrPassManager.Standard()`; **nested** loops unroll inner-out, each inner copy leaving the outer body straight-line for the next fixpoint sweep; verified by `IrPassObservableEquivalenceTests` (render to BASIC, run, compare) and `RemovedBlockUseListTests` |
| **Verified by** | `tests/diff/DIFF26.BAS` |
| **Related** | [O0020](O0020-idiom-replacement.md), [O0063](O0063-duff-unrolling.md), [O0066](O0066-unrolled-counter-propagation.md) |

## What it is

A `FOR` loop whose bounds and step are compile-time constants, whose trip count
is small, and whose body is a short run of straight-line statements is emitted
as N copies of the body — no counter compare, no back-edge, no branch
misprediction, and the loop-control instructions vanish entirely.

The counter cell is left on the **increment-then-test final value** (QUIRK 2.28,
16-bit wrap included), exactly as the rolled loop would have left it.

## Sample

```basic
$OPTIMIZE SPEED
DIM i%, t%
FOR i% = 1 TO 3
  t% = t% + i%
NEXT
PRINT i%; t%
```

## Without the optimizer

```asm
    mov     ax, 0001h
    mov     [i], ax
Top:
    mov     ax, [i]
    cmp     ax, 0003h
    jg      Done
    mov     ax, [t]
    add     ax, [i]
    mov     [t], ax
    mov     ax, [i]
    inc     ax
    mov     [i], ax
    jmp     Top
Done:
```

## With the optimizer

```asm
    mov     ax, 0001h
    mov     [i], ax
    mov     ax, [t]          ; iteration 1
    add     ax, [i]
    mov     [t], ax
    mov     ax, 0002h
    mov     [i], ax
    mov     ax, [t]          ; iteration 2
    add     ax, [i]
    mov     [t], ax
    mov     ax, 0003h
    mov     [i], ax
    mov     ax, [t]          ; iteration 3
    add     ax, [i]
    mov     [t], ax
    mov     ax, 0004h        ; the counter's end value
    mov     [i], ax
```

## Equivalent BASIC

```basic
DIM i%, t%
i% = 1 : t% = t% + i%
i% = 2 : t% = t% + i%
i% = 3 : t% = t% + i%
i% = 4
PRINT i%; t%
```

## Why it is safe

On the emitter path the body must contain no jumps, exits, nested loops or writes
to the counter, so
each copy is a faithful iteration; the trip count is simulated exactly like the
generic loop engine (signed compare, 16-bit wrap on increment), and the final
counter value is stored explicitly. `$OPTIMIZE SPEED` gating matters because
DOS-era code uses tiny loops as **delay loops**.

## Limits

- Each unrolled copy still reads the counter cell rather than seeing its value
  as a literal, so `a%(i%) = i% * i%` keeps a multiply and an address
  computation per copy. Fixing that needs a per-iteration constant override in
  the folder — [O0066](O0066-unrolled-counter-propagation.md).
- Variable-trip loops are not unrolled at all; that is
  [O0063](O0063-duff-unrolling.md).
