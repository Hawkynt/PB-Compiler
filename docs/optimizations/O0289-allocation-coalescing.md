# O0289 — Allocation coalescing

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0024](O0024-multi-concat.md), [O0286](O0286-allocation-elimination.md), [O0294](O0294-string-builder-recognition.md) |

## The idea

Several short-lived allocations with overlapping lifetimes become **one** block,
carved up internally. [O0024](O0024-multi-concat.md) already does exactly this
for a concatenation chain — one `StrAlloc` for what would have been N−1 — and the
same argument applies wherever a procedure builds a small set of temporaries
together.

## Applies to

```basic
DIM a$, b$, c$
a$ = LEFT$(src$, 10)
b$ = MID$(src$, 11, 10)
c$ = RIGHT$(src$, 10)        ' three allocations, one lifetime
```

## What it needs

- Lifetime analysis over the temporaries (the same information
  [O0086](O0086-spill-slot-reuse.md) needs for frame slots).
- A carve-up representation the string runtime understands, or generated code
  that manages the sub-blocks itself — the same design question as
  [O0287](O0287-stack-promotion.md).
- Freeing must remain exact: the coalesced block is released once, when the last
  member dies, which requires the lifetimes to genuinely nest.
