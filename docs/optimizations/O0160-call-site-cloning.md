# O0160 — Call-site cloning by range, alignment and aliasing

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program transformation |
| **Related** | [O0069](O0069-dead-parameter-elimination.md), [O0158](O0158-interprocedural-range-propagation.md), [O0152](O0152-vector-alias-versioning.md) |

## The idea

When call sites disagree, merging their facts produces a weak common
denominator. Cloning keeps the facts sharp: emit a specialized copy of the
procedure for each materially different **shape** of call.

Three shapes are worth specializing on:

- **range** — one clone for the callers passing small values, one for the rest;
- **alignment** — an aligned clone that can use wide/vector accesses, and a
  general one;
- **aliasing** — a "no-overlap" clone guarded by a runtime range check at the
  call site, and a conservative one.

## Applies to

```basic
SUB Fill(a%(), BYVAL n%, BYVAL v%)
  DIM i%
  FOR i% = 0 TO n%
    a%(i%) = v%
  NEXT
END SUB

CALL Fill(small%(), 3, 0)          ' tiny
CALL Fill(big%(), 30000, 0)        ' huge
```

## Today

One body, optimized for neither: too small to justify unrolling, too large to
leave scalar.

## Planned

Two clones — the tiny one unrolled outright, the huge one vectorized or lowered
to `REP STOSW` — with each call site bound to its clone at compile time.

## What it needs

- A **cost model** for the size/benefit trade (cloning multiplies code), and a
  cap on the number of clones per procedure.
- The ownership condition from
  [O0018](O0018-interprocedural-constant-propagation.md): the compiler must own
  every call site, or the general body must remain.
- It composes with [O0069](O0069-dead-parameter-elimination.md), since a clone
  specialized on a constant usually no longer needs that parameter.
