# O0189 — Multiply by `2^a ± 2^b`

| | |
|---|---|
| **Status** | ✅ Implemented (modular int16 path) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region O4` (modular 16-bit multiply) |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `tests/diff/DIFF42.BAS` |
| **Split from** | [O0004](O0004-strength-reduction.md) (which is now the power-of-two multiply only) |

## What it is

Multipliers beyond a single power of two:

| Multiplier shape | Lowered to |
|---|---|
| `2^a + 2^b` (two bits set) | `(v + v<<(a-b)) << b` |
| `2^a - 2^b` (a contiguous run of set bits) | `(v<<(a-b) - v) << b` |
| a negative multiplier | the same chain plus a trailing `NEG` |
| three-term multipliers | left as the compact `IMUL` |

## Sample

```basic
$OPTIMIZE SPEED
DIM v%, r%
r% = v% * 10                 ' 10 = 2^3 + 2^1
```

## With the optimizer

```asm
    mov     ax, [v]
    mov     bx, ax
    shl     ax, 1            ; v<<(3-1) staged
    shl     ax, 1
    add     ax, bx
    shl     ax, 1            ; << b
```

## Why it is safe

The modular int16 path only ever runs **unchecked**, so every chain reproduces
the product's low 16 bits exactly. Under `$ERROR OVERFLOW ON` the reduction backs
off entirely and the real `IMUL` keeps its `JNO` guard
([O0004](O0004-strength-reduction.md)).

## Limits

The general cost-model-driven decomposition for every constant and every path is
[O0078](O0078-multiply-decomposition.md).
