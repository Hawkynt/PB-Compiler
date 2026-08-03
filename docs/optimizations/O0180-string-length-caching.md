# O0180 — String length caching

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0003](O0003-common-subexpression-elimination.md), [O0181](O0181-empty-string-comparison.md), [R0003](R0003-string-engine.md) |

## The idea

`LEN(s$)` reads the string descriptor through the handle — a double
indirection through the descriptor table. Repeated in one expression, in a loop
condition, or in successive statements, it is recomputed every time even though
nothing changed the string.

The CSE machinery ([O0003](O0003-common-subexpression-elimination.md)) already
caches pure integer subexpressions; a `LEN` over an unmodified string variable
should be one of them.

## Applies to

```basic
DIM s$, i%, n%
FOR i% = 1 TO LEN(s$)            ' evaluated once by FOR, but...
  IF MID$(s$, i%, 1) = "x" THEN n% = n% + 1
NEXT
IF LEN(s$) > 0 AND LEN(s$) < 100 THEN PRINT "ok"    ' twice
```

## Today

Each `LEN` is a call into the string manager.

## Planned

The first `LEN(s$)` defines a CSE slot; the second reloads it. Any write to
`s$`, any call that could touch it, or a heap compaction invalidates the slot.

## What it needs

- `LEN` over a **bare string variable** classified as a cacheable pure leaf,
  keyed by the variable — the same treatment the array-element read got
  (`CacheableArrayReadSymbol`, `DIFF69.BAS`).
- Invalidation on every write to the string, on any barrier, and on anything
  that can move the heap — the string runtime's compaction does not change a
  *length*, which is what makes the length safe to cache where the *address*
  would not be.
- `LEN` over a fixed-length or ASCIIZ buffer is a compile-time constant and
  should fold outright ([O0001](O0001-constant-folding.md)).
