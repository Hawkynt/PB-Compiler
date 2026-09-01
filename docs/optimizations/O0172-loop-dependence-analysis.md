# O0172 — Loop dependence analysis

| | |
|---|---|
| **Status** | 🟨 Partial — exact equal-stride affine dependences plus GCD/interval disproval are implemented; general SIV/MIV and nested-loop direction vectors remain |
| **Stage** | Analysis infrastructure |
| **Source** | `Ir/Analysis/IrLoopDependenceAnalysis.cs` |
| **Related** | [O0171](O0171-alias-analysis.md), [O0122](O0122-loop-interchange.md), [O0123](O0123-loop-distribution.md), [O0026](O0026-auto-vectorization.md) |

## The idea

Whether two array accesses from **different iterations** can touch the same
bytes — and if so, in which direction — is the fact that decides whether a loop
may be vectorized, interchanged, distributed, fused, tiled or skewed. It is the
analysis layer that keeps those transformations from substituting a syntactic
"looks independent" test for a legality proof.

The classical machinery expresses each address as an affine function of the
loop counters and then solves or disproves the resulting integer equations. The
implemented first layer uses the same hierarchy deliberately:

1. ask [O0171](O0171-alias-analysis.md) whether the underlying objects are
   already disjoint;
2. recognize a wrap-free byte address of the form
   `base + stride * iteration + constant`;
3. solve **equal-stride** pairs exactly, including the byte width of each load
   or store;
4. for unequal strides, use the **GCD test** and the bounded coefficient
   interval (the one-dimensional Banerjee bound) to disprove impossible
   equations;
5. if those cheap tests still admit a solution, report the result as
   **incomplete** rather than guessing.

That last point is part of the API contract: a consumer may use known
`Flow`/`Anti`/`Output` dependences for diagnostics or costing, but may only infer
independence from an empty dependence list when `IsComplete` is true.

## Applies to

```basic
DIM i%, a%(0 TO 999)
FOR i% = 1 TO 999
  a%(i%) = a%(i% - 1) + 1     ' distance-1 flow dependence
NEXT

FOR i% = 0 TO 998
  a%(i%) = a%(i% + 1) + 1     ' distance-1 anti-dependence
NEXT
```

For the first loop the store in iteration `i-1` supplies the load in iteration
`i`, so the analysis records a loop-carried `Flow` dependence with distance 1.
For the second, the load must precede the store performed by the next iteration,
so it records an `Anti` dependence with distance 1. A future vectorizer may have
a legal strategy for some anti-dependences; the important point is that the
middle end now describes the constraint instead of erasing it.

## Width matters

The analysis works on byte ranges, not merely element starts. For example, a
16-bit store through `base + i` overlaps the same store from the next iteration:

```text
iteration i:       [ byte i ][ byte i+1 ]
iteration i+1:              [ byte i+1 ][ byte i+2 ]
```

That is a distance-1 output dependence even though the two start addresses are
different. This is the same correctness boundary that made the shared O0171
alias oracle width-aware.

## What is recognized

The loop itself currently uses the repository's shared `CountedLoop` matcher:
one canonical integer phi, constant start/step/limit, one preheader and one
latch. Within it, an address expression may contain:

- the canonical counter;
- integer constants;
- `ADD` / `SUB`;
- multiplication by a loop-invariant constant;
- left shift by a constant;
- signed extension;
- truncation only when the whole value range proves that no bit changes.

Every supported intermediate arithmetic instruction must stay inside its signed
IR type for every iteration. If `i * 4000` can wrap an `i16`, it is **not** treated
as the mathematical affine function `4000*i`; the pair stays unknown. This is
required because PB integer wrapping is observable and a wrapped subscript does
not obey the affine equation the dependence test would otherwise solve.

## Current limits

- Unequal-stride equations that survive GCD and interval disproval are not yet
  solved exactly. Strong/weak SIV and general MIV tests are the next precision
  layer.
- Only one loop level is analyzed today. The public direction enum already has
  `<`, `=`, and `>` components so nested-loop direction vectors can extend the
  result without changing consumers.
- A pointer-producing instruction executed inside the loop is conservatively
  considered loop-varying. This means dynamic-array bases loaded from a
  descriptor inside the body remain unknown until pointer-value invariance is
  proved or represented explicitly.
- Calls are a memory wall except for the existing checked pure external math
  intrinsic list from O0161 function summaries.
- Equal-address dependences between instructions in different basic blocks are
  conservatively incomplete because path-sensitive same-iteration statement
  order is not yet represented.

## Why this comes before interchange / tiling

[O0122](O0122-loop-interchange.md), [O0124](O0124-loop-tiling.md), loop
distribution/fusion and an IR vectorizer all need the same answer: whether
changing iteration order changes an observable memory order. Keeping the answer
in one analysis prevents each transformation from growing its own subtly
different alias/dependence matcher.

## Reference model

LLVM's `LoopAccessAnalysis` likewise separates memory legality from
profitability, uses underlying-object alias information before dependence
reasoning, and leaves unresolved pointer relations to conservative handling or
runtime checks. LLVM's general `DependenceAnalysis` contains the classical GCD,
SIV and Banerjee-family tests. Those sources were used as behavioral/design
references only; this implementation is independently written for PB-Compiler's
IR and its wrapping semantics.
