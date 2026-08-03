# O0080 — Division and modulo special cases

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0004](O0004-strength-reduction.md), [O0016](O0016-value-fact-analysis.md), [O0056](O0056-reciprocal-division.md) |

## The idea

Divisors the hardware never needs to see:

| Expression | Result |
|---|---|
| `x \ 1` | `x` |
| `x MOD 1` | `0` |
| `x \ -1` | `-x` (with the `MININT` caveat) |
| `x MOD -1` | `0` |
| `x \ k` where the facts prove `|x| < |k|` | `0` |
| `x MOD k` where the facts prove `0 <= x < k` | `x` (already done — [O0016](O0016-value-fact-analysis.md)) |

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

## What it needs

- The **sign proof** comes from the interval domain
  ([O0016](O0016-value-fact-analysis.md)) — it is already computed, just not
  consulted for the general `MOD 2^n` lowering.
- `x \ -1` must preserve the `MININT \ -1` behavior exactly (a genuine trap on
  the hardware path); it is the one case where the "cheap" rewrite is not
  automatically legal.
