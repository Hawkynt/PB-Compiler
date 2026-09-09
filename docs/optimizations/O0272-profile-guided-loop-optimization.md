# O0272 — Profile-guided loop optimization

| | |
|---|---|
| **Status** | 🟨 Partial — IR trip histograms + dominant-small-trip peeling implemented; collection, vector-width policy and bimodal versioning remain |
| **Stage** | Mid-end policy |
| **IR** | ✅ `Ir/IrProfileMetadata.cs`, `Ir/Passes/ProfileGuidedLoopOptimization.cs` — distribution-aware guarded prefix peeling; wired into the early loop/unroll slot |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/ProfileGuidedLoopOptimizationTests.cs` |
| **Related** | [O0129](O0129-unroll-factor-cost-model.md), [O0130](O0130-trip-count-versioning.md), [O0268](O0268-profile-collection.md) |

## The idea

Unroll factors, vector widths, peeling decisions and loop versioning are all
guesses without trip-count data. A **distribution** — not just an average —
answers them properly:

| Observed trips | Right answer |
|---|---|
| almost always 0 or 1 | do not unroll; consider peeling the guard |
| a small constant | unroll fully ([O0007](O0007-loop-unrolling.md)) |
| large and variable | vectorize with a runtime tail |
| bimodal | version the loop ([O0130](O0130-trip-count-versioning.md)) |

## Implemented IR slice

`IrLoopTripCountProfile` carries the observed histogram itself rather than an
average. `IrProfileMetadata` attaches it to a loop header as side metadata: the
profile affects profitability, never SSA semantics, and clones do not inherit it.
This representation is intentionally independent of O0268's future instrumentation
and profile-file format.

For a canonical SSA loop, O0272 currently acts when:

- at least 32 loop invocations were observed,
- one non-zero trip count owns at least 90% of the samples,
- that dominant count is at most 8, and
- peeling the prefix stays within a 96-instruction growth budget.

It then emits that many **guarded** body copies before the original loop. Every
copy repeats the real loop test. A guard that fails enters the original header,
and after the peeled prefix the untouched loop handles all remaining iterations.
The profile is therefore only a speed hint: an accurate, stale, or flat-out wrong
histogram produces the same result. The tests deliberately run a profile claiming
three trips against loops that actually execute 0, 1, 3 and 12 iterations.

A broad/bimodal distribution is declined instead of optimizing its average. For
example, 50% zero-trip plus 50% eight-trip has mean four, but O0272 correctly does
**not** treat it as a four-trip loop.

## Applies to

```basic
FOR i% = 0 TO n%             ' n% is 3 in 90% of runs and 30 000 in the rest
  ...
NEXT
```

— exactly the case where one static choice is wrong for one of the two
populations.

## Remaining work

- Populate the IR metadata from real profile collection/loading ([O0268](O0268-profile-collection.md)).
- Feed distributions into the general unroll-factor/vector-width cost model ([O0129](O0129-unroll-factor-cost-model.md)).
- Version genuinely bimodal loops once the runtime versioning machinery exists ([O0130](O0130-trip-count-versioning.md)).
- Use hotness to gate loop-top alignment and other target-specific choices.

## Reference behaviour

The implementation is original and uses the profile only as a profitability oracle.
The policy was cross-checked against LLVM's profile-aware loop peeling/unrolling and
estimated-trip-count metadata, and GCC's documented profile-use loop optimizations.
No third-party implementation code was copied or translated.
