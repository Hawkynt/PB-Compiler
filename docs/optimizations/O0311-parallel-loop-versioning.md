# O0311 — Parallel loop versioning

| | |
|---|---|
| **Status** | ⬜ Planned — **not applicable to real-mode DOS**; relevant only to the C/LLVM back ends on a hosted target |
| **Stage** | Mid-end |
| **Related** | [O0172](O0172-loop-dependence-analysis.md), [O0312](O0312-parallel-reduction.md), [docs/BACKENDS.md](../BACKENDS.md) |

## The idea

A sufficiently large loop whose iterations are provably independent can run
across worker threads, with a runtime decision on the trip count.

## The caveat, stated plainly

Real-mode DOS is **single-tasking**: there are no threads, no OS scheduler and no
second core. Every entry in this parallelization family is therefore meaningless
for the `.EXE`/`.COM` targets that are this compiler's reason for existing.

They matter for exactly one reason: `--emit-c` and `--emit-llvm` produce code for
a *hosted* toolchain, where a PB program can be compiled for a modern machine.
There, an independent `FOR` loop over a large array is worth parallelizing like
any other.

## What it needs

- [O0172](O0172-loop-dependence-analysis.md) to prove independence.
- A threading runtime on the host side, and a trip-count threshold below which
  the sequential loop always wins.
- An explicit opt-in, because PB's semantics (including error handling and I/O
  ordering) assume sequential execution.
