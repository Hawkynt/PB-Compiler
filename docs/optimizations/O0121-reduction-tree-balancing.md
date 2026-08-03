# O0121 — Reduction tree balancing

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0061](O0061-reassociation.md), [O0119](O0119-reduction-recognition.md), [O0120](O0120-multiple-accumulators.md) |

## The idea

`a + b + c + d + e + f + g + h` parses left-associatively into a chain seven
operations deep. Reassociated into a balanced tree it is three levels deep, and
on any target that can issue two independent operations at once the difference
is real.

```
((((((a+b)+c)+d)+e)+f)+g)+h      ->      ((a+b)+(c+d)) + ((e+f)+(g+h))
```

## Applies to

```basic
DIM a%, b%, c%, d%, e%, f%, g%, h%, t%
t% = a% + b% + c% + d% + e% + f% + g% + h%
```

## Today

A strictly serial chain of seven adds through the accumulator.

## Planned

Four independent adds, then two, then one — same value, one third of the
dependency depth.

## What it needs

- [O0061](O0061-reassociation.md)'s legality argument, which is the binding
  constraint: reassociation of integer `+`/`*` is exact modulo 2ⁿ, but it changes
  **which intermediate overflows first**, so it is off under `$ERROR OVERFLOW`
  and needs the per-node type check every other fold uses.
- Enough registers to hold the partial results
  ([O0058](O0058-386-register-allocation.md)); with one accumulator the balanced
  tree spills and loses.
- Never for floats — floating-point addition is not associative, and PB's
  observable output would change.
