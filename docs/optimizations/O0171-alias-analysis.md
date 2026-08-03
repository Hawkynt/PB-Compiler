# O0171 — Alias analysis (basic, type-based, allocation-site)

| | |
|---|---|
| **Status** | ⬜ Planned as a shared analysis (ad-hoc alias tests exist in [O0047](O0047-ir-redundant-memory.md), [O0048](O0048-ir-dead-store-elimination.md) and the scheduler) |
| **Stage** | Analysis infrastructure |
| **Related** | [O0060](O0060-memory-ssa.md), [O0140](O0140-load-store-motion.md), [O0152](O0152-vector-alias-versioning.md), [O0161](O0161-function-summaries.md) |
| **Split into** | [O0262](O0262-type-based-alias.md), [O0263](O0263-allocation-site-alias.md) |

## The idea

Most advanced optimizations refuse to fire without an answer to "can these two
accesses touch the same byte?". Three layers, in increasing precision:

1. **Basic** — distinguish storage *kinds*: a local, a module global, a static
   array, a dynamic array's data, string heap storage, an `AT`-placed cell, the
   stack. Different kinds cannot alias unless an address escapes.
2. **Type-based** — two accesses through incompatible element types cannot
   alias, where BASIC's semantics permit that conclusion (PB has no unions of
   convenience except `UNION` itself, which must be excluded).
3. **Allocation-site** — two independently `DIM`ed arrays or independently
   allocated strings are distinct objects, and stay distinct through copies of
   their descriptors.

PB is a good candidate: its aliasing entry points are **few and explicit** —
`VARPTR`/`STRPTR` escape, `BYREF` parameters, `@p` stores, `PEEK`/`POKE` after
`DEF SEG`, inline asm, external unit calls — so everything else is provably
non-aliasing by construction.

## Applies to

```basic
DIM a%(0 TO 99), b%(0 TO 99), i%
FOR i% = 0 TO 99
  b%(i%) = a%(i%) * 2        ' distinct arrays: no dependence at all
NEXT
```

## Today

Each pass makes its own conservative guess; the scheduler's model, for example,
knows only "direct cell / `[BP+disp]` / unknown-indexed".

## Planned

One oracle, consulted by CSE, LICM, dead stores, vectorization, scheduling and
memory SSA — with the escape facts computed once per body.

## What it needs

- An escape analysis over the AST (the reflective node walk
  [O0022](O0022-dead-procedure-elimination.md) uses is the right instrument) plus
  the call summaries of [O0161](O0161-function-summaries.md).
- A conservative default that is *cheap to state*: unknown ⇒ may alias, so every
  consumer stays correct while the precision improves.
