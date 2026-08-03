# O0379 — Selective loop alignment

| | |
|---|---|
| **Status** | ⬜ Planned (refines [O0231](O0231-loop-top-alignment.md), which aligns every loop) |
| **Stage** | Emitter / layout |
| **Related** | [O0231](O0231-loop-top-alignment.md), [O0268](O0268-profile-collection.md), [O0374](O0374-hot-page-packing.md) |

## The idea

[O0231](O0231-loop-top-alignment.md) pads **every** loop top to 16 bytes under
`$CPU 80486` + `$OPTIMIZE SPEED`. Indiscriminate alignment wastes cache and
fetch bandwidth: a loop that runs three times pays the padding bytes forever and
gains nothing.

Aligning only **sufficiently hot** loops keeps the benefit and drops the cost.

## Applies to

```basic
FOR i% = 0 TO 2 : ... : NEXT      ' three iterations: do not pad
FOR i% = 0 TO 99999 : ... : NEXT  ' pad this one
```

## What it needs

- Loop trip counts ([O0268](O0268-profile-collection.md)), or the static
  estimate that a loop with a constant small bound is not worth padding — which
  is available **today**, without any profile, and would already improve on the
  blanket rule.
- The same output-invariance the current pad has: the pad is on the entry path
  and skipped by the back edge.
