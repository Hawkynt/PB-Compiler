# O0308 — Speculative overflow elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0219](O0219-overflow-check-elimination.md), [O0306](O0306-loop-versioning.md), [O0350](O0350-overflow-check-coalescing.md) |

## The idea

[O0219](O0219-overflow-check-elimination.md) drops a check only when the range
proof succeeds. When it does not, a **single guard** on the inputs can establish
the same fact for a whole region: prove once that the operands are small enough,
then run an unchecked — and therefore vectorizable — body.

## Applies to

```basic
$ERROR OVERFLOW ON
DIM i%, n%, a%(0 TO 999), b%(0 TO 999)
FOR i% = 0 TO n%
  a%(i%) = a%(i%) + b%(i%)   ' a JNO per element today; blocks vectorization
NEXT
```

## Planned

```asm
    ; guard: every element of a and b is within a range that cannot overflow
    ; -> unchecked, vectorized loop; else the checked scalar loop
```

## What it needs

- A guard that is **cheaper than the checks it removes** — proving a property of
  every element costs a scan, so this pays only when the loop body is large or
  runs many times, or when the bound comes from elsewhere (a declared range, a
  previous clamp).
- The fallback keeps the exact per-element trap behaviour, including *which*
  element raises first ([O0304](O0304-guarded-specialization.md)).
