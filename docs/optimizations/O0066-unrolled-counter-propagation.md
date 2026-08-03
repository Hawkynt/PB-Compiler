# O0066 — Unrolled-counter propagation

| | |
|---|---|
| **Status** | ⬜ Planned (blocked on AST-node keying) |
| **Stage** | Emitter |
| **Related** | [O0007](O0007-loop-unrolling.md), [O0036](O0036-constant-subscript-folding.md), [O0016](O0016-value-fact-analysis.md) |

## The idea

[O0007](O0007-loop-unrolling.md) fully unrolls a tiny constant-trip `FOR`, but
each copy still **reads the counter cell** and recomputes everything derived
from it. In an unrolled body the counter is a known constant per copy, so the
subscripts and the arithmetic should fold
([O0001](O0001-constant-folding.md), [O0036](O0036-constant-subscript-folding.md)).

## Applies to

```basic
$OPTIMIZE SPEED
DIM i%, a%(0 TO 3)
FOR i% = 0 TO 3
  a%(i%) = i% * i%
NEXT
```

## Today

Four copies, each with a multiply and an address computation:

```asm
    mov     ax, [i]
    imul    ax, ax, 2
    mov     bx, ax
    mov     ax, [i]
    push    ax
    mov     ax, [i]
    pop     bx
    imul    bx
    mov     [a+bx], ax
    ...                      ; three more times
```

## Planned

```asm
    mov     word ptr [a+0], 0000h
    mov     word ptr [a+2], 0001h
    mov     word ptr [a+4], 0004h
    mov     word ptr [a+6], 0009h
    mov     word ptr [i], 0004h
```

## Equivalent BASIC

```basic
a%(0) = 0 : a%(1) = 1 : a%(2) = 4 : a%(3) = 9
i% = 4
```

## Why it is blocked

The blocker is architectural, not semantic. The emitter is keyed by **original
AST-node identity** (`VariableBindings`, `TypeOf`, `ResolvedConstants`), so
substituting the counter with a literal per iteration would mean repopulating
every semantic side table for the cloned nodes.

## What it needs

The smaller path is a **per-iteration constant override** consulted by the
constant folder and by `IndexRangeOf`/`TryFoldSubscripts` — no cloning, no
side-table duplication: the unroller simply announces "for this copy, `i%` is 2"
and the existing folding machinery does the rest.
