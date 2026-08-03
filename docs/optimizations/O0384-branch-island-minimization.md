# O0384 — Branch island minimization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0383](O0383-call-displacement-optimization.md), [O0382](O0382-post-layout-branch-relaxation.md), [O0363](O0363-interprocedural-block-placement.md) |

## The idea

When a branch cannot reach its target directly, the toolchain inserts a
**veneer** — a trampoline that jumps the rest of the way. Every veneer is extra
code, an extra transfer, and a cache line. A layout that keeps related code
within range avoids them.

On x86-16 the relevant limits are the ±127-byte short branch and the 64 KiB
segment; the aggressive block placement of
[O0363](O0363-interprocedural-block-placement.md) is precisely what can push
code out of range.

## What it needs

- Distance-aware placement, with the range limits from the target model.
- A veneer mechanism as the fallback, so that a layout the search could not
  perfect still links — the optimization is *minimizing* islands, not
  eliminating the need for them.
- Interaction with relaxation ([O0382](O0382-post-layout-branch-relaxation.md)):
  the two must converge together, since removing a veneer shortens the code and
  may bring another branch into range.
