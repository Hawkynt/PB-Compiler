# O0076 — Algebraic identities and annihilators

| | |
|---|---|
| **Status** | 🟡 Partial — the integral bitwise identities fold (fact-based and self-operand); the float-promoted arithmetic ones (`x + 0`, `x * 1`, …) remain |
| **Stage** | Emitter |
| **Related** | [O0043](O0043-ir-instcombine.md), [O0001](O0001-constant-folding.md), [O0016](O0016-value-fact-analysis.md), [O0077](O0077-negation-idioms.md) |

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

## Now — the integral identities fold

`TryEmitFactRedundantOp` (the [O0016](O0016-value-fact-analysis.md) lattice path)
folds `x AND -1`, `x OR 0`, `x XOR 0`, `x AND 0` → 0, and `x MOD 1` → 0; `x * 0`
→ 0 is handled by the strength-reduced multiply. On top of those, the
**self-operand** identities now fold outright:

| Identity | Result |
|---|---|
| `x AND x`, `x OR x` | `x` |
| `x XOR x` | `0` |

Sound because these operators stay **integral** (no float promotion), so the
rewrite is exact for any value of `x`; guarded on a *discardable* operand (a pure
variable/constant, via `IsSameLvalue`) so the shared value is read once with no
side effect to duplicate — `x XOR x` needs no read at all. Verified byte-identical
against the genuine oracle over INTEGER and LONG (`x XOR x`, `y AND y`, and
`(x XOR x)+7`).

## Still planned — the arithmetic identities

```asm
    ; y% = x% + 0   ->   mov ax,[x]      (x + 0 is x)
    ; x% * 1, x% \ 1, x% - 0 likewise
```

`x + 0`, `x - 0`, `x * 1`, `x \ 1` are **not** folded yet: PB computes integral
`+ - *` in floating point, so they reach the emitter as float-typed trees (the
same routing that gates [O0077](O0077-negation-idioms.md)). Folding them means
recognizing the identity inside the modular-int lowering (where the tree is
brought back to the integer ALU), plus the per-identity trap review — `x * 0`
cannot overflow, but `x \ 1` on `MININT` must keep its semantics under
`$ERROR OVERFLOW`.

Native-only. The IR tier already folds all of these
([O0043](O0043-ir-instcombine.md) instruction combining), so the C/LLVM back
ends emit the reduced form regardless.
