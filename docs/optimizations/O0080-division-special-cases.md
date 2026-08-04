# O0080 — Division and modulo special cases

| | |
|---|---|
| **Status** | 🟡 Partial — every case folds except `x \ -1` (held back by its `MININT` trap) |
| **Stage** | Emitter |
| **Related** | [O0004](O0004-strength-reduction.md), [O0016](O0016-value-fact-analysis.md), [O0056](O0056-reciprocal-division.md) |

## The idea

Divisors the hardware never needs to see:

| Expression | Result | Status |
|---|---|---|
| `x \ 1` | `x` | ✅ folds unconditionally (÷1 never traps) |
| `x MOD 1` | `0` | ✅ (`facts.Mod.IsMultipleOf(1)`) |
| `x \ -1` | `-x` (with the `MININT` caveat) | ⬜ kept on `IDIV` — see below |
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

## Still open

- **`x \ -1 → -x`.** `NEG` cannot replace the `IDIV` unconditionally:
  `MININT \ -1` overflows `IDIV` (an `#DE` trap the genuine hardware path takes),
  whereas `NEG(8000h)` = `8000h` silently. Folding it needs either a proven
  `x ≠ MININT` (from the interval domain) or an explicit `MININT` guard, so it
  stays on `IDIV` for now — correct, just not reduced.
- **General `MOD 2^n` on a provably non-negative value** (`i% MOD 8 → i% AND 7`
  for `i%` in `[0,99]`) — the sign proof is already computed by
  [O0016](O0016-value-fact-analysis.md); today the mask lowering fires only for
  the parity shape and constant-bounded ranges, not the general non-negative
  case.

Native-only. The IR tier folds `\ 1`/`MOD 1`/`\ -1` itself
([O0043](O0043-ir-instcombine.md)), so the C/LLVM back ends already reduce them.
