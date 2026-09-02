# O0359 — Verified arithmetic lowering

| | |
|---|---|
| **Status** | ✅ Implemented (16-bit verified families) |
| **Stage** | Mid-end before target lowering |
| **Related** | [O0056](O0056-reciprocal-division.md), [O0078](O0078-multiply-decomposition.md), [O0355](O0355-superoptimized-peepholes.md) |

## The idea

Constant multiply, divide and modulo sequences are exactly the place where a
clever lowering is most tempting and most dangerous. Each formula is correct
only for a specific width, wrap rule and quotient/remainder convention, so the
compiler should not trust an attractive identity until it has mechanically
checked it.

`Ir/Passes/VerifiedArithmeticLowering.cs` implements that rule for the complete
16-bit domain. A candidate formula is cached only after all **65 536** input bit
patterns agree with the original operation.

Implemented families are:

- multiplication by `±(2^k ± 1)`, lowered to shift/add-or-subtract and optional
  negate;
- signed division by `±2^k`, using the truncation-toward-zero bias followed by an
  arithmetic shift;
- signed remainder by `±2^k`, derived as `x - quotient * divisor` from the same
  verified quotient.

The simple `0`, `±1` and pure-power-of-two multiply cases remain with ordinary
`InstCombine`; this pass owns formulas whose correctness is less obvious.

## Safety and limits

- Verification enumerates every 16-bit input before admitting a multiplier or
  divisor plan and the verified-plan caches are synchronized for concurrent
  compilation.
- Signed division by `-1` is deliberately not rewritten: `-32768 / -1` must keep
  its overflow behavior instead of becoming an unconditional negate.
- Division/remainder by zero are never candidates.
- The implementation currently covers 16-bit integer arithmetic only. Wider
  magic-number division should use an SMT/independently verified generator rather
  than extrapolating these formulas by hand.
- The lowering runs in the optimized IR pipeline; `$OPTIMIZE OFF` retains the
  original arithmetic operation.
