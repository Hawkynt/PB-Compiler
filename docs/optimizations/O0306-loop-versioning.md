# O0306 — Loop versioning

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0304](O0304-guarded-specialization.md), [O0152](O0152-vector-alias-versioning.md), [O0130](O0130-trip-count-versioning.md), [O0117](O0117-bounds-check-merging.md) |

## The idea

Keep the fully general loop, and generate a second one with **no alias, bounds,
alignment or overflow checks** at all — entered only when a single guard block
proves all of those conditions up front.

This is the cleanest way to get check-free inner loops without weakening
`$ERROR` semantics: the checks still exist, they just all happen once, before
the loop, on the fast path.

## Applies to

```basic
$ERROR BOUNDS ON
SUB Scale(a%(), BYVAL n%, BYVAL k%)
  DIM i%
  FOR i% = 0 TO n%
    a%(i%) = a%(i%) * k%     ' a check per element today
  NEXT
END SUB
```

## Planned

```asm
    ; guard: 0 <= n < UBOUND(a) and no overflow possible for this k
    ja      GeneralLoop
    ...                      ; check-free, vectorizable loop
```

## What it needs

- The **combined guard**: one block proving every precondition, rather than four
  separate versionings ([O0304](O0304-guarded-specialization.md)).
- The general loop must remain byte-identical to today's output, so the
  fidelity bar is unaffected when the guard fails.
- Overflow guarding needs the range arithmetic of
  [O0219](O0219-overflow-check-elimination.md) applied to the *precondition*
  rather than to the operation.
