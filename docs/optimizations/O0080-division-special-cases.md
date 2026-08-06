# O0080 — Division and modulo special cases

| | |
|---|---|
| **Status** | ✅ Done — every case folds, `x \ -1` included (on a proven `x <> MININT`) |
| **Stage** | Emitter |
| **Related** | [O0004](O0004-strength-reduction.md), [O0016](O0016-value-fact-analysis.md), [O0056](O0056-reciprocal-division.md) |

## The idea

Divisors the hardware never needs to see:

| Expression | Result | Status |
|---|---|---|
| `x \ 1` | `x` | ✅ folds unconditionally (÷1 never traps) |
| `x MOD 1` | `0` | ✅ (`facts.Mod.IsMultipleOf(1)`) |
| `x \ -1` | `-x` | ✅ folds when the interval domain proves `x <> MININT`; otherwise the divide stays |
| `x MOD -1` | `0` | ✅ (`facts.Mod.IsMultipleOf(-1)`) |
| `x \ k` where the facts prove `|x| < |k|` | `0` | ✅ ([O0016](O0016-value-fact-analysis.md)) |
| `x MOD k` where the facts prove `0 <= x < k` | `x` | ✅ ([O0016](O0016-value-fact-analysis.md)) |

All but `x \ -1` fold in `TryEmitFactRedundantOp`; the `x \ 1 → x` fold was
verified byte-identical against the genuine oracle (including `-32768 \ 1`).

Generalizing the non-negative case also covers `n MOD 8` → `n AND 7` for any
provably non-negative `n`, which today fires only for the parity shape and for
constant-bounded ranges.

## Applies to

```basic
DIM x%, i%, a%, b%
a% = x% \ 1
FOR i% = 0 TO 99
  b% = i% MOD 8              ' i% is provably in [0,99]
NEXT
```

## Today

`x% \ 1` emits a real `IDIV` with its Error-11 guard; `i% MOD 8` emits the
signed remainder reconstruction (`CWD`, bias, mask, un-bias).

## Planned

```asm
    mov     ax, [x]
    mov     [a], ax          ; \ 1
    ...
    mov     ax, si           ; i%
    and     ax, 0007h        ; MOD 8 on a provably non-negative value
    mov     [b], ax
```

## How `x \ -1` folds

`NEG` cannot replace the divide unconditionally: `MININT \ -1` overflows (the
quotient +32768 does not fit the destination) where `NEG 8000h` is `8000h` and
reports nothing, so an unconditional fold would delete a trap the real divide
takes. The interval domain supplies the missing proof — a range whose low end is
above `MININT` cannot contain it — and without that proof the divide is emitted
unchanged, trap included.

The saving is larger than the instruction count suggests. PB widens `\` to LONG
and calls the software `rt_longdiv` (shift-and-subtract; there is no hardware
IDIV in the image for this at all), so folding the last reference to it lets
Tier 3 trim the whole routine — about 500 bytes on a small program.

`Tests/CodeGen/DivideByMinusOneTests.cs` measures that by image size against an
A/B pair differing only in whether the operand is provable. It does so because
two byte-scans were tried first and both measured nothing: a vanished `IDIV`
(never there), and the LONG negate the fold leaves behind (also inside
`rt_longdiv`, which negates its own operands to divide signed).
