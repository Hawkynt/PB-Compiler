# O0117 — Bounds-check merging and hoisting

| | |
|---|---|
| **Status** | ⬜ Planned (check *removal* when provably safe is done — [O0016](O0016-value-fact-analysis.md)) |
| **Stage** | Emitter |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0010](O0010-redundant-statement-elimination.md), [O0068](O0068-array-zero-fill-elision.md) |

## The idea

[O0016](O0016-value-fact-analysis.md) drops a bounds check that provably cannot
fire. When a check *can* fire it is observable behavior (Error 9) and may not be
dropped — but it may be:

- **merged**: several accesses with the same index in one statement need one
  check, not one per access;
- **hoisted**: a loop-invariant check moves to the preheader;
- **widened**: a counted loop over `lo TO hi` can check the two endpoints once
  before the loop instead of every index inside it.

The rule that makes all three legal: the check must still fire, with the same
error, on exactly the same inputs — only *sooner*, and only where nothing
observable happens in between.

## Applies to

```basic
$ERROR BOUNDS ON
DIM a%(0 TO 99), b%(0 TO 99), i%, n%
FOR i% = 0 TO n%             ' n% is not a constant, so O0016 cannot drop the checks
  a%(i%) = b%(i%) + b%(i%)
NEXT
```

## Today

Three checks per iteration — one per subscript occurrence.

## Planned

```asm
    ; preheader: check the endpoints once
    cmp     word ptr [n], 0063h
    jg      rt_err_arr
Top:
    ...                      ; no per-access checks at all
```

## What it needs

- A **dominance** argument for merging (the second check is dominated by the
  first with no write to the index in between) — the same invalidation set the
  CSE cache uses.
- For the loop form, the check must be *equivalent*: checking `hi` covers every
  index only when the sequence is monotonic and the lower endpoint is checked
  too. A `STEP` that can wrap breaks the equivalence.
- No observable statement may sit between the moved check and its original
  position, or the error would be raised at a different point in the output.
