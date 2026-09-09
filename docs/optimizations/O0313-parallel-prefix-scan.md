# O0313 — Parallel prefix scan

| | |
|---|---|
| **Status** | 🟡 Partial — integer scan recognition/SSA formation implemented; hosted worker splitting remains dependent on [O0311](O0311-parallel-loop-versioning.md) |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/ParallelPrefixScan.cs` |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/ParallelPrefixScanTests.cs` |
| **Related** | [O0119](O0119-reduction-recognition.md), [O0312](O0312-parallel-reduction.md), [O0134](O0134-recurrence-shortening.md), [O0172](O0172-loop-dependence-analysis.md) |

## The idea

A cumulative sum — `t(i) = t(i-1) + a(i)` — looks strictly sequential, but it is
a **scan**, and a scan parallelizes in two passes: each worker computes the
reduction of its slice, the slice offsets are combined, then each worker applies
its offset while computing its local scan.

## Applies to

```basic
DIM i%, t&(0 TO 999999), a&(0 TO 999999)
FOR i% = 1 TO 999999
  t&(i%) = t&(i% - 1) + a&(i%)
NEXT
```

## Implemented IR layer

`ParallelPrefixScan` recognizes the straight-line counted-loop form through
[O0172](O0172-loop-dependence-analysis.md). The store of `t(i)` must be the
source of the loop's **only** carried memory dependence, the read of `t(i-1)`
must be its sink, and the dependence distance must be exactly one.

For fixed-width integer `+`, `*`, `AND`, `OR`, and `XOR`, the pass replaces the
per-iteration previous-element load with a loop-carried SSA phi:

```text
before                              after
------                              -----
previous = load t[i-1]              scan = phi(seed, next)
next = previous + a[i]              next = scan + a[i]
store next, t[i]                    store next, t[i]
```

`seed` is not an invented identity. It is one preheader load of the exact memory
location that the original first iteration would have read; the loop counter in
that address expression is replaced by its proven initial value. Every arithmetic
operation and every intermediate store therefore remains in sequential order.
The transformation removes one dependent memory load per iteration while making
the scan recurrence explicit for later vector or hosted parallel lowering.

The pass declines when O0172 is incomplete, when another carried memory edge is
present, when the previous-element load has additional memory ordering constraints,
or when the address cannot be materialized safely in the preheader.

## Exact arithmetic semantics

The implemented operators are associative over PB's fixed-width integer bit
patterns. In particular, integer addition and multiplication retain modular wrap;
forming the phi does **not** reassociate or remove any per-element operation, so
every stored prefix value is unchanged even when intermediate arithmetic wraps.

Floating-point scans and subtraction are deliberately rejected. A future worker
split would reassociate their operations, which is not valid under strict PB
floating-point semantics and is not algebraically valid for subtraction.

## Remaining hosted layer

Actual two-pass worker execution is intentionally not synthesized here. That is
the hosted loop-versioning/runtime responsibility described by
[O0311](O0311-parallel-loop-versioning.md): it needs explicit opt-in, a host
threading runtime, and a profitability threshold. Real-mode DOS remains
single-tasking, so manufacturing thread machinery in this target-neutral pass
would make the core compiler worse in order to pretend the hosted backend already
has infrastructure it does not.

When O0311 exists, it can consume the explicit scan phi produced here and split
it into local scans plus slice offsets without rediscovering the memory recurrence.

## References

- OpenMP 5.2, §5.6 `scan`: normative inclusive/exclusive scan semantics and the
  restriction that cross-iteration dependences are limited to scan list items.
- Guy E. Blelloch, *Prefix Sums and Their Applications* (CMU-CS-90-190, 1990):
  all-prefix-sums definition and parallel scan construction.
- LLVM loop-vectorization legality/recurrence analysis: reference architecture
  for classifying loop-carried recurrences separately from arbitrary dependences.

No implementation code was copied or translated from those sources; the C# pass
is original and uses PB-Compiler's existing `CountedLoop` and O0172 analyses.
