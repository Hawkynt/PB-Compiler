# O0061 — Reassociation

| | |
|---|---|
| **Status** | 🟡 Partial — the IR tier reassociates constant chains (`op(op(x,c1),c2) → op(x, c1∘c2)`), `x - C → x + (-C)`, and orders constants to the right; canonical *variable* operand ordering by SSA id (so `x+y+1` and `1+y+x` align for GVN) is not done |
| **Stage** | Mid-end, before CSE/GVN |
| **Related** | [O0003](O0003-common-subexpression-elimination.md), [O0046](O0046-ir-gvn.md), [O0064](O0064-lea-fusion.md) |

## The idea

Integer `+`/`*` chains are associative and commutative modulo 2ⁿ, so the
compiler may re-order them into a canonical shape. Two things fall out: equal
subtrees line up so CSE/GVN can see them, and operand groupings appear that map
onto `LEA` and shift-add sequences.

## Applies to

```basic
DIM x%, y%, a%, b%
a% = x% + y% + 1
b% = 1 + y% + x%        ' the same value, written differently
```

## Today

The two trees are structurally different (`(x+y)+1` vs `(1+y)+x`), so CSE does
not recognize them and both are computed.

## Planned

Both canonicalize to the same operand-sorted form, so the second reloads the
first's slot:

```asm
    mov     ax, [x]
    add     ax, [y]
    inc     ax
    mov     [bp-6], ax       ; CSE define
    mov     [a], ax
    mov     ax, [bp-6]       ; reload
    mov     [b], ax
```

## Equivalent BASIC

```basic
DIM t%
t% = x% + y% + 1
a% = t%
b% = t%
```

## What it needs

- A canonical operand order (by SSA value id, with constants folded to one
  term), applied only to integral `+`/`*` trees.
- **Wrap correctness is the catch**: over the dialects that wrap in place,
  reassociation is still valid modulo 2ⁿ for `+`/`*`, but *not* for a tree that
  mixes widths or crosses a promotion boundary — and under `$ERROR OVERFLOW` it
  changes *which* intermediate overflows first, which is observable. So the pass
  must be gated the same way [O0016](O0016-value-fact-analysis.md)'s folds are:
  every node's type must hold its own result, and checked arithmetic opts out.
- Float chains are never reassociated (rounding is not associative).
