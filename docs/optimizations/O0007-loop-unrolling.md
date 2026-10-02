# O0007 — Loop unrolling

| | |
|---|---|
| **Status** | ✅ Implemented (constant-trip counted loop, at most 16 iterations and 192 instructions unrolled, straight-line body) |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/LoopUnroll.cs` — `Run`, `Match`, `TripCount`, `TryUnroll`; in `IrMiddleEndPipeline.Standard()` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF26.BAS`, `IrPassObservableEquivalenceTests` (render to BASIC, run, compare), `RemovedBlockUseListTests` |
| **Related** | [O0020](O0020-idiom-replacement.md), [O0063](O0063-duff-unrolling.md), [O0066](O0066-unrolled-counter-propagation.md) |

## What it is

A counted loop whose start, step and limit are compile-time constants, whose
trip count is small, and whose body is a straight-line block chain is replaced
by N copies of the body — no counter compare, no back-edge, and the
loop-control instructions vanish entirely. Each copy is cloned with every header
phi mapped to its value at that iteration, and the counter phi is seeded with
the literal for that copy ([O0066](O0066-unrolled-counter-propagation.md)).
**Nested** loops unroll inner-out, each inner unroll leaving the outer body
straight-line for the next sweep.

Uses after the loop are rewritten to each phi's value after the last
iteration, so the counter holds the **increment-then-test final value**
(QUIRK 2.28), exactly as the rolled loop would have left it.

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

The listing shows the unrolled shape only. Because each copy sees its counter
as a literal, the later folding passes usually reduce a body like this one
further, down to the constant results.

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

The pass accepts only a header holding phis, one compare and the conditional
branch, followed by a straight-line body/latch chain; anything else declines.
`TripCount` simulates the loop test exactly and declines when the counter would
leave the 16-bit range, so no copy count is ever based on a wrapped counter. A
function with an armed error handler is never unrolled. Full unrolling runs
under `--optimize` at every objective; only the runtime unroll of
[O0063](O0063-duff-unrolling.md) is limited to `$OPTIMIZE SPEED`.

## Limits

- More than 16 iterations, or an unrolled result above 192 instructions,
  keeps the loop.
- Variable-trip loops are not fully unrolled; that is
  [O0063](O0063-duff-unrolling.md).
