# O0174 — Per-target cost models

| | |
|---|---|
| **Status** | 🟡 Partial (the model and query interface exist — `TargetCost` — and live passes consume them as target-sensitive transforms land) — **the prerequisite for most of the other planned passes** |
| **Stage** | Compiler infrastructure |
| **Related** | [O0078](O0078-multiply-decomposition.md), [O0092](O0092-encoding-selection.md), [O0129](O0129-unroll-factor-cost-model.md), [O0147](O0147-vector-width-cost-model.md), [O0177](O0177-cycle-estimate-battery.md), [O0338](O0338-reciprocal-sequence-reuse.md) |

## The idea

An optimization is only an optimization **on a particular machine**. The targets
this compiler can emit for span four decades of microarchitecture, and their
rules contradict each other:

| Target | What actually costs | What is free |
|---|---|---|
| 8086 | instruction **bytes** (the 4-byte prefetch queue), memory accesses, taken branches, effective-address computation, `MUL`/`DIV` | independent work between a load and its use — the bus unit is the bottleneck |
| 80286/386 | as above, plus 32-bit ops become available | wider moves, hardware divide |
| 486 | microcoded instructions (`LOOP`, `ENTER`, `XLAT`), misaligned access | simple ops (1 cycle), aligned wide moves |
| Pentium | U/V pipe pairing, AGI stalls, partial-register writes | paired simple instructions |
| P6+ | micro-op count, decode width, false dependencies, branch misprediction | register-to-register moves (eliminated in rename) |
| SIMD targets | vector transitions, frequency throttling, shuffle chains | packed lane arithmetic |

Concretely: 8-bit sub-register packing ([O0058](O0058-386-register-allocation.md))
is a **win on an 8086 and a stall on a P6**; keeping a `CMP` adjacent to its
branch ([O0109](O0109-macro-fusion-placement.md)) is **required on a Core and
counterproductive on an 8086**, where filling that slot with independent work is
exactly right. Without a cost model, an optimization tuned for one is actively
dreadful on the other.

## Now

`TargetCost` (`CodeGen/TargetCost.cs`) is the model. A `CpuTier` (8086 → P6) and
a `CostObjective` (`Balanced`/`Size`/`Speed`) are derived from the `$CPU` floor
and `$OPTIMIZE` objective by `TargetCost.For(...)`; the code generator exposes it
as the `Cost` property. It answers the profitability questions the gated passes
need instead of a hard-coded tier threshold, and encodes the contradictions the
doc opens with:

- `PreferBranchless(armBytes, sideEffectFree)` — false on the predictor-less
  8086/286/386 (a not-taken branch is nearly free), true on a pipelined core only
  when the arms are small enough that running both beats a misprediction; never
  under a size objective. Backs O0094/O0108/O0248.
- `SubRegisterPackingProfitable` — true below the P6, false on it (the
  partial-register merge stall). Backs O0058.
- `MacroFusionMatters` — true from the Pentium, false earlier. Backs O0109.
- `Mul16Cycles`/`Div16Cycles`/`ShiftAddCycles` — the latencies that price the
  multiply/divide-decomposition rewrites (O0078/O0056) per tier.
- `X87MultiplyCycles`/`X87DivideCycles` and `PreferReciprocalReuse(type, count)` —
  representative x87-generation costs and the O0338 break-even query. The routed
  x86 SPEED pipeline passes this model into the middle end through
  `IIrArithmeticCostModel`; exact power-of-two reciprocal rewrites do not need a
  profitability permission because they preserve strict semantics and replace
  FDIV by the cheaper FMUL on every modelled tier.

As of 2026-09-08, live consumers include `AlignHotLoops`, `MaxFullUnrollTrips`,
`PreferShiftAddMultiply`, and `PreferReciprocalReuse`. The x87 cycle properties
feed the reciprocal query internally rather than being second pass-level policy
entry points. Other answers below remain ready for their planned consumers.

Read the "backs O00xx" lines above as *what each answer is for*, not as wiring that
necessarily exists — the passes they name (O0058's sub-register packing, O0109's
fusion placement, the branchless halves of O0094/O0108/O0248, the partial-unroll
factor of O0129) bring their own consumer when they land. The model is not the
blocker it is sometimes taken for: it is built, tested (`TargetCostTests`) and
ready.

One member is deliberately not consulted where it might look like it should be. The
branchless ABS that ships (`TryEmitBranchlessAbsIf`, O0249) does not ask
`PreferBranchless`, and should not: that predicate prices general if-conversion,
where running both arms buys you a mispredict you might not have paid, whereas
`cwd; xor ax,dx; sub ax,dx` is shorter *and* faster than the compare-and-branch it
replaces on every tier. An idiom that wins everywhere needs no cost question.
- `PreferLoopInstruction` — avoids the microcoded `LOOP` from the 486 up unless
  size-bound.
- `UnrollFactor(trip, bodyBytes)` — scales with the tier, clamps to the fetch
  window, and prefers a factor dividing the trip count. Backs O0129.
- `AlignHotLoops` — the 16-byte hot-loop pad asks the model (speed objective on a
  486+) instead of hard-coding `OptimizeSpeed && (Cpu486 || Cpu586)`.

O0338 also demonstrates why the cost interface belongs above individual machine
instructions. A pair of divisions can be below the reciprocal break-even on an
8087-class FPU, while the same pair inside a four-trip counted loop becomes eight
dynamic divisions and is profitable once guarded hoisting pays the reciprocal
only once. The mid-end combines the target query with its exact `CountedLoop`
proof; the target model itself stays ignorant of CFG shape.

The model emits nothing, so consulting it never changes output on its own — only
a pass acting on an answer does, and every such pass stays `Optimize`-gated.

## Still planned

- A finer encoding-length / micro-op table for [O0092](O0092-encoding-selection.md)
  and the decode-width scheduler ([O0245](O0245-decode-width-scheduling.md)).
- Cache and alignment penalty parameters for the access-merging family.
- A cycle-estimate battery to regression-test the numbers —
  [O0177](O0177-cycle-estimate-battery.md).
