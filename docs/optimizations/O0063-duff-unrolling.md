# O0063 — Duff's-device unrolling (variable-trip loops)

| | |
|---|---|
| **Status** | ✅ Implemented for canonical unit-stride integer loops |
| **Stage** | IR middle-end |
| **IR** | `Ir/Passes/LoopUnroll.cs` — factor-4 runtime unrolling: a remainder prologue plus a fourfold main loop |
| **Related** | [O0007](O0007-loop-unrolling.md), [O0026](O0026-auto-vectorization.md), [O0066](O0066-unrolled-counter-propagation.md) |

## What it does

[O0007](O0007-loop-unrolling.md) fully unrolls loops whose trip count is a small compile-time
constant. O0063 handles the complementary canonical `FOR` shape when the count is only known at run
time: `trips MOD 4` iterations run first in the original loop, and everything after that runs four
iterations per loop test.

The gain is the same one Duff's device is famous for — the per-iteration test and back edge
disappear for three iterations out of four — but it is reached without Duff's computed jump into the
middle of the body. That matters here rather than being a matter of taste: a shared entry gives one
cycle four entry points, and every pass downstream that reasons from a single loop header (LICM
above all) then declines the loop, so invariant work stays inside and is recomputed in each copy.
A remainder prologue keeps both cycles single-entry, and nothing after this pass has to change.

It pays exactly where a string intrinsic or target-specific repeat instruction cannot express the
body — per-element transforms, planar masks, strided writes, and similar scalar work.

## Applies to

```basic
$OPTIMIZE SPEED
DIM i%, n%, a%(0 TO 999)
FOR i% = 0 TO n%
  a%(i%) = a%(i%) * 3
NEXT
```

The implemented safe core accepts:

- integer counters,
- a loop-invariant run-time start and/or bound,
- `STEP 1` with `<=` (`SLE`/`ULE`) or `STEP -1` with signed `>=`,
- the canonical straight-line body/latch shape produced by `IrLowering`,
- body growth within the existing unroll instruction budget.

Constant-trip loops remain O0007's responsibility, including the ones O0007 itself declines as too
long to copy out; the test looks through the width casts the lowering wraps literals in, so a `LONG`
bound arriving as `sext i16 100 to i32` still counts as a constant. Non-unit modular steps
deliberately remain scalar:
a step larger than one can jump across the comparison boundary, wrap, and become true again, so the
usual mathematical quotient is not necessarily PB's machine-level trip count.

## IR shape (factor 4)

Conceptually the transformed loop is:

```text
preheader
    |
    v
zero-trip guard ---- false --------------------+
    |                                          |
   true                                        |
    v                                          |
setup: rem = trips AND 3                       |
       stop = init +/- rem                     |
    |                                          |
    v                                          |
 +-> prologue header: counter <> stop ?        |
 |      | true            | false              |
 |      v                 |                    |
 +-- original body        |                    |
                          v                    |
                +-> main header: original test |
                |      | true      | false     |
                |      v           +---------> exit merge -> original exit
                +--- body x4
```

The prologue is the original loop, unchanged except for its bound and where it goes when it is done.
Its `<>` test is exact: `rem` is at most three, so a counter stepping by one reaches `stop` on the
nose, which a relational test could not promise once the subtraction is allowed to wrap.

Both cycles keep one header and one entry. The merge block carries the loop-carried values out —
their initial values on the zero-trip edge, the main loop's final values otherwise — so everything
after the loop reads one definition regardless of which path ran.

## PowerBASIC semantics

The PB **increment-then-test counter end value** (QUIRK 2.28) is preserved exactly. The pass does not
replace the loop counter with a mathematical wide counter: every cloned increment remains in the
counter's original fixed width, including wrap.

For unit stride, the finite distance to the comparison boundary is exact. `trips MOD 4` is computed
in that same width; this is safe even at the signed edge because wrapping is modulo `2^bits` and
`2^bits` is divisible by four. A separate copy of the original header comparison guards the entry, so
a zero-trip loop still executes no body at all, and the main loop reuses the original comparison, so
it stops exactly where the loop always stopped.

## Validation

`PowerBasic.Compiler.Tests/Ir/LoopUnrollTests.cs` covers:

- all four runtime remainders plus the zero-trip path,
- loop-carried accumulator state,
- descending `STEP -1`,
- single-entry prologue and main loops,
- loop-invariant work still leaving both loops,
- verifier-valid SSA after the rewrite,
- observable equivalence by rendering the transformed IR back to BASIC and running it,
- rejection of non-unit runtime steps.

`PipelineSoundnessTests` runs the whole middle end with verification after every pass, which is where
a malformed phi on a rewired edge shows up; production never verifies, so a broken edge would
otherwise survive silently until a later pass collapsed the function.

The implementation is derived from the language/IR semantics and the standard runtime-unrolling
model; LLVM's runtime unroller was used as a behavioral reference for remainder handling, not as
source code to copy.
