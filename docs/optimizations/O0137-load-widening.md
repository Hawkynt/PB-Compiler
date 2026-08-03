# O0137 — Load widening across unrolled iterations

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0136](O0136-adjacent-access-merging.md), [O0007](O0007-loop-unrolling.md), [O0153](O0153-swar-arithmetic.md) |

## The idea

Load 32 or 64 bits **once**, then consume its lanes across several unrolled
iterations of a byte or word loop. Where the four operations can stay packed,
this is a genuine 4× on the memory side.

The warning attached to this idea is the important part: *"load four bytes and
run four bodies"* is **not** automatically faster. Extracting each lane back out
(`SHR`/`AND`/`MOV`) can cost more than the three loads it saved. It pays when
the lanes are processed **packed** — which is SWAR
([O0153](O0153-swar-arithmetic.md)) or SIMD
([O0026](O0026-auto-vectorization.md)), not scalar-with-extraction.

## Applies to

```basic
DIM i%, src AS STRING, total&
FOR i% = 1 TO LEN(src)
  total& = total& + ASC(MID$(src, i%, 1))
NEXT
```

## Today

One byte load per iteration.

## Planned (packed, not extracted)

```asm
    mov     eax, [si]        ; four bytes at once
    ; SWAR: accumulate all four lanes without extracting them
    mov     ebx, eax
    and     eax, 00FF00FFh
    shr     ebx, 8
    and     ebx, 00FF00FFh
    add     eax, ebx         ; two 16-bit partial sums
```

## What it needs

- **Alignment, bounds, aliasing, volatility and fault behavior** all have to
  permit the wide access — reading four bytes where the array has three is a
  fault or a bounds violation ([O0139](O0139-alignment-versioning.md) covers the
  versioning that makes it safe).
- A cost model that knows when extraction kills the win
  ([O0174](O0174-target-cost-models.md)).
- The packed consumption path: without
  [O0153](O0153-swar-arithmetic.md) or SIMD there is usually nothing to gain.
