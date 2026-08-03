# O0192 — Parity / zero-test modulo mask

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` (modulo lowering, compare-to-zero path) |
| **Gate** | `--optimize` |
| **Verified by** | scenario `ParityTestIsAMask`, oracle-verified over negative dividends and MOD 2/4/8 |
| **Split from** | [O0004](O0004-strength-reduction.md) |

## What it is

The everyday even/odd test `IF n MOD 2 = 0` does not need the remainder's
*value*, only whether it is zero. And `(x MOD 2^n) = 0` iff
`(x AND (2^n-1)) = 0` **for every sign** — the sign fix-up
([O0191](O0191-modulo-power-of-two.md)) changes the remainder's value but never
whether it is zero.

So the condition becomes a bare `AND` driving the branch on its own flags:
three instructions where the full modulo was eight.

## Sample

```basic
DIM n%
IF n% MOD 2 = 0 THEN PRINT "even"
```

## With the optimizer

```asm
    mov     ax, [n]
    and     ax, 0001h
    jnz     NotEven          ; the AND's own flags drive the branch (O0031)
```

## Equivalent BASIC

```basic
IF (n% AND 1) = 0 THEN PRINT "even"
```

## Why it is safe

The equivalence is exact for both signs and for `MININT`, and it applies to
`<> 0` as well by inverting the branch. Any other comparison against the modulo
keeps the full reconstruction.
