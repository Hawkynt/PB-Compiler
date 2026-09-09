# O0311 — Parallel loop versioning

| | |
|---|---|
| **Status** | ✅ Implemented — conservative first slice; **hosted C/LLVM only**, explicit `--parallel-loops` opt-in |
| **Stage** | IR mid-end after mem2reg, before the ordinary hosted optimization pipeline |
| **Source** | `Ir/Passes/ParallelLoopVersioning.cs`, `runtime/pbc_parallel.c` |
| **Related** | [O0172](O0172-loop-dependence-analysis.md), [O0312](O0312-parallel-reduction.md), [docs/BACKENDS.md](../BACKENDS.md) |

## The idea

A sufficiently large loop whose iterations are provably independent can run
across worker threads, with a runtime decision on whether parallel execution is
worthwhile.

O0311 keeps the original loop intact as the sequential version. The preheader
calls `rt_parallel_should_run(trips)` and branches either to that original loop or
to a parallel path. The parallel path calls an outlined one-iteration helper
through `rt_parallel_for`; the hosted runtime distributes those iterations with
OpenMP when it is available.

The current profitability floor is 1024 iterations. `rt_parallel_should_run`
also requires more than one OpenMP worker, so a build without OpenMP always takes
the original sequential loop.

## Safety boundary

The transform only fires when all of these are true:

- [O0172](O0172-loop-dependence-analysis.md) returns a **complete** result and
  proves no loop-carried dependence;
- the counted loop has at least 1024 iterations and the simple straight-line
  header/body/latch shape;
- the induction counter is a signed 8-, 16- or 32-bit integer. Signed `i64`
  counters remain sequential in this first slice because `rt_parallel_for`
  reconstructs each logical counter value in `int64_t`; signed overflow there
  would be undefined C behavior, so O0311 does not speculate about it;
- the body contains at least one write, no calls, no integer/FP division or
  remainder, and no value escaping the iteration;
- the outlined iteration captures only constants and globals. Local/argument
  captures remain sequential until the IR has a target-independent closure
  representation;
- the loop counter itself is not observable after the loop.

Same-iteration dependences are fine: each outlined helper invocation preserves
that iteration's instruction order. Incomplete dependence information is not
interpreted optimistically.

## Hosted runtime and opt-in

Real-mode DOS is **single-tasking**: there are no threads, no OS scheduler and no
second core. The native `.EXE`/`.COM` pipeline never invokes this pass.

Hosted output opts in explicitly:

```bash
pbc --emit-c --parallel-loops PROG.BAS -O prog.c
cc -std=c99 -O2 -fopenmp -I runtime -o prog prog.c \
  runtime/pbc_rt.c runtime/pbc_parallel.c -lm
```

The LLVM path uses the same runtime ABI; compile/link `runtime/pbc_parallel.c`
with OpenMP alongside the emitted module. If `pbc_parallel.c` is built without
OpenMP it remains linkable, but `rt_parallel_should_run` always selects the
compiler-retained sequential version.

The callback is carried through a variadic runtime entry rather than converted
to `void *`. That is deliberate: C99 does not guarantee conversion between an
object pointer and a function pointer, while passing and retrieving the function
pointer through `...` preserves its actual pointer type.

## References

- LLVM `LoopVersioning`: runtime-checked loop cloning/version selection is the
  structural model for keeping an original loop and a guarded optimized version.
- GCC `-ftree-parallelize-loops=n`: parallelization is restricted to loops whose
  iterations can be executed independently and is only meaningful on a target
  with thread support.
- OpenMP 5.2 worksharing-loop semantics: logical iterations may execute
  concurrently and each logical iteration executes exactly once.

No implementation code was copied from LLVM, GCC or OpenMP. They were used as
behavioral/specification references; the pass and runtime are original code.
