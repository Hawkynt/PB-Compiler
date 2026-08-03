# O0252 — Safe over-read versioning

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0139](O0139-alignment-versioning.md), [O0137](O0137-load-widening.md), [O0154](O0154-swar-search.md) |
| **Split from** | [O0139](O0139-alignment-versioning.md) |

## The idea

A widened load past the last element is only permissible when there is provably
**accessible padding** behind the data — otherwise it is a bounds violation or,
at a segment boundary, a genuine fault. Where the padding exists, the tail
disappears; where it does not, a scalar tail runs.

This is the safety condition that makes SWAR string scanning
([O0154](O0154-swar-search.md)) and load widening
([O0137](O0137-load-widening.md)) legal rather than merely usual.

## Applies to

```basic
DIM s$, i%
FOR i% = 1 TO LEN(s$)        ' a 2- or 4-byte read of the last byte over-reads
  ...
NEXT
```

## What it needs

- Knowledge of each storage class's **padding guarantee**: a string descriptor's
  allocation granularity, a static array's trailing slack, the segment end.
- A fallback tail whenever the guarantee is absent, and an explicit statement of
  the invariant in the runtime, so the allocator cannot later tighten allocations
  and silently break it.
