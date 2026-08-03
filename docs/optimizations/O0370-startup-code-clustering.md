# O0370 — Startup code clustering

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0371](O0371-steady-state-clustering.md), [O0373](O0373-phase-aware-layout.md), [O0375](O0375-working-set-minimization.md) |

## The idea

Everything that runs **once, at startup** — argument parsing, table
initialization, file opening, mode setting — belongs on the minimum number of
pages, together, and away from the code that runs afterwards. Otherwise it is
scattered through the hot region, occupying cache lines and pages it will never
need again.

For a DOS program the equivalent benefit is load time and the code-fetch traffic
of the initialization itself.

## What it needs

- A phase signal: either a profile with timestamps
  ([O0362](O0362-temporal-function-clustering.md)) or the structural observation
  that a block is reachable only from the program's entry prologue and never
  from a loop.
- Placeable fragments ([O0360](O0360-basic-block-fragments.md)).
- It pairs with [O0371](O0371-steady-state-clustering.md): separating the two
  phases is one decision with two names.
