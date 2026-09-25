# O0192 — Parity / zero-test modulo mask

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR scalar simplification (`InstCombine`), before [O0191](O0191-modulo-power-of-two.md) expands the remainder |
| **Source** | `Ir/Passes/InstCombine.cs` — `SimplifyBinary` (`SRem` case), `IsZeroTest` |
| **Gate** | `--optimize` |
| **Verified by** | scenario `ParityTestIsAMask`, oracle-verified over negative dividends and MOD 2/4/8 |
| **Split from** | [O0004](O0004-strength-reduction.md) |

## What it is

The everyday even/odd test `IF n MOD 2 = 0` does not need the remainder's
*value*, only whether it is zero. And `(x MOD 2^n) = 0` iff
`(x AND (2^n-1)) = 0` **for every sign** — the sign fix-up
([O0191](O0191-modulo-power-of-two.md)) changes the remainder's value but never
whether it is zero.

So the condition can become a bare `AND` driving the branch on its own flags:
three instructions where the full modulo was eight.

On the IR it is a rewrite of the remainder itself: a signed `x SREM 2^k` whose
every user compares it with zero becomes `x AND (2^k-1)`, which each compare then
tests. It runs in `InstCombine`, ahead of `VerifiedArithmeticLowering`, so the
sign-biased expansion of [O0191](O0191-modulo-power-of-two.md) is never built for
it. A remainder whose value is used keeps its sign and its full lowering.

## Sample

```basic
DIM n%
IF n% MOD 2 = 0 THEN PRINT "even"
```

## Intended output

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
