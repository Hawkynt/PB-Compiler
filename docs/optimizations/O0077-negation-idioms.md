# O0077 — Negation idioms

| | |
|---|---|
| **Status** | 🟡 Partial — `-(-x)` folds; `0 - x` / `x * -1` await the float-promotion routing |
| **Stage** | Emitter |
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

`EmitUnary` collapses `-(-x)` to the inner value: two sign flips cancel exactly
(`FCHS` then `FCHS` for the float-promoted forms, `NEG` then `NEG` for the
integral `LONG`), guarded on both negations producing the **same** type so no
rounding step sits between them. This is bit-exact even at `-32768`
(`NEG(NEG(8000h)) = 8000h`) and the whole `LONG` `80000000h` case — verified
byte-identical against the genuine oracle (`-(-x%)`, `-(-y&)`, `-(-(-32768))`).

## Still planned — `0 - x`, `x * -1`

```asm
    ; a% = 0 - x%   ->   neg ax
```

Both are blocked on the same routing as [O0076](O0076-algebraic-identities.md):
PB computes integral `-` and `*` in floating point, so `0 - x%` and `x% * -1`
reach the emitter as *float*-typed subtract/multiply trees rather than integer
ALU ops. Recognizing them as an `FCHS` (or an integer `NEG` in the modular-int
lowering) is the remaining work, together with the `-32768` / `LONG 80000000h`
Error-6 trap review under `$ERROR OVERFLOW` and the unsigned modular-complement
case.

Native-only, in `CodeGenerator.EmitUnary`. The IR back ends emit a `0 - x` /
`-1 * x` the host C compiler folds to a negate itself.
