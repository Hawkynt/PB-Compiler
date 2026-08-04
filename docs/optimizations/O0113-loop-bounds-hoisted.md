# O0113 — Loop bounds loaded once

| | |
|---|---|
| **Status** | 🟡 Partial (a constant limit folds into the compare as an immediate on both the SI-resident and the memory-counter path; a variable limit still reloads its cell) |
| **Stage** | Emitter |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0005](O0005-register-residency.md), [O0112](O0112-countdown-loop.md), [O0131](O0131-exact-trip-count.md) |

## The idea

A `FOR` loop's limit and step are evaluated once by definition — PB evaluates
them at loop entry — but the *values* are kept in frame cells and reloaded for
the compare on every iteration. Holding them in a register (or folding the limit
into the compare as an immediate when it is constant) removes a memory access
per iteration.

## Applies to

```basic
DIM i%, n%, s%
n% = 1000
FOR i% = 1 TO n%
  s% = s% + i%
NEXT
```

## Today

```asm
Top:
    mov     ax, si
    cmp     ax, [bp-limit]   ; reloaded every iteration
    jg      Done
```

## Planned

```asm
    mov     bx, [bp-limit]   ; once, in the preheader
Top:
    cmp     si, bx
    jg      Done
```

or, when the limit is a constant, `CMP SI,03E8h` with no cell at all — which the
int16 fast path already does for literal bounds.

## Now

On the SI-resident FOR path (`TryEmitForCounterInRegister`, the common hot loop),
a limit that folds to a compile-time integer in `INTEGER` range is no longer
stored to a stack temp and reloaded — it becomes the immediate operand of the
per-iteration compare: `cmp si, 03E8h` at both the entry guard and the rotated
bottom test. No temp cell is allocated and no memory is read on the compare. A
non-constant limit (`FOR i% = 1 TO n%`), an out-of-range or float bound keeps the
temp and the `cmp si, [bp+disp]` form, exactly as before. The fold reuses the same
in-range integer test the [O0112](O0112-countdown-loop.md) countdown guard uses.

The **memory-counter fallback** (`EmitForInt16Fast`, taken when the body clobbers
SI/DI — a string op, a call — so the counter cannot live in a register) folds the
same way: `cmp ax, 64h` against the just-loaded counter, both ends. That path is
not itself `--optimize`-gated (it emits the faithful loop for every dialect), so
the immediate fold there is explicitly gated on the `Optimize` flag; with the
optimizer off it keeps `cmp ax, [bp+disp]`, byte-identical to genuine.

Gated on `--optimize` throughout (golden gate 250/250). Verified by self-differential
DOSBox runs — an ascending constant limit and a descending loop with a constant
`TO` on the SI path, and a string-concat loop on the memory-counter path — all
identical to `$OPTIMIZE OFF`, plus regression tests asserting each path emits
`cmp reg, imm` with no memory limit read while a variable limit keeps the cell.

## Still planned

- Holding a **variable** limit in a register across the loop. On an 8086 SI and DI
  are already spoken for by [O0005](O0005-register-residency.md), so the limit
  competes with the accumulator — another case for the cost model
  ([O0174](O0174-target-cost-models.md)); on a 386 there is room
  ([O0058](O0058-386-register-allocation.md)). It also needs a body proven not to
  clobber the chosen register.
- The same immediate fold on the memory-counter fallback path
  (`EmitForInt16Fast`), which is not `--optimize`-gated and so needs the fold
  guarded on the flag there.
