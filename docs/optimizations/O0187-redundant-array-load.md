# O0187 — Redundant array-element load caching

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Pre-emission analysis + emitter |
| **Source** | `CodeGen/OptCommonSubexpr.cs` — `CacheableArrayReadSymbol` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF69.BAS` |
| **Split from** | [O0003](O0003-common-subexpression-elimination.md) |

## What it is

A repeated array-element read `a%(i%)` with no intervening write reloads the
first read's stashed value instead of re-reading memory. The cache key is the
array symbol plus its indices.

Eligibility (`CacheableArrayReadSymbol`): a plain static array, not
`HUGE`/`VIRTUAL`/`ABSOLUTE`, with a 2-byte non-float element and simple
name/literal indices.

## Sample

```basic
DIM a%(0 TO 99), i%, m%
IF a%(i%) > m% THEN m% = a%(i%)      ' the element is read twice
```

## With the optimizer

The element is read **once** — which, together with
[O0188](O0188-cse-if-condition.md) registering the condition and
[O0034](O0034-redundant-load-elimination.md) dropping the reload, is what makes
the max-scan idiom hand-quality.

## Why it is safe

Any write to the array (to *any* element), to an index name, or a barrier
invalidates the entry; a write to a **different** array keeps it live. Under
`$ERROR BOUNDS` the check still runs where the first read ran.
