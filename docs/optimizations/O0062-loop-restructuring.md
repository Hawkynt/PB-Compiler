# O0062 — Loop rotation, IV simplification and fusion

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0030](O0030-induction-variable-strength-reduction.md), [O0007](O0007-loop-unrolling.md) |

## The idea

Three classic loop transforms that the current pipeline does not do:

1. **Rotation** — a pre-test loop (`test; body; jmp test`) becomes
   `if !test goto end; do body while test`, which costs one branch per iteration
   instead of two and makes the body a single block that LICM and CSE can treat
   as straight-line.
2. **Induction-variable simplification** — derived induction variables
   (`j = 2*i + 3` inside the loop) are rewritten as their own incrementally
   updated variables, and redundant ones are coalesced.
3. **Fusion** — two adjacent loops with the same trip count over the same arrays
   merge into one, halving the loop overhead and improving locality.

## Applies to

```basic
DIM i%, a%(0 TO 99), b%(0 TO 99)
FOR i% = 0 TO 99 : a%(i%) = i% : NEXT
FOR i% = 0 TO 99 : b%(i%) = a%(i%) * 2 : NEXT
```

## Today

Two loops, 200 iterations of loop overhead, and `a%()` is walked twice.

## Planned (fusion)

```basic
FOR i% = 0 TO 99
  a%(i%) = i%
  b%(i%) = a%(i%) * 2
NEXT
```

## What it needs

- **Rotation** needs the CFG builder to accept post-test loops, which it
  currently bails on — that alone would widen every SSA-based pass.
- **Fusion** needs a dependence test: the second loop may not read an element of
  `a%()` that the first writes at a *later* index (a backward dependence),
  and neither loop may have side effects that observe the interleaving (`PRINT`,
  file I/O, `TIMER`).
- The counter's post-loop value must be preserved in every case
  (increment-then-test, QUIRK 2.28).
