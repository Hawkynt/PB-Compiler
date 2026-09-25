# O0077 — Negation idioms

| | |
|---|---|
| **Status** | 🟡 Partial — integer `-(-x)` folds to `x` and `x * -1` to `0 - x` in the IR; the x86-16 selector spells `0 - x` as a zero load plus `SUB`, not `NEG`; float negations are not folded |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/InstCombine.cs` (`Sub`/`Mul` identities); `Ir/IrLowering.cs` lowers unary minus to `sub 0, x` / `fsub 0.0, x` |
| **Related** | [O0076](O0076-algebraic-identities.md), [O0004](O0004-strength-reduction.md), [O0033](O0033-constant-store.md) |

## The idea

Three rewrites around unary minus:

| Source | Becomes |
|---|---|
| `0 - x` | `NEG` |
| `x * -1` | `NEG` |
| `-(-x)` | `x` |

## Applies to

```basic
DIM x%, a%, b%, c%
a% = 0 - x%
b% = x% * -1
c% = -(-x%)
```

## Today

`0 - x%` loads the zero, stages it, and subtracts; `x% * -1` runs a multiply (or,
under `$OPTIMIZE SPEED`, a shift chain with a trailing `NEG`); the double
negation emits two negations.

## Now — `-(-x)` folds

```asm
    ; c% = -(-x%)   ->   c% = x%
    mov     ax, [x]
    mov     [c], ax
```

The lowering spells integer unary minus as `sub 0, x`. `InstCombine` rewrites
`0 - (0 - x)` to `x`: two modular sign flips cancel exactly, including at `-32768`
(`NEG(NEG(8000h)) = 8000h`) and `LONG` `80000000h`. The float spelling
(`fsub 0.0, x`) has no matching rule, so `-(-x!)` still negates twice on the FPU.

## Now — `x * -1` becomes `0 - x`

`InstCombine` rewrites an integer multiply by all-ones (either operand order) to
`0 - x`, so no multiply is emitted. `0 - x` itself is already the IR's negate. The
x86-16 selector has no `NEG` rule for it: the generic two-address path loads the
zero into the destination and subtracts `x`, a few bytes more than
`mov ax,[x] : neg ax`. Selecting `sub 0, x` as `NEG` is the remaining step.
