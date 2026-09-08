# O0062 — Loop rotation, IV simplification and fusion

| | |
|---|---|
| **Status** | 🟡 Partial — `DO`/`FOR` rotation, affine integer IV simplification and adjacent-`FOR` fusion are done; wider-counter/runtime-step `FOR` rotation and general SCEV-style IV handling remain |
| **Stage** | Mid-end / emitter |
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

## Now — fusion

```basic
FOR i% = 0 TO 99
  a%(i%) = i%
  b%(i%) = a%(i%) * 2
NEXT
```

`OptLoopFusion` (a Tier-1 pre-pass, after `OptPruner` makes loops adjacent) merges
two adjacent `FOR` loops over the **same counter with identical bounds** whose
bodies are fusion-simple — only scalar or **counter-indexed**-array assignments,
no I/O, calls, control flow or non-counter subscripts. Because every array
subscript is exactly the counter, a value the first loop writes and the second
reads is the same element the fused iteration just produced, so the `b(i)=a(i)*2`
chain is legal; a run of such loops collapses into one (verified: three loops —
fill, derive, sum — fuse, oracle byte-identical). The **only** rejected shape is a
scalar carry — the second body reading a scalar the first writes, or the first
reading one the second writes — which also drops the non-associative
shared-accumulator case. A regression test confirms the merge fires and the carry
case does not. Runs under `--optimize` (the golden gate, being `--no-optimize`,
never fuses).

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
must have the same integer width, and the expression tree may contain only
`add`, `sub`, and multiplication with one constant side. Nonlinear expressions
such as `i*i`, casts, divisions, shifts, calls, memory-derived values and direct
phi-edge users decline. A cheap `i + constant` also stays as-is because replacing
one add with another add plus extra loop-carried state is not profitable. If the
affine scale wraps to zero, the derived value collapses to a constant instead.

The pass runs immediately before O0111 `phicong`. That ordering is deliberate:
O0062 creates derived recurrences; O0111 can then coalesce recurrences that are
equal or differ only by a constant offset instead of carrying redundant phis.

## `DO` and `FOR` rotation

`FOR` loops rotate the same way: the register-resident SI-counter path
(`TryEmitForCounterInRegister`, which claims most SPEED loops), its 386 `LONG`
sibling (`TryEmitForLongCounterInRegister`, counter in ESI), and the fast Int16
fallback (`EmitForInt16Fast`) all emit an entry guard plus a bottom test that
re-tests the just-incremented counter in place with the inverse condition
(`stop-if-past` → `continue-if-not-past`). The compare runs the same N+1 times and
the counter wraps identically, so the increment-then-test end value (QUIRK 2.28)
and every trip count are unchanged — verified byte-identical against the genuine
oracle over ascending / descending / zero-trip / `STEP` / negative-start and the
`BYTE`/`WORD` (unsigned, wrapping) counters; a regression test confirms the SI
counter is compared at both ends. The wider-counter (`LONG`/float, runtime-step)
`FOR` shapes still take the top-tested path.

## `DO` rotation

Under `$OPTIMIZE SPEED`, a pre-tested `DO WHILE`/`DO UNTIL … LOOP` (a pre-condition,
no post-condition) is emitted as one entry guard plus a bottom test:

```asm
    ; DO WHILE i < n
    <test; jump done if false>   ; entry guard, once
top:
    <body>
    <test; jump top if true>     ; bottom test - no per-iteration JMP
done:
```

`EmitDoLoopControl` (shared by the plain `EmitDoLoop` and the SI/DI
register-resident `TryEmitDoLoopInRegister`, so it fires for essentially every
`DO` loop) drops the unconditional `jmp top` each pass. The condition is evaluated
the **same N+1 times** — one entry, one after each body — so any side effect is
preserved exactly; only the jump disappears. A zero-trip loop is correctly skipped
by the entry guard. Verified byte-identical against the genuine oracle and
self-differential (rotated == the golden-faithful build) over `WHILE`, `UNTIL` and
zero-trip cases; a regression test confirms the bound is compared at both ends.

## Still planned

- **The remaining FOR shapes** — the memory-counter `LONG`/float paths and the
  runtime-step loops, whose multi-branch bound test (step-sign dispatch, 32-bit /
  x87 compares) would each need its inverse form at the bottom. The
  constant-step, register-resident Int16 and `LONG` counters (the common cases)
  already rotate.
- **General IV analysis** — the implemented IR slice handles constant-coefficient,
  same-width integer affine values of a canonical counted counter. Runtime
  coefficients, casted/mixed-width recurrences, non-constant starts/steps and
  general Scalar-Evolution equivalence remain the wider [O0110](O0110-general-induction-variables.md)
  problem.
- **Wider fusion** — the current pass rejects a subscript that is not exactly the
  counter (`a(i-1)`, `a(2*i)`) and any non-counter-indexed access, so a
  cross-iteration or affine-index dependence declines rather than being proven
  safe; loops with differing-but-compatible bounds (one a sub-range of the other)
  are also not yet handled.
