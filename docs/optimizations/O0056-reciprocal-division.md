# O0056 — Reciprocal-multiply division by a constant

| | |
|---|---|
| **Status** | 🟡 Partial — 16-bit signed `\`/`MOD` by a constant (under `$OPTIMIZE SPEED`) reciprocal-multiplies; 32-bit `LONG` and unsigned remain |
| **Stage** | Emitter (extends [O0004](O0004-strength-reduction.md)) |
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

## Today

```asm
    mov     ax, [n]
    mov     bx, 000Ah
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx               ; ~100+ cycles
    mov     [d], ax
```

## Now — 16-bit signed

`TryEmitStrengthReducedDivMod` routes a non-power-of-two constant divisor to
`TryMagicSigned16` + `EmitReciprocalDivMod16`: a signed high multiply (`IMUL`),
an arithmetic shift, and the sign add-back, replacing the ~100+-cycle `IDIV`. The
`MOD` form multiplies the quotient back and subtracts. `n% \ 10` / `n% MOD 10`
and every other 16-bit signed constant divide compile to a `MUL`+shift under
`$OPTIMIZE SPEED`.

### Why it is exact

The `(multiplier, shift)` pair is **brute-force-checked at compile time against
every one of the 65 536 `int16` dividends** — if any value would disagree with
`IDIV`, the magic is rejected and the genuine `IDIV` stays. So the rewrite is
exact by construction, matching PB's truncate-toward-zero `\` and dividend-signed
`MOD`. Confirmed by a differential checksum over the whole range `-32760…32760`
(mixed `\10`, `\7`, `MOD 10`, `MOD 100`) that is byte-identical to the genuine
oracle, plus a dedicated unit test. The `$ERROR` interaction is settled by
[O0004](O0004-strength-reduction.md): a non-zero constant divisor raises neither
Error 11 nor a quotient overflow.

## Still planned

- **32-bit `LONG`** constant division (`l& \ 10`) — needs a 32×32→64 magic and
  the wider shift sequence.
- **Unsigned** (`WORD`/`DWORD`) — the unsigned magic variant; today a `WORD`
  operand promotes to `LONG`, so it takes the `LONG` `IDIV` path.
The `$OPTIMIZE SPEED` gate this list used to name as planned already ships — the
rewrite is behind `this.OptimizeSpeed` in `TryEmitConstantDivide`, so a size-tuned
build keeps the compact `IDIV`, exactly as the status line above says. Verified
against the code 2026-08-06.

Native-only. The IR back ends leave `/ constant` for LLVM / the host C compiler,
which apply their own reciprocal-multiply against the real target's cost model.
