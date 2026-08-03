# O0123 — Loop distribution and fission

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0062](O0062-loop-restructuring.md) (fusion — the inverse), [O0026](O0026-auto-vectorization.md), [O0172](O0172-loop-dependence-analysis.md) |

## The idea

Splitting one loop into several is the inverse of fusion, and it pays for two
different reasons:

1. **Enabling vectorization** — a loop that mixes a vectorizable elementwise
   computation with something that is not (a call, a dependence-carrying update)
   vectorizes neither. Split apart, the clean half becomes eligible.
2. **Reducing register pressure** — a body with more live values than registers
   spills on every iteration; two smaller bodies may each fit.

## Applies to

```basic
DIM i%, a%(0 TO 999), b%(0 TO 999), c%(0 TO 999)
FOR i% = 0 TO 999
  c%(i%) = a%(i%) + b%(i%)      ' vectorizable
  CALL Log(c%(i%))              ' not
NEXT
```

## Today

Neither statement is optimized: the call blocks vectorization, pointer stepping
and register residency for the whole body.

## Planned

```basic
FOR i% = 0 TO 999 : c%(i%) = a%(i%) + b%(i%) : NEXT     ' now vectorizable
FOR i% = 0 TO 999 : CALL Log(c%(i%)) : NEXT
```

## What it needs

- **Dependence analysis** ([O0172](O0172-loop-dependence-analysis.md)):
  distribution is legal only if no dependence cycle spans the two halves, and
  only if reordering the *observable* effects is unobservable — a `PRINT` in one
  half and a computation in the other may not be separated if the computation
  can trap.
- Register-pressure estimation to decide the fission case
  ([O0176](O0176-register-pressure-scheduling.md)).
- The counter's post-loop value must come out the same, which is automatic when
  both loops keep the original bounds.
