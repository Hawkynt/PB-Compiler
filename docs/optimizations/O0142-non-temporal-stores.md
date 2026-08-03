# O0142 — Non-temporal stores

| | |
|---|---|
| **Status** | ⬜ Planned (no such instruction before SSE; meaningless on the DOS-era targets) |
| **Stage** | Emitter |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0141](O0141-access-clustering.md), [O0174](O0174-target-cost-models.md) |

## The idea

A large streaming write — clearing a frame buffer, filling a big array — pushes
out every useful cache line to store data that will not be read again.
Non-temporal stores (`MOVNTQ`, `MOVNTDQ`, `MOVNTI`) bypass the cache for exactly
that case.

## Applies to

```basic
$CPU 80586 SSE
DIM big%(0 TO 32000), i%
FOR i% = 0 TO 32000
  big%(i%) = 0
NEXT
```

## Today

Recognized as a constant fill and lowered to `REP STOSW`
([O0020](O0020-idiom-replacement.md)) — or `REP STOSD` under
[C0001](C0001-386-codegen.md) — which is the right answer on every target the
compiler currently supports.

## Planned

On an SSE-capable target, a sufficiently large fill uses non-temporal stores plus
a fence, leaving the cache intact for the data the program actually rereads.

## What it needs

- A **size threshold** relative to the target's cache — below it, the ordinary
  store is better, so the transform is meaningless without
  [O0174](O0174-target-cost-models.md).
- The store-ordering fence (`SFENCE`), and the fact that DOSBox executes none of
  this, so it would be encoding-verified only — like the SSE/AVX paths in
  [R0004](R0004-asm-intrinsics.md).
- Honestly assessed, this is the lowest-value item in the memory family for a
  16-bit DOS compiler; it is listed because the target list may not stop at DOS.
