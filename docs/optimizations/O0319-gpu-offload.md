# O0319 — Automatic GPU offload

| | |
|---|---|
| **Status** | ⬜ Planned — hosted back ends only (see [O0311](O0311-parallel-loop-versioning.md)) |
| **Stage** | Back end |
| **Related** | [O0311](O0311-parallel-loop-versioning.md), [O0172](O0172-loop-dependence-analysis.md), [docs/BACKENDS.md](../BACKENDS.md) |

## The idea

A large, regular, dependence-free loop nest over arrays is offloaded to a GPU,
including the transfer-cost analysis that decides whether the round trip is worth
it at all.

## Applies to

```basic
FOR y% = 0 TO 1079
  FOR x% = 0 TO 1919
    out&(y%, x%) = Blend&(a&(y%, x%), b&(y%, x%))
  NEXT
NEXT
```

— compiled through `--emit-llvm` for a modern host, not for DOS.

## What it needs

- Dependence and alias proofs at full strength
  ([O0172](O0172-loop-dependence-analysis.md)), a kernel-emission path in the
  LLVM back end, and a host runtime.
- **Transfer-cost analysis**: for anything short of a very large working set, the
  copy in and out dominates and the loop should stay on the CPU.
- Honest placement: this is an aspiration for the hosted back ends, not a DOS
  compiler feature — the same caveat as the rest of the parallel family, with the
  hardware distance at its maximum.
