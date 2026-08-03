# O0060 — Memory SSA / alias analysis

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end infrastructure |
| **Related** | [O0046](O0046-ir-gvn.md), [O0049](O0049-ir-licm.md), [O0028](O0028-loop-invariant-code-motion.md), [O0065](O0065-dead-frame-store-elimination.md) |

## The idea

Loads and stores get dependency edges, so memory becomes analyzable the way
values already are. That unblocks the passes currently stopped at "it touches
memory, give up":

- **load GVN** — a repeated array or field read across blocks collapses;
- **LICM of loads** — a loop-invariant `a(k)` or string-handle lookup hoists out
  of the loop;
- **cross-block dead-store elimination** — today only intra-block
  ([O0048](O0048-ir-dead-store-elimination.md));
- **dead frame stores** — [O0065](O0065-dead-frame-store-elimination.md).

PB is an unusually good candidate for this. Its aliasing barriers are explicit
and few — `VARPTR`/`STRPTR` escape, BYREF parameters, `@p` stores, `PEEK`/`POKE`
after `DEF SEG`, inline asm, external unit calls — and **everything else is
provably non-aliasing**. There is no arbitrary pointer arithmetic to spoil the
analysis, which is more than a C++ front end can say.

## Applies to

```basic
DIM a%(0 TO 99), i%, k%, s%
FOR i% = 0 TO 99
  s% = s% + a%(i%) * a%(k%)     ' a%(k%) is loop-invariant, but it is a load
NEXT
```

## Today

`a%(k%)` is re-read every iteration: the AST-tier CSE can cache it within a
straight-line run, but LICM refuses to hoist a load out of the loop because
nothing proves the body does not write the array.

## Planned

`a%(k%)` is hoisted to the preheader once memory dependencies show that the only
store in the body — if any — cannot alias it.

## What it needs

- A memory-version numbering (LLVM's MemorySSA shape) over the IR, plus the
  barrier list above as the conservative fallback.
- An alias oracle that understands PB's storage classes (static array, dynamic
  array, string descriptor, UDT field, `AT`-placed cell) as disjoint universes
  unless an address escapes.
