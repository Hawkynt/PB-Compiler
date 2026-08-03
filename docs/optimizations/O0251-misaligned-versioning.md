# O0251 — Misaligned access versioning

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0139](O0139-alignment-versioning.md), [O0130](O0130-trip-count-versioning.md), [O0026](O0026-auto-vectorization.md) |
| **Split from** | [O0139](O0139-alignment-versioning.md) |

## The idea

When alignment cannot be established statically, emit **two** paths and choose
at run time: an aligned fast path with wide or vector accesses, and an unaligned
(or scalar) fallback. One test on the pointer's low bits selects.

## Applies to

```basic
SUB Sum(a%(), BYVAL n%)      ' the array arrives BYREF: alignment unknown
  ...
END SUB
```

## What it needs

- The runtime test plus a code-size budget — two copies of every versioned loop.
- The **congruence** domain from [O0016](O0016-value-fact-analysis.md) to prove
  alignment statically wherever possible, so the versioning is only emitted when
  the fact is genuinely unavailable.
- Shares its machinery with trip-count and alias versioning
  ([O0130](O0130-trip-count-versioning.md),
  [O0152](O0152-vector-alias-versioning.md)) — one guard block, several
  conditions.
