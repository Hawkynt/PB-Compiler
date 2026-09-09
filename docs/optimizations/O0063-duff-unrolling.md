# O0063 — Duff's-device unrolling (variable-trip loops)

| | |
|---|---|
| **Status** | ✅ Implemented for canonical unit-stride integer loops |
| **Stage** | IR middle-end |
| **IR** | `Ir/Passes/LoopUnroll.cs` — factor-4 runtime unrolling with modulo dispatch and SSA lane phis |
| **Related** | [O0007](O0007-loop-unrolling.md), [O0026](O0026-auto-vectorization.md), [O0066](O0066-unrolled-counter-propagation.md) |

## What it does

[O0007](O0007-loop-unrolling.md) fully unrolls loops whose trip count is a small compile-time
constant. O0063 handles the complementary canonical `FOR` shape when the count is only known at run
time: the middle-end emits four shared copies of the body and selects the first copy from
`tripCount MOD 4`. After the first partial group, complete groups enter at copy four and the original
loop condition is tested only once per four iterations.

This is Duff's device in target-neutral SSA CFG form. The x86 back end may lower the IR `switch` to a
jump table when profitable; other back ends are free to choose their own dispatch sequence.

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

Constant-trip loops remain O0007's responsibility. Non-unit modular steps deliberately remain scalar:
a step larger than one can jump across the comparison boundary, wrap, and become true again, so the
usual mathematical quotient is not necessarily PB's machine-level trip count.

## IR shape (factor 4)

Conceptually the transformed loop is:

```text
preheader
    |
    v
zero-trip guard ---- false ----> exit merge
    |
   true
    v
remainder = tripCount & 3
switch remainder
  0 -> lane4
  3 -> lane3
  2 -> lane2
  1 -> lane1

lane4 -> lane3 -> lane2 -> lane1 -> original header test
   ^                                      | true
   +--------------------------------------+
                                          | false
                                          v
                                      exit merge
```

Each lane begins with SSA phis. A lane reached directly from the remainder switch receives the
original loop state; the same lane reached from the previous copy receives that copy's carried state.
That is the SSA equivalent of Duff's shared case labels and is what keeps accumulators and other
loop-carried values correct, not just the induction variable.

## PowerBASIC semantics

The PB **increment-then-test counter end value** (QUIRK 2.28) is preserved exactly. The pass does not
replace the loop counter with a mathematical wide counter: every cloned increment remains in the
counter's original fixed width, including wrap.

For unit stride, the finite distance to the comparison boundary is exact. `tripCount MOD 4` is
computed in that same width; this is safe even at the signed edge because wrapping is modulo `2^bits`
and `2^bits` is divisible by four. A separate copy of the original header comparison guards the entry,
so a zero-trip loop still executes no body at all.

## Validation

`PowerBasic.Compiler.Tests/Ir/LoopUnrollTests.cs` covers:

- all four runtime remainders plus the zero-trip path,
- loop-carried accumulator state,
- descending `STEP -1`,
- verifier-valid SSA after the rewrite,
- observable equivalence by rendering the transformed IR back to BASIC and running it,
- rejection of non-unit runtime steps.

The implementation is derived from the language/IR semantics and the standard runtime-unrolling
model; LLVM's runtime unroller was used as a behavioral reference for remainder handling, not as
source code to copy.
