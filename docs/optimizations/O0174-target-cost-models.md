# O0174 — Per-target cost models

| | |
|---|---|
| **Status** | ⬜ Planned — **the prerequisite for most of the other planned passes** |
| **Stage** | Compiler infrastructure |
| **Related** | [O0078](O0078-multiply-decomposition.md), [O0092](O0092-encoding-selection.md), [O0129](O0129-unroll-factor-cost-model.md), [O0147](O0147-vector-width-cost-model.md), [O0177](O0177-cycle-estimate-battery.md) |

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

## What it needs

- A per-target table: instruction latencies and throughputs, encoding lengths,
  cache and prefetch parameters, register file, alignment penalties, branch
  behavior, available instruction sets.
- A **query interface** the passes call rather than hard-coded thresholds —
  `Cost.Of(instruction)`, `Cost.UnrollFactor(loop)`, `Cost.PreferBranchless(...)`.
- Wiring to the existing `$CPU` metastatement, which already declares the target
  floor (`8086`, `80386`, `80486`, `80586 [MMX|SSE|AVX|AVX512]`), plus
  `$OPTIMIZE SIZE|SPEED` as the objective selector.
- A test architecture that can express the difference —
  [O0177](O0177-cycle-estimate-battery.md).
