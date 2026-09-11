# O0338 — Reciprocal reuse across repeated divisions

| | |
|---|---|
| **Status** | ✅ Implemented — strict exact reciprocals, `arcp` reuse, guarded canonical-loop hoisting, target cost hook, and F80/x87 support |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/ReciprocalSequenceReuse.cs`, `Ir/Passes/ReciprocalLoopHoisting.cs` |
| **Gate** | `--optimize`; non-exact/runtime reciprocal reuse additionally requires `$OPTIMIZE SPEED` / `-OZF` or an explicit `AllowReciprocal` flag |
| **Verified by** | `ArithmeticIdiomOptimizationTests`, `ReciprocalLoopHoistingTests`, `ReciprocalCostPipelineTests`, `TargetCostTests` |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0174](O0174-target-cost-models.md), [O0341](O0341-reciprocal-approximation.md), [O0345](O0345-common-denominator-factoring.md) |

## The idea

Dividing repeatedly by the **same invariant** value can compute or reuse a reciprocal and multiply instead.
That is worthwhile only when two independent questions both answer yes:

1. is `x / d -> x * (1/d)` numerically legal for this operation, and
2. is one divide plus the resulting multiplies cheaper on the selected target than the original divides?

O0338 keeps those questions separate. Strict arithmetic gets only provably exact constant reciprocals;
relaxed arithmetic consumes `AllowReciprocal`, and a target may additionally supply O0174 profitability data.

## Strict exact reciprocals

Repeated F32, F64, or F80 division by the same finite nonzero power-of-two constant becomes multiplication
by its exact reciprocal. No fast-math permission is required because both the divisor and reciprocal are
exact binary powers of two.

```basic
a! = x! / 8!
b! = y! / 8!
```

becomes the IR equivalent of two multiplies by `0.125`.

`IrConstantFloat` currently stores its payload in a .NET `double`, including when typed F80. Therefore the
strict F80 path covers the full set of power-of-two constants representable by the current IR literal payload;
the resulting value is exact when embedded in x87 extended precision. This is a literal-representation limit,
not an O0338 numerical approximation. Dynamic F80 values do not have that limitation.

## `arcp`-authorized runtime reuse

When an `FDiv` carries `IrFastMathFlags.AllowReciprocal`, O0338 may form one runtime `1/d` and use it for
multiple divisions. This applies to F32/F64/F80, non-power-of-two constants, and dynamic SSA divisors.

Within an ordinary CFG region, the first reused division must dominate every later one and every path between
them must be call-free. That prevents the pass from speculating a reciprocal into a sibling branch or moving it
across a call that may disturb the floating-point environment. If the dominating operation is already `1/d`,
that value is retained instead of creating another division.

Bit-identical constants are grouped even when lowering produced different `IrConstantFloat` objects; dynamic
divisors require the same SSA value. Generated multiplies inherit arithmetic fast-math freedoms but not
`arcp`/approximate-function-only flags.

LLVM's `arcp` contract is the normative behavioral boundary: it permits treating `a / b` as `a * (1.0 / b)`
and permits the resulting form to participate in code motion. No LLVM implementation code is copied.

## Guarded/lazy loop hoisting

A shared reciprocal inside a loop is still one divide **per iteration**. Ordinary LICM cannot simply move it to
the preheader because a zero-trip loop would then execute `1/d` even though the original program executed no
division.

For the canonical loop shape, O0338 instead clones the loop-entry comparison into the preheader:

```text
preheader:
  if !entry-test -> exit
  else -> recip.init

recip.init:
  r = 1/d
  -> header

header:
  original loop test
  ...
```

The reciprocal is evaluated only on the edge that actually enters the loop. Header phi entry edges are renamed
from the old preheader to `recip.init`, so SSA remains valid. The current transform deliberately requires:

- one natural-loop header and one outside preheader,
- one header-controlled exit and no other loop exits,
- a header consisting of phis plus the comparison/conditional branch,
- no calls in the loop,
- an invariant divisor available before the preheader,
- the shared reciprocal to originate as the first real operation in the body block selected directly by the header,
- no exit phis and no loop-header phi values used outside the loop.

The direct-body requirement matters independently of the zero-trip guard. A reciprocal originally inside an
inner `IF` must not be evaluated merely because the loop entered; such a conditional sequence may still share
one reciprocal inside its dominated arm, but it is not preheader-hoisted.

Those restrictions make the CFG rewrite transactional and avoid inventing broad SSA-repair machinery merely to
force a match. More complicated multi-exit or live-out loops are a different transform, not a silently weaker
proof.

## Target-specific profitability

`IIrArithmeticCostModel` is the target-neutral interface consumed by O0338. O0174's `TargetCost` implements it
using representative x87 generation costs and compares:

```text
N * FDIV    versus    FDIV + N * FMUL
```

under the SPEED objective. The resulting conservative break-even points are intentionally target-dependent:

| Target tier | Representative FMUL | Representative FDIV | Minimum repeated divides |
|---|---:|---:|---:|
| 8087 / 80287 class | 145 | 203 | 4 |
| 80387 class | 57 | 91 | 3 |
| 80486 class | 16 | 73 | 2 |
| Pentium / P6 class | 3–4 | 39 | 2 |

The figures are cost-model inputs, not cycle-exact promises for every stepping or operand class. Their purpose is
to preserve the historically important ordering: reciprocal reuse that clearly wins on an integrated 486/Pentium
FPU need not win for only two divisions on an early discrete coprocessor. Size/balanced objectives decline the
non-exact widening rewrite.

For a guarded-hoist candidate, O0338 composes that target query with the repository's shared `CountedLoop`
analysis. If the static sequence loses but the trip count is exact, profitability may be retried with the number
of divisions eliminated over the complete loop: e.g. two divisions over four proven iterations are priced as
eight original `FDIV`s versus one hoisted `FDIV` plus eight `FMUL`s. This amplification is used only when each
priced division's block dominates the unique latch, proving it executes on every iteration; conditional arms are
not assigned imaginary executions.

Unknown-trip loops can still be guarded-hoisted after a statically profitable reuse, but an unknown trip count is
never guessed merely to overturn a target cost refusal.

`IrPassManager.Standard` accepts the arithmetic cost model explicitly, and the routed x86 pipeline passes its
`SelectionCost` under `$OPTIMIZE SPEED`. Target-neutral/hosted callers that do not have a model retain the legal
transform once SPEED supplied `arcp`.

## Interaction with O0345

O0345 handles the local common-denominator form under SPEED. O0338 runs later, after LICM, so invariant divisor
computations are already exposed and reuse can extend across dominated blocks or be guarded-hoisted out of a
canonical loop.

## Deliberate boundaries

- Native 80-bit literal payloads beyond binary64's exponent/significand range require a richer IR constant type;
  runtime F80 reciprocal reuse itself already supports x87 extended values.
- Multi-entry/multi-exit loops, inner-conditional reciprocal origins, and loops with live-out header phis are
  declined for hoisting rather than repaired/speculated.
- Exact target timings can be refined as individual backend CPU models become more detailed; the legality proof is
  independent of those estimates.
