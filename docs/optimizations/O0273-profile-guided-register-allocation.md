# O0273 — Profile-guided register allocation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Register allocation |
| **Related** | [O0058](O0058-386-register-allocation.md), [O0176](O0176-register-pressure-scheduling.md), [O0268](O0268-profile-collection.md) |

## The idea

Spill cost is not uniform: a reload inside a loop that runs a million times costs
a million memory accesses, and one on an error path costs one. Weighting each
live range by its **block execution frequency** puts the registers where the
program actually spends its time — and places the spills where it does not.

## Applies to

```basic
SUB Render
  LOCAL x%, y%, err%
  FOR y% = 0 TO 199          ' hot: x% and y% deserve the registers
    ...
  NEXT
  IF err% THEN ...           ' cold: err% is a fine spill candidate
END SUB
```

## What it needs

- Block frequencies ([O0268](O0268-profile-collection.md)); without a profile,
  the static heuristic "inside a loop is hotter" is already a usable
  approximation and is what the current residency passes assume implicitly.
- An allocator whose spill decisions are cost-driven rather than structural
  ([O0058](O0058-386-register-allocation.md)).
