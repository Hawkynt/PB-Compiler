# O0098 — Sparse `SELECT` → balanced decision tree

| | |
|---|---|
| **Status** | ⬜ Planned |
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

## What it needs

- A **cluster analysis**: dense sub-ranges should still become jump tables and
  only the sparse remainder a tree, so the two lowerings compose (a tree whose
  leaves are small tables).
- A cost comparison against the compare chain for very small `n` — below about
  four arms the chain is already optimal.
- The same arm emission and default routing [O0029](O0029-select-jump-table.md)
  already uses, so the arms themselves are untouched.
