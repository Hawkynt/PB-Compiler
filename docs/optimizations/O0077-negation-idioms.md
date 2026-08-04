# O0077 — Negation idioms

| | |
|---|---|
| **Status** | ✅ Done — `-(-x)`, `0 - x` and `x * -1` all fold to a negate in the integer paths (a float-typed-position residue rides the FPU, folded by the IR tier for C/LLVM) |
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

## Now — `0 - x` and `x * -1` fold too

```asm
    ; a% = 0 - x%    ->   mov ax,[x] : neg ax
    ; b% = x% * -1   ->   mov ax,[x] : neg ax
```

Assigned to an integer target, both lower through the modular-int path: the
`c - v` shape of the subtract negates then adds `c` (here `c = 0`, adding
nothing — `TryEmitModularConstAddSub`), and `* -1` becomes `neg ax`
unconditionally under `--optimize` (`TryEmitModularConstMul`). Bit-exact even at
`-32768` (`NEG(8000h) = 8000h`, matching PB's own modular store), verified
byte-identical against the genuine oracle over `0 - x`, `-1 * x` and
`x% * -1` at `MININT`.

The only unfolded residue is a negation consumed in a **float-typed
subexpression position**, which stays on the FPU; the IR tier folds it for the
C/LLVM back ends. Native-only, in `CodeGenerator.EmitUnary` /
`CodeGenerator.Optimize` (the modular lowering).
