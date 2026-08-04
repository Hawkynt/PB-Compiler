# O0098 — Sparse `SELECT` → balanced decision tree

| | |
|---|---|
| **Status** | 🟡 Partial (the balanced tree is emitted for sparse single-constant INTEGER `SELECT`s; the cluster analysis that makes dense sub-ranges tables within the tree is not) |
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

## Still planned

- A **cluster analysis**: dense sub-ranges should still become jump tables and
  only the sparse remainder a tree, so the two lowerings compose (a tree whose
  leaves are small tables).
- `LONG` subjects (the current path is `INTEGER`-only; a 32-bit subject needs a
  two-word compare at each node).
- The same arm emission and default routing [O0029](O0029-select-jump-table.md)
  already uses, so the arms themselves are untouched.
