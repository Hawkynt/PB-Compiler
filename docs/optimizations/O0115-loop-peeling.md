# O0115 — Loop peeling

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0007](O0007-loop-unrolling.md), [O0114](O0114-loop-unswitching.md), [O0139](O0139-alignment-versioning.md) |

## The idea

Some loop bodies contain a branch that is only ever taken on the **first**
iteration — an initialization test, a "previous value" guard, a first-element
special case. Peeling that iteration out of the loop lets the branch be resolved
at compile time in both copies: taken in the peeled prologue, provably not taken
in the remaining loop.

## Applies to

```basic
DIM i%, prev%, a%(0 TO 99), d%(0 TO 99)
FOR i% = 0 TO 99
  IF i% = 0 THEN
    d%(i%) = 0                  ' only on the first iteration
  ELSE
    d%(i%) = a%(i%) - a%(i% - 1)
  END IF
NEXT
```

## Today

100 tests of `i% = 0` for one true outcome, and a branchy body that blocks the
loop passes that want straight-line code.

## Planned

```basic
d%(0) = 0                       ' peeled
FOR i% = 1 TO 99
  d%(i%) = a%(i%) - a%(i% - 1)  ' branch-free
NEXT
```

## What it needs

- A first-iteration value analysis: the condition must be decidable for `i =
  lower bound` and provably false for every later value — which is
  [O0016](O0016-value-fact-analysis.md)'s counter range applied to the peeled
  and residual loops separately.
- The trip count must be provably ≥ 1 before peeling, or the peeled copy needs
  its own guard.
- It is also the enabling transform for **alignment peeling**
  ([O0139](O0139-alignment-versioning.md)), where the peeled iterations exist to
  bring a pointer to a vector boundary.
