# O0016 — Value-fact analysis (intervals, known bits, congruences)

| | |
|---|---|
| **Status** | ✅ Implemented (reduced product; storage narrowing is [O0057](O0057-storage-narrowing.md)) |
| **Stage** | Pre-emission lattice consulted by the emitter |
| **Source** | `CodeGen/IntervalRange.cs`, `CodeGen/ValueFactReduction.cs`, `CodeGen/KnownBits.cs`, `CodeGen/CodeGenerator.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `IntervalRangeTests`, `ValueFactReductionTests`, `ValueFactRangePropagationTests`, `DIFF77/78/79/80/87/89/92/93/112.BAS`, `SelfDifferentialTests` |
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

`ValueFacts` carries all three as a **reduced product**. Facts flow in both
directions: a range can prove high bits zero; known bits can recover a tighter
range; a power-of-two congruence fixes low bits; and known low bits imply a
power-of-two congruence. Reduction repeats to a local fixpoint.

Every consumer asks whichever domain settles its question, and
`Allows(candidate)` folds an equality as soon as **any** domain excludes the
value.

**This page covers the lattice itself** — the domains, their transfer functions
and the joins a branch merge and a loop fixpoint need. What the facts are *used
for* is a set of separate entries (see *Split into* above): bounds, overflow and
divide-guard elimination, comparison folding, operation narrowing, identity and
constant-result removal, and keeping a bounded multiply off the FPU.

The fixed-width transfer functions cover `+ - *`, `AND OR XOR EQV IMP NOT`,
shifts and rotates, comparisons, and the supported `\`/`MOD` cases.

## Range-propagation invariant

A variable with a finite tracked range does not silently become range-unknown
merely because it participates in another modeled fixed-width calculation.

For `+`, `-`, `*`, bitwise operators, shifts, rotates and unary `NOT`/negation:

1. compute the tight mathematical/result interval when the interval domain can
   express it;
2. if fixed-width wrapping or a non-convex bit transformation prevents that
   precise interval from being represented, retain the finite range of the
   result type instead of dropping to `Top`;
3. feed known-bit and congruence facts back into that fallback range, often
   tightening it substantially or even recovering the exact wrapped value.

For example, a tracked signed INTEGER in `[-1,0]` passed through `XOR 4` cannot
be represented by one useful convex input-to-output mapping across the sign
boundary. O0016 nevertheless retains `[-32768,32767]` for the result; a later
`AND 7` immediately tightens that to `[0,7]`.

Likewise `INCR`/`DECR` use the same reduced-product arithmetic transfer as an
ordinary `+`/`-` expression, so `INCR` of the exact INTEGER value `32767`
wraps to an exactly tracked `-32768` rather than becoming unknown.

`\` and `MOD` deliberately do **not** use the generic type-range fallback when
their dedicated interval transfer fails. A divisor range may include zero, so
pretending that a successful result range was proven would hide an observable
runtime fault. They retain a finite result only when their own transfer proves
one safely.

A narrowing/wrapping integral store follows the same rule: if its source had a
finite range, the destination still has at least the destination type's finite
range, after which bits/congruences may tighten it.

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

- **Every value range describes runtime values, not an unchecked mathematical
  intermediate.** When a precise arithmetic hull leaves the fixed-width type,
  O0016 does not keep that invalid hull. It either recovers the wrapped result
  from other domains or falls back to the whole finite result type. Consumers
  therefore never mistake `60000` for the value of a wrapped INTEGER; with
  exact bits, `30000 + 30000` is recovered as `-5536`.
- The range-closure fallback never creates precision from an unknown input. It
  applies only when the relevant input ranges were already finite and the
  operation is fixed-width. Unknown inputs remain unknown unless the operation
  itself imposes a result bound, such as `x AND 7`.
- `NarrowRangeOf` is stricter than the general value-range query: to *replace*
  an operation every node of the operand tree must fit a word, not merely the
  result.
- Bit facts survive two's-complement wrapping (wrapping is arithmetic modulo
  2ⁿ, which leaves the low n bits where they were), so they need no
  dialect-dependent exactness proof. Congruences survive wrapping only where
  their modulus remains valid; unsafe congruence information is discarded.
- Width-aware comparisons reduce each operand in its own integer width. A LONG
  or DWORD operand is never truncated to the 16-bit PB boolean result width
  merely because the comparison itself produces `0` or `-1`.
- A discarded operand must be **discardable** — a plain variable read or a
  constant. Anything else could call a `FUNCTION` or index an array whose bounds
  check is observable.
- A call invalidates only what a callee can reach (module data, `STATIC`/
  `SHARED`, this frame's parameters, and every variable the statement names),
  and only while the body takes no address, stores through no pointer, runs no
  inline asm and declares no capturing lambda.

## References

The design was cross-checked against LLVM's `ConstantRange`/value-tracking model,
where binary operators map operand ranges to result ranges, and against GCC's
VRP description, which explicitly propagates ranges of values rather than only
constants. No implementation code was copied; O0016 is independently implemented
for PB's fixed-width and dialect-specific semantics.
