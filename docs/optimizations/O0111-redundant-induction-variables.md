# O0111 — Redundant induction-variable elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0110](O0110-general-induction-variables.md), [O0112](O0112-countdown-loop.md), [O0027](O0027-copy-propagation.md) |

## The idea

When two loop-carried variables advance in lockstep, only one needs to exist:
the other is an affine function of it, or can be dropped entirely. The classic
case is a hand-written index and an offset maintained side by side, which is
exactly how DOS-era BASIC code walks two parallel arrays.

## Applies to

```basic
DIM i%, j%, a%(0 TO 99), b%(0 TO 99)
j% = 100
FOR i% = 0 TO 99
  a%(i%) = b%(j% - 100)      ' j% - 100 is always i%
  j% = j% + 1
NEXT
```

## Today

Both `i%` and `j%` are updated and stored every iteration, and both compete for
the one available index register.

## Planned

`j%` is recognized as `i% + 100`, its update is removed, and its uses are
rewritten — after which the loop has one induction variable and one free
register:

```basic
FOR i% = 0 TO 99
  a%(i%) = b%(i%)
NEXT
j% = 200                      ' the final value, materialized once
```

## What it needs

- IV classification from [O0110](O0110-general-induction-variables.md) plus a
  congruence test between the two evolutions.
- The **post-loop value** of the eliminated variable must be materialized where
  it is read after the loop — otherwise this changes observable behavior, since
  PB leaves loop variables live.
- Wrap correctness: two variables that agree mathematically may diverge once one
  of them wraps, so the equality needs the same
  [O0016](O0016-value-fact-analysis.md) type-range check every other fold uses.
