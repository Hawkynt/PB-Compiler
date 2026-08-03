# O0223 — Fact-proven constant result

| | |
|---|---|
| **Status** | ✅ Implemented (2026-08) |
| **Stage** | Emitter, on the value lattice |
| **Source** | `CodeGen/CodeGenerator.cs` — `TryEmitFactRedundantOp` |
| **Gate** | `--optimize` |
| **Split from** | [O0016](O0016-value-fact-analysis.md) |

## What it is

An operation whose **result** the facts already know emits the constant — while
still evaluating the operand for its effects:

- `(x * 4) AND 3` is always zero, because the low two bits of a multiple of four
  are;
- `(x * 10) MOD 5` is always zero, because the congruence proves the multiple.

The second kind is where the **congruence** domain earns its place: no interval
and no bit pattern can see that a multiple of ten is also a multiple of five.

## Sample

```basic
DIM x%, a%, b%
a% = (x% * 4) AND 3          ' always 0
b% = (x% * 10) MOD 5         ' always 0
```

## With the optimizer

```asm
    ; x% is still evaluated if it could have an effect; the result is a literal
    xor     ax, ax
    mov     [a], ax
```

## Why it is safe

The operand is **still evaluated** (it may call a `FUNCTION` or raise a bounds
error) and only its value is discarded — the distinction from
[O0222](O0222-identity-operation-removal.md), where the operand is what
survives.
