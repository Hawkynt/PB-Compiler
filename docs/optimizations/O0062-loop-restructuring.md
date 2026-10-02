# O0062 — Loop rotation, IV simplification and fusion

| | |
|---|---|
| **Status** | 🟡 Partial — machine-level loop rotation and affine integer IV simplification are done; loop fusion is not implemented on the IR path; rotation of loops with a multi-instruction header test and general SCEV-style IV handling remain |
| **Stage** | IR middle end + x86 back end (instruction selection) |
| **Source** | `Ir/Passes/InductionVariableSimplification.cs`; `Backend/MachineLoopRotation.cs` (run from `Backend/InstructionSelector.cs` under `$OPTIMIZE SPEED`). No loop-fusion pass exists |
| **IR** | ✅ `Ir/Passes/InductionVariableSimplification.cs` — profitable same-width integer `a*i+b` values over a canonical counted loop become loop-carried recurrences; wired as `ivsimplify` immediately before `phicong` |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/InductionVariableSimplificationTests.cs` — affine recurrence, modulo-width wrap, nonlinear/cheap/phi-edge declines, and standard-pipeline verification |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0030](O0030-induction-variable-strength-reduction.md), [O0007](O0007-loop-unrolling.md), [O0111](O0111-redundant-induction-variables.md) |

## The idea

O0062 groups three classic loop transforms:

1. **Rotation** — a pre-test loop (`test; body; jmp test`) becomes
   `if !test goto end; do body while test`, which costs one branch per iteration
   instead of two and makes the body a single block that LICM and CSE can treat
   as straight-line.
2. **Induction-variable simplification** — derived induction variables
   (`j = 2*i + 3` inside the loop) are rewritten as their own incrementally
   updated variables, and redundant ones are coalesced.
3. **Fusion** — two adjacent loops with the same trip count over the same arrays
   merge into one, halving the loop overhead and improving locality.

## Applies to

```basic
DIM i%, a%(0 TO 99), b%(0 TO 99)
FOR i% = 0 TO 99 : a%(i%) = i% : NEXT
FOR i% = 0 TO 99 : b%(i%) = a%(i%) * 2 : NEXT
```

## Today

Two loops, 200 iterations of loop overhead, and `a%()` is walked twice.

## Fusion (retired)

```basic
FOR i% = 0 TO 99
  a%(i%) = i%
  b%(i%) = a%(i%) * 2
NEXT
```

Not implemented on the IR path; the syntax-level version was retired with the
direct emitter. The retired `OptLoopFusion` pre-pass merged
two adjacent `FOR` loops over the **same counter with identical bounds** whose
bodies are fusion-simple — only scalar or **counter-indexed**-array assignments,
no I/O, calls, control flow or non-counter subscripts. Because every array
subscript is exactly the counter, a value the first loop writes and the second
reads is the same element the fused iteration just produced, so the `b(i)=a(i)*2`
chain is legal; a run of such loops collapsed into one (three loops —
fill, derive, sum — fused, oracle byte-identical). The **only** rejected shape was a
scalar carry — the second body reading a scalar the first writes, or the first
reading one the second writes — which also drops the non-associative
shared-accumulator case.
It ran under `--optimize` only.

## IR induction-variable simplification

For a canonical counted SSA loop with

```text
i[n+1] = i[n] + step
```

an integer expression `f(i) = scale*i + offset` obeys

```text
f(i[n+1]) = f(i[n]) + scale*step
```

in the IR's fixed-width wrapping arithmetic. `InductionVariableSimplification`
uses that identity to replace profitable derived values such as `2*i+3` with a
header phi initialized once in the preheader and one constant add in the latch.
The multiply and address/value arithmetic inside the body then become dead and
are collected by the normal value passes.

The accepted slice is intentionally proof-friendly: the counter and constants
must have the same integer width, and the expression tree may contain `add`,
`sub`, multiplication with one constant side, and a constant left shift. The
left-shift case matters in the real pipeline: `InstCombine` lowers `i*2` to
`i<<1`, while `VerifiedArithmeticLowering` lowers factors such as three into a
shift plus add before O0062 runs. Both are still exactly affine modulo the integer
width. Nonlinear expressions such as `i*i`, casts, division/right shifts, calls,
memory-derived values and direct phi-edge users decline. A cheap `i + constant`
also stays as-is because replacing one add with another add plus extra
loop-carried state is not profitable. If the affine scale wraps to zero, the
derived value collapses to a constant instead.

The pass runs immediately before O0111 `phicong`. That ordering is deliberate:
O0062 creates derived recurrences; O0111 can then coalesce recurrences that are
equal or differ only by a constant offset instead of carrying redundant phis.

## `DO` and `FOR` rotation

Under `$OPTIMIZE SPEED`, `MachineLoopRotation` runs on the selected machine code
of every function. It matches a pre-tested loop whose header is exactly
`CMP; Jcc; JMP` with two predecessors and a single latch that ends in `JMP header`,
and copies that compare-and-branch suffix into the latch:

```asm
    ; DO WHILE i < n  /  FOR i% = 1 TO n
    <test; jump done if false>   ; header, now only the entry guard
top:
    <body>
    <test; jump top if true>     ; bottom test - no per-iteration JMP
done:
```

The latch's unconditional `jmp header` disappears. The condition is still
evaluated the **same N+1 times** — once on entry, once after each body — so any
side effect, the increment-then-test end value (QUIRK 2.28) and every trip count
are unchanged; a zero-trip loop is skipped by the header. A header with more than
that one compare (step-sign dispatch, 32-bit or x87 compares) is left top-tested.
Inline-assembly functions are not rotated.

## Still planned

- **The remaining loop shapes** — loops whose header test is more than one
  `CMP`/`Jcc` (runtime-step dispatch, 32-bit / x87 compares) would each need
  their multi-branch test duplicated at the bottom.
- **General IV analysis** — the implemented IR slice handles constant-coefficient,
  same-width integer affine values of a canonical counted counter. Runtime
  coefficients, casted/mixed-width recurrences, non-constant starts/steps and
  general Scalar-Evolution equivalence remain the wider [O0110](O0110-general-induction-variables.md)
  problem.
- **Fusion** — no IR pass; the retired pass rejected a subscript that is not exactly the
  counter (`a(i-1)`, `a(2*i)`) and any non-counter-indexed access, so a
  cross-iteration or affine-index dependence declines rather than being proven
  safe; loops with differing-but-compatible bounds (one a sub-range of the other)
  are also not yet handled.
