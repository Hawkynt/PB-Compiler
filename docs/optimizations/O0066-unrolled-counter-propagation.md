# O0066 — Unrolled-counter propagation

| | |
|---|---|
| **Status** | ✅ Done |
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

## How it works

The per-iteration constant override the "what it needs" section anticipated: no
cloning, no side-table duplication. `ConstantFolder` already takes an optional
`resolve` callback, so `OptFolder` is given one (`ResolveUnrollCounter`) that
returns the counter's current value. `TryEmitUnrolledFor` sets `_unrollCounter =
(counter, value)` around each copy's body; every fold site downstream —
`TryFold`, and through it `FactsOf`, `IndexRangeOf`, constant-subscript folding —
then reads the literal for `i%`. Outside unrolling the field is null, so the
resolver is inert on every other path (the golden gate never sees it).

`i%`-derived arithmetic and subscripts collapse: `s = s + i% * i%` over `1…4`
emits four `add`-immediates with **no runtime multiply**, and `a%(i%) = i%*i%`
becomes four constant stores. Verified byte-identical against the genuine oracle,
and a regression test confirms the unrolled `i% * i%` leaves no `IMUL`.

### Safety

The override is set **only when the body cannot reassign the counter**
(`IsModifiedIn`), so a later read can never fold to a stale value. Since the copy
still writes the counter cell (`mov [i], value`), any read the folder does *not*
collapse — a by-ref pass, say — still sees the correct runtime value.

Native-only, in `CodeGenerator`. The IR back ends emit the unrolled body (from
LLVM's own full-unroll) with the counter as an SSA constant, so their folders
propagate it without this hook.
