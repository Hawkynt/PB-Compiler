# O0299 — Interned literal identity comparison

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0011](O0011-literal-overlap-pooling.md), [O0298](O0298-string-compare-length-guard.md) |

## The idea

The literal pool is deduplicated and packed
([O0011](O0011-literal-overlap-pooling.md)), so two occurrences of the same
literal have the same address. Comparing **two literal-backed values** is then an
address-and-length comparison rather than a byte comparison.

The useful case is a comparison against a `CONST` string or a `DATA` item that
the folder has already resolved to a pool entry.

## Applies to

```basic
%Mode = "fast"               ' equate, pooled
DIM m$
m$ = %Mode
IF m$ = %Mode THEN ...       ' both sides are the same pool entry
```

## What it needs

- The comparison must be provably between **canonical** pool references — a
  value copied into a dynamic string is a different allocation with the same
  bytes, and comparing addresses there would be wrong.
- Overlap packing means a literal is an (offset, length) pair rather than a
  unique object, so identity is "same offset and same length", not "same
  pointer".
- Realistically narrow: the honest value here is as a fast path inside
  [O0298](O0298-string-compare-length-guard.md), not as a general rewrite.
