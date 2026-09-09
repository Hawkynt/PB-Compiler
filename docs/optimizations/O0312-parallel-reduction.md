# O0312 — Parallel reduction

| | |
|---|---|
| **Status** | 🟡 Partial — IR reduction analysis is implemented; hosted worker lowering waits for [O0311](O0311-parallel-loop-versioning.md) |
| **Stage** | Mid-end |
| **IR** | `Ir/Passes/ParallelReduction.cs` — `ParallelReduction.Analyze` |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/ParallelReductionTests.cs` |
| **Related** | [O0119](O0119-reduction-recognition.md), [O0120](O0120-multiple-accumulators.md), [O0311](O0311-parallel-loop-versioning.md), [O0317](O0317-false-sharing-avoidance.md) |

## The idea

A reduction is the one common loop-carried dependence that does not necessarily make a loop serial.
Instead of sharing one accumulator, each worker starts from the operator's identity, folds its own
slice, and the partial results are combined after the workers finish. Integer addition and
multiplication are associative modulo the IR width, as are `AND`, `OR` and `XOR`, so regrouping those
operations preserves the result exactly.

```basic
DIM i%, s&, a&(0 TO 999999)
FOR i% = 0 TO 999999
  s& = s& + a&(i%)
NEXT
```

## Implemented middle-end contract

`ParallelReduction.Analyze` recognizes the SSA shape
`phi(init, op(phi, input))` (including the commuted operand order) inside the repository's proven
single-backedge counted-loop shape. It returns an `IrParallelReduction` descriptor containing the
accumulator, update, per-iteration input, original initial value, operator identity, loop boundaries
and exact trip count.

The supported operators are integer `add`, `mul`, `and`, `or` and `xor`. The identities are `0`, `1`,
all-one bits, `0` and `0` respectively. The analysis declines when:

- the candidate is the induction variable rather than a reduction;
- the update is floating-point or uses a non-associative operator such as subtraction, division,
  remainder or shifts;
- the running accumulator is read by anything else inside the loop;
- the update result itself is observed inside the loop; or
- the nominal per-iteration input depends, directly or transitively, on the accumulator.

The last three checks are essential. A syntactic `acc = acc + x` is not privatizable if another
statement observes `acc`, or if `x` is actually derived from `acc` through another instruction.

## Deliberate boundary

This analysis does **not** make the surrounding loop parallel by itself. [O0311](O0311-parallel-loop-versioning.md)
still has to prove that the remaining memory accesses and loop-carried values are independent, choose
a profitable hosted execution path, and arrange worker scheduling. The hosted lowering can then seed
one private accumulator per worker with `Identity`, combine the partials with `Update.Op`, and apply
`InitialValue` exactly once. Storage for those private values must also respect
[O0317](O0317-false-sharing-avoidance.md) when workers share a cache-coherent machine.

Floating-point reductions remain excluded. Parallel regrouping changes rounding; enabling
reassociation elsewhere does not make this analysis silently opt a source program into different
numeric semantics.

## Reference model

The behavioral model follows the OpenMP reduction contract: each participating execution context gets
a private reduction copy initialized from an operator-specific identity and the private results are
combined at the end. The implementation here is original and uses the specification only as a
behavioral reference; no external implementation code or dependency is used.

- [OpenMP 5.2 — reduction clauses](https://www.openmp.org/spec-html/5.2/openmpsu52.html)
