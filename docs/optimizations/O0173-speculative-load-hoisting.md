# O0173 — Speculative load hoisting

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end / scheduler |
| **Related** | [O0140](O0140-load-store-motion.md), [O0060](O0060-memory-ssa.md), [O0152](O0152-vector-alias-versioning.md) |

## The idea

A load can be hoisted above a store — or out of a conditional — even when
non-aliasing cannot be *proved*, provided the compiler either:

- guards the motion with a runtime check (the pointer-range test of
  [O0152](O0152-vector-alias-versioning.md)), or
- proves the load itself cannot fault where it now runs.

The second condition is the interesting one on a 16-bit real-mode target:
a speculative read from a wrong segment does not fault the way it would under a
protected-mode OS — but it *can* read hardware-mapped memory, and under
`$ERROR BOUNDS` it can raise Error 9 where the original program would not have.

## Applies to

```basic
DIM i%, a%(0 TO 99), b%(0 TO 99), flag%
FOR i% = 0 TO 99
  IF flag% THEN
    b%(i%) = a%(i%) * 2      ' the load is conditional
  END IF
NEXT
```

## Today

The load stays inside the conditional, so it cannot be hoisted, pipelined or
vectorized.

## Planned

With `flag%` loop-invariant, [O0114](O0114-loop-unswitching.md) is the better
answer here; where the condition is *not* invariant, the load is hoisted under a
guard or a no-fault proof and the body becomes branch-free.

## What it needs

- A **fault model** for the target: which addresses can be read safely, and what
  `$ERROR BOUNDS` promises about when Error 9 is raised.
- The guard machinery shared with [O0152](O0152-vector-alias-versioning.md).
- A recovery story if the speculation is wrong — which on x86-16 means "do not
  speculate where it could be wrong", since there is no hardware recovery
  mechanism to lean on.
