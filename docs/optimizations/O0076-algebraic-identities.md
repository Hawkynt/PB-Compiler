# O0076 — Algebraic identities and annihilators

| | |
|---|---|
| **Status** | ⬜ Planned (the IR tier already does this — [O0043](O0043-ir-instcombine.md); the x86 emitter does not) |
| **Stage** | Emitter |
| **Related** | [O0043](O0043-ir-instcombine.md), [O0001](O0001-constant-folding.md), [O0016](O0016-value-fact-analysis.md) |

## The idea

Fold the identities directly, without evaluating the operator:

| Identity | Result |
|---|---|
| `x + 0`, `x - 0`, `x * 1`, `x \ 1`, `x AND -1`, `x OR 0`, `x XOR 0` | `x` |
| `x * 0`, `x AND 0`, `x MOD 1` | `0`, with `x` evaluated only if it is not pure |

[O0016](O0016-value-fact-analysis.md) already removes *fact-dependent*
identities (`x AND 255` when the high byte is provably clear). What is missing
is the unconditional, syntactic case, which needs no lattice at all.

## Applies to

```basic
DIM x%, y%, z%
y% = x% + 0
z% = x% * 0
```

## Today

Both are emitted as real operations (a constant load plus an `ADD`/`IMUL`), or
folded only if `x%` itself is a proven constant.

## Planned

```asm
    mov     ax, [x]
    mov     [y], ax          ; x + 0 is x
    xor     ax, ax
    mov     [z], ax          ; x * 0 is 0; x is pure, so it is not evaluated
```

## What it needs

- The **discardability** rule from [O0016](O0016-value-fact-analysis.md): the
  vanishing operand must be a plain variable read or a constant. Anything else
  could call a `FUNCTION`, or index an array whose `$ERROR BOUNDS` check is part
  of the observed behavior.
- Under `$ERROR OVERFLOW`, `x * 0` still cannot overflow, but `x \ 1` on
  `MININT` must keep its semantics — each identity needs its own trap review.
