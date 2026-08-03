# O0284 — Semantic function merging

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program / assembler |
| **Related** | [O0040](O0040-identical-code-folding.md), [O0391](O0391-cold-code-deduplication.md), [P0006](P0006-header-squeeze.md) |

## The idea

[O0040](O0040-identical-code-folding.md) merges procedures whose bytes are
**identical**. Semantic merging goes one step further: two procedures that
differ in a single constant, one call target or one field offset become **one**
procedure plus a small parameter describing the difference.

Monomorphized generic instantiations are the obvious source — `Stack OF LONG`
and `Stack OF DWORD` differ in almost nothing — and so are near-duplicate
handlers in DOS-era code.

## Applies to

```basic
TYPE Stack OF T
  ...
END TYPE
DIM a AS Stack OF LONG
DIM b AS Stack OF DWORD      ' the same machine code except for signedness
```

## What it needs

- A structural diff over the emitted regions (or the IR) that can identify a
  **single** varying operand and prove everything else congruent.
- A cost check: the added parameter costs a push and a load at every call, so
  merging only pays for bodies above a size threshold — and it is a
  `$OPTIMIZE SIZE` transformation.
