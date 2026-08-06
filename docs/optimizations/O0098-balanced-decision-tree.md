# O0098 — Sparse `SELECT` → balanced decision tree

| | |
|---|---|
| **Status** | 🟡 Partial (the balanced tree is emitted for sparse single-constant `INTEGER` **and** `LONG`/`DWORD` `SELECT`s; the cluster analysis that makes dense sub-ranges jump tables is not) |
| **Stage** | Emitter |
| **Related** | [O0029](O0029-select-jump-table.md), [O0099](O0099-bit-test-dispatch.md), [O0100](O0100-perfect-hash-dispatch.md) |

## The idea

[O0029](O0029-select-jump-table.md) requires a **dense** value span (≥ 4 cases,
span ≤ 256 and ≤ 4 × the case count) — otherwise the table would be mostly
default entries. A sparse `SELECT` falls all the way back to a linear compare
chain, which is O(n) in the number of arms.

A **balanced binary decision tree** over the sorted case values is O(log n) with
no table at all: compare against the median, recurse into the half that can
contain the value.

## Applies to

```basic
SELECT CASE code%
  CASE 1    : ...
  CASE 17   : ...
  CASE 250  : ...
  CASE 1000 : ...
  CASE 4096 : ...
  CASE ELSE : ...
END SELECT
```

## Today

Five compares and five branches worst case.

## Planned

```asm
    mov     ax, [code]
    cmp     ax, 03E8h        ; median: 1000
    je      Arm4
    jg      High
    cmp     ax, 0011h        ; 17
    je      Arm2
    jg      Arm3Check        ; 250
    cmp     ax, 0001h
    je      Arm1
    jmp     Default
High:
    cmp     ax, 1000h        ; 4096
    je      Arm5
    jmp     Default
```

Three compares worst case instead of five, and it scales logarithmically.

## Now

`TryEmitSelectDecisionTree` (`CodeGenerator.cs`) fires when the dense jump table
([O0029](O0029-select-jump-table.md)) has declined, the subject is `INTEGER`,
every arm is a single-constant point case (no ranges, no `IS`), and there are at
least **8 distinct values** (below that the linear chain is as fast and smaller).
It sorts the values, keeps the subject in `AX`, and emits exactly the tree the
"Planned" listing shows: a signed `CMP AX, median` / `JE arm` / `JG right` at each
node, recursing into the half that can hold the value; a value in no arm falls to
`CASE ELSE` (or the end). First-match-wins is preserved by routing each value to
its **first** arm, so the same arm runs as the compare chain — verified by a
self-differential DOSBox run over the whole subject range (every match, the
boundaries, negatives and non-matches) being identical to `$OPTIMIZE OFF`, plus a
regression test pinning the `CMP AX, imm` tree shape (and its absence below the
threshold). Gated on `$OPTIMIZE SPEED` (the tree's extra `JL`/`JG` branches can be
larger than the chain) so non-optimized output is byte-identical to genuine
(golden gate 250/250).

The same widening and the same guard serve [O0099](O0099-bit-test-dispatch.md);
[O0100](O0100-perfect-hash-dispatch.md)'s hash is the one still refusing a
non-INTEGER subject.
- The same arm emission and default routing [O0029](O0029-select-jump-table.md)
  already uses, so the arms themselves are untouched.

A `LONG`/`DWORD` subject now dispatches through the tree. Every tree point must
fit an int16 to survive the fold, and the tree compares `AX`, so a 32-bit subject
is first proven to BE its own int16 low half: `CWD` against the real high word
(held in `CX` across the check, since the tree only ever touches `AX`). A subject
failing it cannot equal any point and goes straight to the default path. Without
the check the tree compares a truncated low word — 0001_0064h reads as 100 — and
takes an arm the program never selected.

## Still planned

- A **cluster analysis**: dense sub-ranges should still become jump tables and
  only the sparse remainder a tree, so the two lowerings compose (a tree whose
  leaves are small tables). Worth building, but it needs a **big** cluster to pay,
  and the threshold belongs in the implementation rather than being discovered
  after it.

  A cluster costs one tree node to reach plus an indexed jump; those same values
  as tree points cost `ceil(log2 k)` nodes. Pricing both (era-typical figures, the
  table path dominated by the indirect memory jump):

  | tier | table path | k=8 | k=16 | k=32 | k=64 | break-even k |
  |---|---|---|---|---|---|---|
  | 8086 | 48 | 36 | 48 | 60 | 72 | **17** |
  | 286 | 29 | 27 | 36 | 45 | 54 | 9 |
  | 386 | 20 | 18 | 24 | 30 | 36 | 9 |
  | 486 | 11 | 9 | 12 | 15 | 18 | 9 |
  | Pentium | 8 | 6 | 8 | 10 | 12 | 17 |
  | P6 | 8 | 6 | 8 | 10 | 12 | 17 |

  So on the default target a cluster of fewer than ~17 values is *slower* as a
  table than as tree points — the indexed jump is worth about three compares.

  Narrowing it further: the tree only runs when the whole-select jump table has
  already declined, which it does for `span > 256` or under 25% density
  (`TryEmitSelectJumpTable`). The shape that actually benefits is therefore a
  SELECT listing 17+ individual dense constants *plus* outliers far enough out to
  push the span past 256 — real, but not common. Gate on the cluster size; do not
  cluster because a run merely exists.
