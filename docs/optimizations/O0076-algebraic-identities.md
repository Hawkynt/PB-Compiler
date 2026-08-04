# O0076 — Algebraic identities and annihilators

| | |
|---|---|
| **Status** | ✅ Done — folds in the integer-materializing paths (assignments, bitwise, self-operand); a `+ 0` buried in a float-typed subexpression rides the FPU (and is folded by the IR tier for C/LLVM) |
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

## Now — the arithmetic identities fold in the integer path

```asm
    ; y% = x% + 0   ->   mov ax,[x]       (nothing added)
    ; z% = x% * 0   ->   xor ax,ax
    ; y% = x% * 1   ->   mov ax,[x]       (no IMUL)
```

An arithmetic tree assigned to an integer target is lowered back to the integer
ALU (the modular-int lowering — PB computes integral `+ - *` in floating point,
but the low bits of the exact result *are* the modular value). There:

- `x + 0`, `x - 0` add a zero immediate, which `EmitModularAddImm` emits as
  **nothing** (`TryEmitModularConstAddSub`).
- `x * 0` → `xor ax,ax`, `x * 1` → the operand unchanged, `x * -1` → `neg ax`
  (`TryEmitModularConstMul`). These three are strictly smaller than `IMUL`, so
  they fold under plain `--optimize` — not only `$OPTIMIZE SPEED`, which the
  shift/add multiply decompositions still require.

Each is bit-exact against the modular result the generic `IMUL`/`ADD` would give
(`x * 0 = 0`, `x * -1 = NEG x` even at `-32768`), verified byte-identical against
the genuine oracle.

The one residue: an identity in a **float-typed subexpression position** (say
`PRINT (x% + 0) * 1.5`, where `x% + 0` is consumed as a `SINGLE`) still rides the
FPU. The IR tier folds those too ([O0043](O0043-ir-instcombine.md) instruction
combining), so the C/LLVM back ends emit the reduced form regardless; only the
native-x86 float path leaves that rare shape unfolded.
