# O0056 — Reciprocal-multiply division by a constant

| | |
|---|---|
| **Status** | 🟡 Partial — 16-bit signed `\`/`MOD` by a positive constant (under `$OPTIMIZE SPEED`) reciprocal-multiplies; 32-bit `LONG`, unsigned, and negative non-power-of-two constants remain |
| **Stage** | IR middle end + target selection (extends [O0004](O0004-strength-reduction.md)) |
| **Related** | [O0004](O0004-strength-reduction.md), [R0003](R0003-string-engine.md) |

## The idea

Division by a power of two already lowers to a shift. Division by any *other*
compile-time constant can be lowered too, with the standard magic-number trick:
multiply by a fixed-point reciprocal and shift the high half down. On an 8086,
where `DIV`/`IDIV` costs ~80–160 cycles against `MUL`'s ~120 for a full 16×16
product plus a couple of shifts, the win is real; on a 386+ it is decisive.

It pairs naturally with a two-digit-table number formatter (see
[R0003](R0003-string-engine.md)), which is the biggest consumer of
divide-by-ten in PRINT-heavy code.

## Applies to

```basic
DIM n%, d%, r%
d% = n% \ 10
r% = n% MOD 10
```

## Baseline

```asm
    mov     ax, [n]
    mov     bx, 000Ah
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx               ; ~100+ cycles
    mov     [d], ax
```

## Now — 16-bit signed in the IR

`VerifiedArithmeticLowering` handles O0056 after the canonical/value passes. Under
`$OPTIMIZE SPEED`, a positive, non-power-of-two signed 16-bit constant divisor is
replaced by the ordinary target-neutral IR spelling of signed high multiplication:

```text
wide = sext i16 n to i32
product = wide * magic
high = trunc i16 (product >>a 16)
[high += n]                 ; only for magic values with the sign bit set
q = (high >>a shift) - (n >>a 15)
```

`MOD` reuses that exact quotient and computes `n - q * divisor`. No target-specific
`mulhi` pseudo-op is needed: C and LLVM receive normal widening/multiply/shift/trunc
operations and can optimize them against their own target cost model. The x86-16
selector recognizes the single-use `sext -> i32 mul -> ashr 16 -> trunc` shape and
selects the high DX half of one accumulator `IMUL`, so routed native code does not
fall through to the 32-bit multiply helper.

The direct emitter keeps its existing O0056 sequence as the non-routed fallback;
the arithmetic decision itself is no longer native-only.

### Why it is exact

The `(multiplier, shift, add-back)` plan is **brute-force-checked at compile time
against every one of the 65 536 `int16` dividends** before it may be emitted. If
any value disagrees with signed truncate-toward-zero division, the candidate is
rejected and the genuine divide remains. The same exhaustive pass verifies the
remainder identity `n - q*d`, including negative dividends.

The derivation is the standard Granlund-Montgomery / signed magic-number method,
but the implementation is self-contained and the verification is the final
admission rule. This preserves PB's truncate-toward-zero `\` and dividend-signed
`MOD` semantics rather than trusting an algebraic rewrite on faith.

The `$ERROR` interaction is settled by [O0004](O0004-strength-reduction.md): a
non-zero positive constant divisor raises neither Error 11 nor a quotient overflow.
The speed gate is intentional; ordinary/size-oriented optimization keeps the compact
`IDIV` rather than expanding the expression.

## Still planned

- **32-bit `LONG`** constant division (`l& \ 10`) — needs a 32×32→64 magic and a
  portable high-half spelling (or an explicit target-independent primitive).
- **Unsigned** (`WORD`/`DWORD`) — the unsigned magic variant; today a `WORD`
  operand promotes to `LONG`, so it takes the `LONG` divide path.
- **Negative non-power-of-two signed constants** — the previous emitter optimization
  only covered positive divisors, and the IR migration deliberately preserves that
  scope instead of silently expanding O0056 while moving it.
