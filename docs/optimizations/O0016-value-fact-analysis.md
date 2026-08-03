# O0016 — Value-fact analysis (intervals, known bits, congruences)

| | |
|---|---|
| **Status** | ✅ Implemented (three domains, five consumers); storage narrowing is [O0057](O0057-storage-narrowing.md) |
| **Stage** | Pre-emission lattice consulted by the emitter |
| **Source** | `CodeGen/IntervalRange.cs`, `CodeGen/KnownBits.cs`, `CodeGen/CodeGenerator.cs` (`IndexRangeOf`, `NarrowRangeOf`, `TryEmitFactRedundantOp`, `ProvablyNoOverflow`, `DivisorNonZero`) |
| **Gate** | `--optimize` |
| **Verified by** | `DIFF77/78/79/80/87/89/92/93/112.BAS`, `SelfDifferentialTests`, scenarios `BoundedMultiplyStaysOffTheFpu` |
| **Related** | [O0017](O0017-sccp.md), [O0013](O0013-promotion-lowering.md), [O0057](O0057-storage-narrowing.md) |
| **Split into** | [O0217](O0217-bounds-check-elimination.md), [O0218](O0218-range-comparison-folding.md), [O0219](O0219-overflow-check-elimination.md), [O0220](O0220-divide-guard-elimination.md), [O0221](O0221-operation-narrowing.md), [O0222](O0222-identity-operation-removal.md), [O0223](O0223-constant-result-folding.md), [O0224](O0224-bounded-multiply-off-fpu.md) |

## What it is

Three abstract domains are carried per value, because an interval is only one
way to be ignorant:

| Domain | Knows | The question only it answers |
|---|---|---|
| `Interval` | `[lo, hi]` | `x MOD 2` is never 2 |
| `KnownBits` | which bits are always 1 / always 0 | `(x \ 2) * 2` is never 1; `x AND 12` is never 5 |
| `Congruence` | `v ≡ r (mod m)` | `x * 10` is never 25 |

`ValueFacts` carries all three; every consumer asks whichever one settles its
question, and `Allows(candidate)` folds an equality as soon as **any** domain
excludes the value.

**This page covers the lattice itself** — the domains, their transfer functions
and the joins a branch merge and a loop fixpoint need. What the facts are *used
for* is a set of separate entries (see *Split into* above): bounds, overflow and
divide-guard elimination, comparison folding, operation narrowing, identity and
constant-result removal, and keeping a bounded multiply off the FPU.

The transfer functions cover `+ - *`, `AND OR XOR NOT`, the shifts, and
`\`/`MOD` by a constant.

## Sample

```basic
$ERROR BOUNDS ON
DIM a%(0 TO 99), i%, h%
FOR i% = 0 TO 99
  a%(i%) = i%
NEXT
h% = a%(h% AND 63)
PRINT (a%(0) \ 2) * 2 = 1        ' provably FALSE, whatever a%(0) is
```

## Without the optimizer

```asm
    ; per array access, under $ERROR BOUNDS
    mov     ax, [i]
    cmp     ax, 0000h
    jl      rt_err_arr
    cmp     ax, 0063h
    jg      rt_err_arr
    ...
    ; and the comparison is really computed
```

## With the optimizer

```asm
    ; the counter's range is [0,99] and the array's is [0,99]: no check at all
    ...
    ; h% AND 63 is bounded to [0,63], inside [0,99]: no check
    ...
    xor     ax, ax           ; the comparison folded to FALSE (bit 0 is always 0)
```

## Equivalent BASIC

```basic
$ERROR BOUNDS OFF            ' but only for the accesses that provably cannot fail
...
PRINT 0                      ' the impossible comparison
```

## Why it is safe

- **Every node's range is checked against its own type.** Composing
  mathematical ranges through a wrapped intermediate is a fiction on the
  dialects that wrap in place (QuickBASIC, Turbo Basic, `$COMPAT`), where
  `(i% + i%) \ 6000` with `i% = 20000` really computes `-25536 \ 6000`. A
  wrapped intermediate yields "unknown", and every consumer inherits the fix.
- `NarrowRangeOf` is stricter than `IndexRangeOf`: to *replace* an operation
  every node of the operand tree must fit a word, not merely the result.
- Bit facts survive two's-complement wrapping (wrapping is arithmetic modulo
  2ⁿ, which leaves the low n bits where they were), so they need no
  dialect-dependent exactness proof. Congruences survive it only for
  power-of-two moduli and are dropped with the range otherwise.
- A discarded operand must be **discardable** — a plain variable read or a
  constant. Anything else could call a `FUNCTION` or index an array whose bounds
  check is observable.
- A call invalidates only what a callee can reach (module data, `STATIC`/
  `SHARED`, this frame's parameters, and every variable the statement names),
  and only while the body takes no address, stores through no pointer, runs no
  inline asm and declares no capturing lambda.
