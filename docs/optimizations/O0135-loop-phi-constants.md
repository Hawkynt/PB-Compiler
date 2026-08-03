# O0135 — Loop-phi constants and backedge elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | SSA mid-end |
| **Related** | [O0017](O0017-sccp.md), [O0114](O0114-loop-unswitching.md), [O0002](O0002-dead-code-elimination.md) |

## The idea

Two facts a loop can hide:

1. **A loop-carried value that is actually constant.** `x = phi(5, x)` — the
   back-edge input is the phi itself, so the value never changes, but the phi's
   presence stops SCCP from lowering it. Detecting the self-reference makes it a
   constant everywhere in the loop.
2. **A back-edge condition that is provably always true or false.** Once loop
   invariants are known, a condition guarding the back edge may be decidable —
   turning the loop into a straight line, or proving it infinite (which is a
   diagnostic, not an optimization).

The second also cleans up after specialization: unswitching
([O0114](O0114-loop-unswitching.md)) and cloning leave loop-carried values that
became invariant in the clone.

## Applies to

```basic
DIM i%, base%, s%
base% = 5
FOR i% = 1 TO 100
  s% = s% + base%            ' base% is loop-invariant but flows through a phi
NEXT
```

## Today

`base%` reaches the loop body through a phi with two inputs (the entry value and
itself), so SCCP treats it as unknown inside the loop and the read stays a
memory access.

## Planned

The phi is recognized as trivial, `base%` folds to 5 inside the body, and
`s% = s% + 5` becomes an immediate add — after which the whole loop is an
arithmetic series ([O0134](O0134-recurrence-shortening.md)).

## What it needs

- Trivial-phi detection over the loop form — the IR tier already has it for the
  general case ([O0052](O0052-ir-simplify-cfg.md)); the AST-tier SSA does not.
- For the back-edge case, the invariant facts from
  [O0016](O0016-value-fact-analysis.md) evaluated at the loop header.
